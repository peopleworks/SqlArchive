using System.Text.Json;
using System.Text.Json.Serialization;
using SqlSchemaDiff.Models;

namespace SqlArchive.Core.Format;

/// <summary>
/// Everything about an archive that is not the archive: what wrote it, where it came
/// from, the whole schema, and per table what a restore and a verify need to know.
/// <para>
/// The schema is a <see cref="DatabaseSnapshot"/> - the very object SqlSchemaDiff.Core's
/// differ compares - rather than a model of our own. That is what makes
/// <c>restore-as-migration</c> and <c>verify</c> possible with no translation layer: one
/// side of the diff is read out of a file and the other out of a server.
/// </para>
/// </summary>
public sealed class ArchiveManifest
{
    /// <summary>
    /// Always <see cref="ArchiveFormat.FormatName"/>. dbdumper's manifest also declares a
    /// <c>formatVersion</c> of 1, and without something that says whose format this is,
    /// telling the two apart comes down to which properties happen to bind.
    /// </summary>
    public string Format { get; set; } = ArchiveFormat.FormatName;

    /// <summary>The shape of the archive, not the version of the tool. See <see cref="ArchiveFormat.CurrentVersion"/>.</summary>
    public int FormatVersion { get; set; } = ArchiveFormat.CurrentVersion;

    public ArchiveTool Tool { get; set; } = new();

    public DateTimeOffset CreatedAt { get; set; }

    public ArchiveSource Source { get; set; } = new();

    /// <summary>
    /// How the rows were read relative to each other. Written down because in two years
    /// nobody will remember which flags the export ran with, and the answer changes what
    /// a difference between two tables means.
    /// </summary>
    public ArchiveConsistency Consistency { get; set; } = ArchiveConsistency.PerTable;

    /// <summary>The full schema. Never null in an archive this build wrote.</summary>
    public DatabaseSnapshot? Schema { get; set; }

    public List<ArchiveTableEntry> Tables { get; set; } = [];

    /// <summary>
    /// SHA-256 of every entry in the archive that is not a table's data and not the
    /// manifest itself: the schema phases and the README. Keyed by entry name, values
    /// prefixed <c>sha256:</c>. Together with each table's
    /// <see cref="ArchiveTableEntry.FileHashes"/> this covers the whole archive exactly
    /// once, which is what lets "is this archive intact?" be answered without a server.
    /// </summary>
    public Dictionary<string, string> Files { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Whatever a newer writer put here that this build does not know about, kept so
    /// that <c>inspect</c> can show it and a rewrite does not throw it away.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extensions { get; set; }

    /// <summary>
    /// True when the file declares a format this build can describe but not act on. Set
    /// by the reader; not serialized.
    /// </summary>
    [JsonIgnore]
    public bool RequiresNewerReader => FormatVersion > ArchiveFormat.CurrentVersion;

    /// <summary>The table entry for a name, or null.</summary>
    public ArchiveTableEntry? Table(string schema, string name) =>
        Tables.FirstOrDefault(t =>
            string.Equals(t.Schema, schema, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The declared hash of one entry, whichever half of the manifest records it, or
    /// null when the manifest does not cover that entry at all.
    /// </summary>
    public string? HashOf(string entryName)
    {
        ArgumentException.ThrowIfNullOrEmpty(entryName);

        if(Files.TryGetValue(entryName, out var hash))
            return hash;

        foreach(var table in Tables)
        {
            if(table.FileHashes.TryGetValue(entryName, out var dataHash))
                return dataHash;
        }

        return null;
    }

    /// <summary>Every entry the manifest claims the archive contains, once each.</summary>
    public IEnumerable<string> DeclaredEntries()
    {
        foreach(var name in Files.Keys)
            yield return name;

        foreach(var table in Tables)
        {
            foreach(var name in table.DataFiles)
                yield return name;
        }
    }
}

/// <summary>What wrote the archive. Not the format version; the two move independently.</summary>
public sealed class ArchiveTool
{
    public string Name { get; set; } = "SqlArchive";

    public string Version { get; set; } = string.Empty;
}

/// <summary>Where the archive came from, for the person reading it years later.</summary>
public sealed class ArchiveSource
{
    public string Server { get; set; } = string.Empty;

    public string Database { get; set; } = string.Empty;

    /// <summary><c>SERVERPROPERTY('Edition')</c>.</summary>
    public string? Edition { get; set; }

    /// <summary><c>SERVERPROPERTY('ProductVersion')</c>.</summary>
    public string? ProductVersion { get; set; }

    /// <summary>The database's collation, not the connection's.</summary>
    public string? Collation { get; set; }
}

/// <summary>How the rows of different tables relate to each other in time.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ArchiveConsistency>))]
public enum ArchiveConsistency
{
    /// <summary>
    /// Each table read on its own. The default, because a single point in time costs
    /// permissions and space that many servers do not have, and demanding it would make
    /// the tool useless in the common case.
    /// </summary>
    [JsonStringEnumMemberName("per-table")]
    PerTable,

    /// <summary>Read from a database snapshot, so every table is the same instant. Keeps the parallelism.</summary>
    [JsonStringEnumMemberName("snapshot")]
    Snapshot,

    /// <summary>
    /// Read through one connection under SNAPSHOT isolation. The same instant, at the
    /// cost of the parallelism.
    /// </summary>
    [JsonStringEnumMemberName("snapshot-isolation")]
    SnapshotIsolation
}

/// <summary>
/// One table: how many rows, what they hash to, which entries hold them, and whether
/// what is here is all of it.
/// </summary>
public sealed class ArchiveTableEntry
{
    public string Schema { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public long RowCount { get; set; }

    /// <summary>
    /// The table's content hash, lowercase hex, no prefix - unlike the file hashes,
    /// which carry <c>sha256:</c>. The difference is deliberate: this is not the SHA-256
    /// of anything, it is the sum of one SHA-256 per row. See <see cref="RowHash"/>.
    /// <para>
    /// Null when <see cref="DataSkipped"/> is set, because there is nothing to hash.
    /// </para>
    /// </summary>
    public string? RowHash { get; set; }

    /// <summary>The entry names holding this table's rows, in range order.</summary>
    public List<string> DataFiles { get; set; } = [];

    /// <summary>SHA-256 of each of <see cref="DataFiles"/>, prefixed <c>sha256:</c>.</summary>
    public Dictionary<string, string> FileHashes { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The <c>--where</c> the table was exported with, or null for all of it. In the
    /// manifest rather than in a note beside it because a partial archive that does not
    /// say it is partial makes <c>verify</c> report differences that are not differences.
    /// </summary>
    public string? RowFilter { get; set; }

    /// <summary>True when the schema was archived and the rows deliberately were not.</summary>
    public bool DataSkipped { get; set; }

    /// <summary>
    /// Columns the table has and the archive does not carry, with the reason: computed,
    /// rowversion, GENERATED ALWAYS. Written so that someone reading the JSONL by hand
    /// and finding fewer columns than the CREATE TABLE has an answer without reading
    /// this source.
    /// </summary>
    public Dictionary<string, string> OmittedColumns { get; set; } = new(StringComparer.Ordinal);

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extensions { get; set; }

    /// <summary>The two-part name, for messages.</summary>
    [JsonIgnore]
    public string Identifier => $"[{Schema}].[{Name}]";
}
