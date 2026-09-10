using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.SqlClient;
using SqlArchive.Core.Format;

namespace SqlArchive.Core.Import;

/// <summary>
/// What an interrupted restore has already done, so the next run does the rest.
/// <para>
/// Not a spool. The export's working directory holds rows, because the rows only exist
/// in it until the archive is packed; here the rows are already on disk in the archive
/// and what has to survive a crash is a much smaller thing - which tables are published
/// and which halves of the schema have been run. So this is a directory of empty
/// markers, and it is written after the work rather than before it.
/// </para>
/// <para>
/// A restore is resumable at all because every table is published atomically on its own:
/// a run killed in the middle of one leaves that table holding exactly what it held
/// before, so redoing it is not a repair, it is just doing it. The journal is what makes
/// redoing it unnecessary, not what makes it safe.
/// </para>
/// </summary>
public sealed class ImportJournal
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    private ImportJournal(string directory)
    {
        Directory = directory;
        Units = System.IO.Path.Combine(directory, "units");
    }

    /// <summary>The working directory itself.</summary>
    public string Directory { get; }

    /// <summary>Where the one marker per finished unit lives.</summary>
    public string Units { get; }

    /// <summary>True when this run picked up an existing journal rather than starting one.</summary>
    public bool Resumed { get; private set; }

    /// <summary>
    /// Opens the working directory for a restore, checking or writing the fingerprint.
    /// </summary>
    /// <exception cref="ImportResumeException">
    /// When a journal is there, resuming was asked for, and it was left by a restore with
    /// different parameters. Carrying on from it would leave a destination holding half
    /// of one archive and half of another, with nothing anywhere saying so.
    /// </exception>
    public static ImportJournal Open(string directory, ImportFingerprint fingerprint, bool resumable)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        ArgumentNullException.ThrowIfNull(fingerprint);

        var journal = new ImportJournal(directory);
        var path = System.IO.Path.Combine(directory, "fingerprint.json");

        if(!resumable && System.IO.Directory.Exists(directory))
        {
            // Whatever is here belongs to somebody else's half-finished run and must not
            // be mistaken for this one's - but this deletes a directory tree that a
            // caller named. One with things in it that no restore put there is refused
            // rather than emptied.
            if(!File.Exists(path) && System.IO.Directory.EnumerateFileSystemEntries(directory).Any())
            {
                throw new ImportException(
                    $"'{directory}' is not empty and was not left behind by a restore - it has no " +
                    "fingerprint.json. Point --work-dir somewhere else; the restore would have deleted " +
                    "everything in it.");
            }

            System.IO.Directory.Delete(directory, recursive: true);
        }

        System.IO.Directory.CreateDirectory(journal.Units);

        if(resumable && File.Exists(path))
        {
            var existing = JsonSerializer.Deserialize<ImportFingerprint>(File.ReadAllText(path), Json)
                ?? throw new ImportResumeException($"The journal at '{directory}' has an unreadable fingerprint.");

            var difference = existing.Difference(fingerprint);

            if(difference is not null)
                throw new ImportResumeException($"The journal at '{directory}' was left by a different restore: {difference}");

            journal.Resumed = true;
            return journal;
        }

        File.WriteAllText(path, JsonSerializer.Serialize(fingerprint, Json));
        return journal;
    }

    /// <summary>True when this unit was finished by this run or an earlier one.</summary>
    public bool Done(string unit) => File.Exists(MarkerPath(unit));

    /// <summary>
    /// Records that a unit is finished. Written after the work and in one call, so a run
    /// killed halfway leaves work with no marker - which is done again - and never a
    /// marker for work that was not finished.
    /// </summary>
    public void Complete(string unit, string detail) =>
        File.WriteAllText(MarkerPath(unit), detail);

    /// <summary>Removes the working directory. Called when the restore is done, and when a failed run is not being kept.</summary>
    public void Delete()
    {
        if(System.IO.Directory.Exists(Directory))
            System.IO.Directory.Delete(Directory, recursive: true);
    }

    /// <summary>
    /// Which of the two routes to the archive's shape this restore took, or null when it
    /// has not decided yet.
    /// </summary>
    /// <remarks>
    /// Recorded before the work rather than after it, unlike everything else here, and
    /// deliberately: it is a decision and not a step. A restore that runs the archive's
    /// phases makes the destination non-empty the moment it creates the first table, so a
    /// resume that decided again would look at a destination full of tables, choose the
    /// migration route, and try to add the indexes twice - once by diff and once by phase
    /// 050. The decision has to survive the crash that the tables it created will.
    /// </remarks>
    public string? Route
    {
        get
        {
            var path = System.IO.Path.Combine(Directory, "route.txt");
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }

        set => File.WriteAllText(System.IO.Path.Combine(Directory, "route.txt"), value ?? string.Empty);
    }

    /// <summary>The unit name for one table.</summary>
    /// <remarks>
    /// The two parts are escaped separately, and only then joined by a dot. Joining them
    /// first would make <c>[dbo].[a.b]</c> and <c>[dbo.a].[b]</c> the same unit, so
    /// finishing one would mark the other done - which is the same reason the archive's
    /// entry names escape the dot, and it is why the escape used here is that one.
    /// </remarks>
    public static string TableUnit(string schema, string name) =>
        $"table:{ArchiveFormat.EscapeIdentifier(schema)}.{ArchiveFormat.EscapeIdentifier(name)}";

    /// <summary>The unit name for one half of the schema.</summary>
    public static string SchemaUnit(string half) => $"schema:{half}";

    /// <summary>
    /// A marker's file name. Percent-encoded through the archive format's own escape, so
    /// that a table whose name contains a dot, a slash or a colon cannot land on the same
    /// file as another one.
    /// </summary>
    private string MarkerPath(string unit) =>
        System.IO.Path.Combine(Units, ArchiveFormat.EscapeIdentifier(unit) + ".done");
}

/// <summary>
/// What a restore was, reduced to something a later run can compare itself against: the
/// archive it was putting back, the database it was putting it into, and which parts of
/// it were being restored.
/// </summary>
public sealed class ImportFingerprint
{
    /// <summary>The shape of archive being read. A different one is a different file.</summary>
    public int FormatVersion { get; set; } = ArchiveFormat.CurrentVersion;

    /// <summary>
    /// The build that wrote the journal.
    /// </summary>
    /// <remarks>
    /// Here for the same reason it is in the export's fingerprint, read the other way
    /// round: the decoder belongs to the build, and two of them finishing one restore
    /// would put back rows that two different definitions of equality had been applied
    /// to. The guard would still catch it table by table - which is the point of the
    /// guard - and it is cheaper to refuse the resume.
    /// </remarks>
    public string Tool { get; set; } = string.Empty;

    /// <summary>The database being restored into, in the clear, so a refusal can name it.</summary>
    public string Database { get; set; } = string.Empty;

    /// <summary>The server, in the clear, for the same reason.</summary>
    public string Server { get; set; } = string.Empty;

    /// <summary>A hash of the destination's identity - server, database, and the login if there is one.</summary>
    /// <remarks>
    /// A hash and not the string, because a connection string carries a password and this
    /// file sits on disk. The identity rather than the whole string, so a rotated password
    /// does not throw away a journal that is still about the same database, while pointing
    /// at another server does.
    /// </remarks>
    public string Destination { get; set; } = string.Empty;

    /// <summary>
    /// A hash of what the archive is: when it was made, where from, and every table's
    /// name, row count and row hash.
    /// </summary>
    /// <remarks>
    /// The content and not the path. Two archives of the same database taken an hour
    /// apart have the same file name as often as not, and resuming one restore into the
    /// other is exactly the mixture this exists to refuse; while the same archive moved
    /// to another directory is still the same archive.
    /// </remarks>
    public string Archive { get; set; } = string.Empty;

    /// <summary>A hash of what is being restored: the mode and the table globs.</summary>
    public string Selection { get; set; } = string.Empty;

    /// <summary>Reduces the archive and the options to a fingerprint.</summary>
    public static ImportFingerprint Of(ArchiveManifest manifest, ImportOptions options, string toolVersion)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(options);

        var builder = new SqlConnectionStringBuilder(options.ConnectionString);

        var destination = string.Join(
            Separator,
            builder.DataSource.ToUpperInvariant(),
            builder.InitialCatalog.ToUpperInvariant(),
            builder.IntegratedSecurity ? "sspi" : builder.UserID.ToUpperInvariant());

        var archive = new StringBuilder()
            .Append("format=").Append(manifest.Format).Append(Separator)
            .Append("version=").Append(manifest.FormatVersion.ToString(CultureInfo.InvariantCulture)).Append(Separator)
            .Append("created=").Append(manifest.CreatedAt.ToString("O", CultureInfo.InvariantCulture)).Append(Separator)
            .Append("source=").Append(manifest.Source.Server).Append('/').Append(manifest.Source.Database).Append(Separator);

        foreach(var table in manifest.Tables)
        {
            archive.Append(table.Identifier).Append('=')
                .Append(table.RowCount.ToString(CultureInfo.InvariantCulture)).Append('/')
                .Append(table.RowHash ?? "-").Append(Separator);
        }

        var selection = new StringBuilder()
            .Append("mode=").Append(options.Mode).Append(Separator)
            .Append("include=").AppendJoin(',', options.IncludeTables).Append(Separator)
            .Append("exclude=").AppendJoin(',', options.ExcludeTables)
            .ToString();

        return new ImportFingerprint
        {
            Tool = toolVersion,
            Server = builder.DataSource,
            Database = builder.InitialCatalog,
            Destination = Hash(destination),
            Archive = Hash(archive.ToString()),
            Selection = Hash(selection)
        };
    }

    /// <summary>The first thing that differs, in words, or null when the two match.</summary>
    public string? Difference(ImportFingerprint other)
    {
        ArgumentNullException.ThrowIfNull(other);

        if(FormatVersion != other.FormatVersion)
            return $"it was written for format version {FormatVersion} and this is version {other.FormatVersion}.";

        if(!string.Equals(Tool, other.Tool, StringComparison.Ordinal))
            return $"it was written by SqlArchive {Tool} and this is {other.Tool}; the encoding belongs to the " +
                   "build, so two of them must not finish one restore between them.";

        if(!string.Equals(Destination, other.Destination, StringComparison.Ordinal))
            return $"it was restoring into [{Database}] on {Server} and this run restores into " +
                   $"[{other.Database}] on {other.Server}.";

        if(!string.Equals(Archive, other.Archive, StringComparison.Ordinal))
            return "it was putting back a different archive - the tables, their row counts or their hashes are " +
                   "not the ones it started with.";

        if(!string.Equals(Selection, other.Selection, StringComparison.Ordinal))
            return "the mode or the table filters are not the ones it was started with.";

        return null;
    }

    /// <summary>
    /// The unit separator, U+001F, between the parts of a fingerprint's input. A character
    /// no identifier contains, so two different selections cannot run together into one
    /// string that happens to read the same.
    /// </summary>
    private const char Separator = '';

    private static string Hash(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
