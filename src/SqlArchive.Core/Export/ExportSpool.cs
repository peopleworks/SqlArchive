using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.SqlClient;
using SqlArchive.Core.Format;

namespace SqlArchive.Core.Export;

/// <summary>
/// The working directory an export writes rows into before they are packed, and what
/// makes an interrupted export resumable.
/// <para>
/// The spool is not a choice. A zip being created holds one entry open at a time - see
/// <c>ArchiveWriter</c> - so several tables cannot stream into the archive at once. Rows
/// therefore land in one file per range here and the archive is assembled from them
/// afterwards. That costs a second write of the data on the way out, and it buys both
/// the parallelism and, for nothing extra, the resume.
/// </para>
/// </summary>
public sealed class ExportSpool
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    private ExportSpool(string directory)
    {
        Directory = directory;
        Units = Path.Combine(directory, "units");
    }

    /// <summary>The working directory itself.</summary>
    public string Directory { get; }

    /// <summary>Where the one file per range lives.</summary>
    public string Units { get; }

    /// <summary>True when this run picked up an existing spool rather than starting one.</summary>
    public bool Resumed { get; private set; }

    /// <summary>
    /// Opens the working directory for an export, checking or writing the fingerprint.
    /// </summary>
    /// <exception cref="ExportResumeException">
    /// When a spool is there, resuming was asked for, and it was made by an export with
    /// different parameters. Mixing two of those would give an archive whose tables were
    /// read from two different places, or with two different filters, and nothing in the
    /// file would say so.
    /// </exception>
    public static ExportSpool Open(string directory, ExportFingerprint fingerprint, bool resumable)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        ArgumentNullException.ThrowIfNull(fingerprint);

        var spool = new ExportSpool(directory);
        var path = Path.Combine(directory, "fingerprint.json");

        if(!resumable && System.IO.Directory.Exists(directory))
        {
            // Not a resume, so whatever is here is somebody else's half-finished run and
            // must not be mistaken for this one's - but this deletes a directory tree, and
            // the working directory is something a caller can name. A directory with
            // things in it that an export did not put there is refused rather than
            // emptied.
            if(!File.Exists(path) && System.IO.Directory.EnumerateFileSystemEntries(directory).Any())
            {
                throw new ExportException(
                    $"'{directory}' is not empty and was not left behind by an export - it has no " +
                    "fingerprint.json. Point --spool somewhere else; the export would have deleted " +
                    "everything in it.");
            }

            System.IO.Directory.Delete(directory, recursive: true);
        }

        System.IO.Directory.CreateDirectory(spool.Units);

        if(resumable && File.Exists(path))
        {
            var existing = JsonSerializer.Deserialize<ExportFingerprint>(File.ReadAllText(path), Json)
                ?? throw new ExportResumeException($"The spool at '{directory}' has an unreadable fingerprint.");

            var difference = existing.Difference(fingerprint);

            if(difference is not null)
                throw new ExportResumeException($"The spool at '{directory}' was made by a different export: {difference}");

            spool.Resumed = true;
            return spool;
        }

        File.WriteAllText(path, JsonSerializer.Serialize(fingerprint, Json));
        return spool;
    }

    /// <summary>
    /// The plan as it was decided on the first run, or the one just made, written down.
    /// A resumed export uses what is on disk and never what it would have planned today.
    /// </summary>
    public async Task<ExportPlan> LoadOrSavePlanAsync(Func<Task<ExportPlan>> make)
    {
        ArgumentNullException.ThrowIfNull(make);

        var path = Path.Combine(Directory, "plan.json");

        if(Resumed && File.Exists(path))
        {
            return JsonSerializer.Deserialize<ExportPlan>(await File.ReadAllTextAsync(path).ConfigureAwait(false), Json)
                ?? throw new ExportResumeException($"The spool at '{Directory}' has an unreadable plan.");
        }

        var plan = await make().ConfigureAwait(false);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(plan, Json)).ConfigureAwait(false);
        return plan;
    }

    /// <summary>What a unit already produced, or null when it has not finished.</summary>
    public ExportUnitResult? Completed(ExportUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);

        var path = Path.Combine(Units, unit.DoneName);
        if(!File.Exists(path))
            return null;

        var result = JsonSerializer.Deserialize<ExportUnitResult>(File.ReadAllText(path), Json);
        if(result is null)
            return null;

        var rows = Path.Combine(Units, unit.SpoolName);

        // The marker without the rows is not a completed unit. It happens when a spool is
        // copied, or half deleted by hand, and taking the marker's word for it would seal
        // a manifest that describes a file that is not there.
        if(!File.Exists(rows) || new FileInfo(rows).Length != result.Bytes)
            return null;

        return result;
    }

    /// <summary>The path a unit's rows go to.</summary>
    public string RowsPath(ExportUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return Path.Combine(Units, unit.SpoolName);
    }

    /// <summary>Records what a unit produced, after its rows are safely on disk.</summary>
    public void Complete(ExportUnit unit, ExportUnitResult result)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(result);

        // The marker is written last and in one call, so a run killed in the middle of a
        // unit leaves rows without a marker - which is redone - and never a marker
        // without the rows it describes.
        File.WriteAllText(Path.Combine(Units, unit.DoneName), JsonSerializer.Serialize(result, Json));
    }

    /// <summary>Removes the working directory. Called when the archive is written, and when a failed run is not being kept.</summary>
    public void Delete()
    {
        if(System.IO.Directory.Exists(Directory))
            System.IO.Directory.Delete(Directory, recursive: true);
    }
}

/// <summary>
/// What an export was, reduced to something a later run can compare itself against:
/// where it read from, what it selected, and what shape of archive it was writing.
/// <para>
/// Resuming with different parameters has to be an error rather than a merge. Half a
/// table filtered one way and half filtered another is an archive nothing in the format
/// can describe, and the manifest would report it as whole.
/// </para>
/// </summary>
public sealed class ExportFingerprint
{
    /// <summary>The shape of archive being written. A different one is a different file.</summary>
    public int FormatVersion { get; set; } = ArchiveFormat.CurrentVersion;

    /// <summary>
    /// The build that wrote the spool.
    /// </summary>
    /// <remarks>
    /// Not asked for by the work package, and here because the encoding belongs to the
    /// build: two versions of the encoder writing halves of one table would produce an
    /// archive with two definitions of equality in it, and the row hash of each half
    /// would be right about its own half and wrong about the table.
    /// </remarks>
    public string Tool { get; set; } = string.Empty;

    /// <summary>The server, in the clear, so that a refusal can name it.</summary>
    public string Server { get; set; } = string.Empty;

    /// <summary>The database, in the clear, for the same reason.</summary>
    public string Database { get; set; } = string.Empty;

    /// <summary>
    /// A hash of the connection's identity - server, database, and the login if there is
    /// one.
    /// </summary>
    /// <remarks>
    /// A hash and not the string, because a connection string carries a password and this
    /// file sits on disk beside the data. And the identity rather than the whole string,
    /// so that a rotated password or a changed <c>Application Name</c> does not throw
    /// away a spool that is still about the same database, while pointing at another
    /// server does.
    /// </remarks>
    public string Connection { get; set; } = string.Empty;

    /// <summary>A hash of everything that decides which rows are read: the globs, the filters, the range settings.</summary>
    public string Selection { get; set; } = string.Empty;

    /// <summary>Reduces the options to a fingerprint.</summary>
    public static ExportFingerprint Of(ExportOptions options, string toolVersion)
    {
        ArgumentNullException.ThrowIfNull(options);

        var builder = new SqlConnectionStringBuilder(options.ConnectionString);

        var identity = string.Join(
            Separator,
            builder.DataSource.ToUpperInvariant(),
            builder.InitialCatalog.ToUpperInvariant(),
            builder.IntegratedSecurity ? "sspi" : builder.UserID.ToUpperInvariant());

        var selection = new StringBuilder()
            .Append("include=").AppendJoin(',', options.IncludeTables).Append(Separator)
            .Append("exclude=").AppendJoin(',', options.ExcludeTables).Append(Separator)
            .Append("nodata=").AppendJoin(',', options.ExcludeData).Append(Separator)
            .Append("where=").AppendJoin(',', options.RowFilters.Select(f => $"{f.Key}=>{f.Value}")).Append(Separator)
            .Append("consistent=").Append(options.Consistent).Append(Separator)
            .Append("ranges=").Append(options.Ranges).Append(Separator)
            .Append("split=").Append(options.SplitThreshold.ToString(CultureInfo.InvariantCulture)).Append(Separator)
            .Append("perRange=").Append(options.RowsPerRange.ToString(CultureInfo.InvariantCulture)).Append(Separator)
            .Append("maxRanges=").Append(options.MaxRangesPerTable.ToString(CultureInfo.InvariantCulture))
            .ToString();

        return new ExportFingerprint
        {
            Tool = toolVersion,
            Server = builder.DataSource,
            Database = builder.InitialCatalog,
            Connection = Hash(identity),
            Selection = Hash(selection)
        };
    }

    /// <summary>The first thing that differs, in words, or null when the two match.</summary>
    public string? Difference(ExportFingerprint other)
    {
        ArgumentNullException.ThrowIfNull(other);

        if(FormatVersion != other.FormatVersion)
            return $"it was written for format version {FormatVersion} and this is version {other.FormatVersion}.";

        if(!string.Equals(Tool, other.Tool, StringComparison.Ordinal))
            return $"it was written by SqlArchive {Tool} and this is {other.Tool}; the encoding belongs to the " +
                   "build, so two of them must not write halves of one table.";

        if(!string.Equals(Connection, other.Connection, StringComparison.Ordinal))
            return $"it read [{Database}] on {Server} and this run reads [{other.Database}] on {other.Server}.";

        if(!string.Equals(Selection, other.Selection, StringComparison.Ordinal))
            return "the table filters, the row filters or the range settings are not the ones it was made with.";

        return null;
    }

    /// <summary>
    /// The unit separator, U+001F, between the parts of a fingerprint's input. A
    /// character no identifier and no WHERE clause contains, so two different selections
    /// cannot be run together into one string that happens to read the same.
    /// </summary>
    private const char Separator = '\u001F';

    private static string Hash(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}

/// <summary>A spool that cannot be picked up by this run.</summary>
public sealed class ExportResumeException : ExportException
{
    public ExportResumeException(string message) : base(message) { }
}
