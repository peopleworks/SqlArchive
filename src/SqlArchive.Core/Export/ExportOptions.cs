using System.IO.Compression;

namespace SqlArchive.Core.Export;

/// <summary>
/// Everything an export needs to know that is not the destination file: where to read
/// from, which tables, how hard to push, and whether a single point in time is being
/// asked for.
/// </summary>
public sealed class ExportOptions
{
    /// <summary>The database to read. Pointed at the database itself, not at <c>master</c>.</summary>
    public required string ConnectionString { get; init; }

    /// <summary>
    /// What goes into <c>tool.version</c> in the manifest. Defaults to this assembly's
    /// informational version, which is what a build produces on its own.
    /// </summary>
    public string? ToolVersion { get; init; }

    /// <summary>
    /// Globs of tables to archive; empty means all of them. A pattern with no dot is
    /// matched against the bare table name, so <c>Customer</c> finds <c>[dbo].[Customer]</c>;
    /// one with a dot is matched against <c>schema.name</c>, so <c>sales.*</c> takes a
    /// schema. <c>*</c> and <c>?</c> are the wildcards, and matching ignores case.
    /// <para>
    /// A table left out by these is <b>not in the archive at all</b> - not its rows and
    /// not its <c>CREATE TABLE</c>. That is different from <see cref="ExcludeData"/>, and
    /// the difference is what the manifest's <c>dataSkipped</c> is for.
    /// </para>
    /// </summary>
    public IList<string> IncludeTables { get; init; } = [];

    /// <summary>Globs of tables to leave out entirely. Applied after <see cref="IncludeTables"/>, and wins.</summary>
    public IList<string> ExcludeTables { get; init; } = [];

    /// <summary>
    /// Globs of tables whose schema is archived and whose rows deliberately are not.
    /// These appear in the manifest with <c>dataSkipped</c> set, because a partial
    /// archive that does not say it is partial makes a later <c>verify</c> report
    /// differences that are not differences.
    /// </summary>
    public IList<string> ExcludeData { get; init; } = [];

    /// <summary>
    /// A <c>WHERE</c> clause per table, keyed by the same kind of glob. The first entry
    /// whose pattern matches a table wins, so order the specific before the general. The
    /// text is recorded in the manifest's <c>rowFilter</c>.
    /// </summary>
    /// <remarks>
    /// The clause is the operator's SQL and is passed to the server as written. A filter
    /// that is not deterministic - one that calls <c>GETDATE()</c>, say - makes a table
    /// read in ranges non-repeatable, because each range evaluates it again.
    /// </remarks>
    public IList<KeyValuePair<string, string>> RowFilters { get; init; } = [];

    /// <summary>
    /// Ask for a single point in time. See <see cref="ConsistencySession"/> for the three
    /// steps and for why the third one refuses rather than falling back.
    /// </summary>
    public bool Consistent { get; init; }

    /// <summary>
    /// How many table ranges are read at once. Forced to one when the export is running
    /// through a single connection under <c>SNAPSHOT</c> isolation, which is what that
    /// mode costs.
    /// </summary>
    public int Parallelism { get; init; } = Math.Min(Environment.ProcessorCount, 8);

    /// <summary>Whether a large table is split into ranges. See <see cref="ExportRanges"/>.</summary>
    public ExportRanges Ranges { get; init; } = ExportRanges.Auto;

    /// <summary>
    /// A table with fewer rows than this is read in one pass whatever else is set. The
    /// count is the catalog's estimate, which costs no scan; it decides how many files
    /// are written and never which rows go in them.
    /// <para>
    /// Null, the default, means one range's worth - <see cref="RowsPerRange"/>. So asking
    /// for ranges of a thousand rows splits a table of five thousand into five, which is
    /// what asking for that plainly means. Set it to hold a floor independent of the
    /// range size: "aim for a hundred thousand rows a file, and do not bother splitting a
    /// table under five million" is the two of them together.
    /// </para>
    /// </summary>
    public long? MinimumRowsToSplit { get; init; }

    /// <summary>Roughly how many rows a range should hold, which is what decides how many there are.</summary>
    public long RowsPerRange { get; init; } = 1_000_000;

    /// <summary>The floor in force, which is <see cref="RowsPerRange"/> unless one was named.</summary>
    public long SplitThreshold => MinimumRowsToSplit ?? RowsPerRange;

    /// <summary>The ceiling on ranges per table, so a huge table does not become a thousand entries.</summary>
    public int MaxRangesPerTable { get; init; } = 16;

    /// <summary>
    /// Compare each table's written row count against a <c>COUNT(*)</c> from the server
    /// and fail the export if they disagree.
    /// <para>
    /// Null, the default, means "whenever the answer is meaningful": under a database
    /// snapshot or <c>SNAPSHOT</c> isolation the count and the read see the same instant,
    /// so a disagreement is a real fault and worth an extra pass over an index. Read
    /// table by table it is not - a row inserted between the read and the count would
    /// fail an export that did nothing wrong - so it is off unless asked for.
    /// </para>
    /// </summary>
    public bool? VerifyRowCounts { get; init; }

    /// <summary>
    /// Where the rows are spooled before they are packed. Defaults to the archive path
    /// with <c>.work</c> appended.
    /// </summary>
    /// <remarks>
    /// The spool is not optional, and the reason is in <c>ArchiveWriter</c>: a zip being
    /// created holds one entry open at a time, so several tables cannot stream into it at
    /// once. Rows therefore land on disk first and are packed afterwards, which costs a
    /// second write of the data and buys both the parallelism and the resume.
    /// </remarks>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// Keep the spool when the export fails, and pick it up on the next run. Off by
    /// default, because a spool left behind by a failure nobody is going to retry is just
    /// a copy of the database on the disk.
    /// </summary>
    public bool Resumable { get; init; }

    /// <summary>Seconds a single query may take. Zero, the default, is no limit, which is what a large table needs.</summary>
    public int CommandTimeoutSeconds { get; init; }

    /// <summary>
    /// How hard the zip is compressed. <c>SmallestSize</c> is many times slower on JSONL
    /// for a few percent, which is a bad trade on a table of a hundred million rows.
    /// </summary>
    public CompressionLevel Compression { get; init; } = CompressionLevel.Optimal;

    public IProgress<ExportProgress>? Progress { get; init; }

    /// <summary>
    /// Called once the point in time has been established and before a single row has
    /// been read.
    /// <para>
    /// It exists so that the consistency modes can be tested for what they actually do
    /// rather than for what they report: a test writes to the source here and then
    /// asserts that the archive does not contain the write. An operator can use it to log
    /// which mode was reached.
    /// </para>
    /// </summary>
    public Func<ArchiveConsistencyEstablished, CancellationToken, Task>? OnConsistencyEstablished { get; init; }
}

/// <summary>Whether, and when, a table is read in several ranges at once.</summary>
public enum ExportRanges
{
    /// <summary>
    /// Split a table when it is big enough to be worth it and there is a column to split
    /// by. What "big enough" means is <see cref="ExportOptions.SplitThreshold"/>.
    /// </summary>
    Auto,

    /// <summary>One entry per table, read in one pass. Always correct, and slower on a large table.</summary>
    Off,

    /// <summary>
    /// Split whenever there is a column to split by, however few rows there are. For
    /// tests: it is the only way to exercise the range code on a table small enough for a
    /// test to check every row of.
    /// </summary>
    Always
}

/// <summary>What the consistency negotiation settled on, handed to <see cref="ExportOptions.OnConsistencyEstablished"/>.</summary>
/// <param name="Consistency">The mode reached, which is also what goes in the manifest.</param>
/// <param name="ReadConnectionString">
/// What the rows will be read through. Under a database snapshot this points at the
/// snapshot and not at the source, which is the whole point of it.
/// </param>
/// <param name="SnapshotDatabase">The database snapshot's name, when one was created, so a caller can say so.</param>
public sealed record ArchiveConsistencyEstablished(
    Format.ArchiveConsistency Consistency,
    string ReadConnectionString,
    string? SnapshotDatabase);

/// <summary>Where an export has got to. Reported often enough to tell a slow export from a stuck one.</summary>
/// <param name="Phase">What is happening: reading the schema, reading rows, packing.</param>
/// <param name="Table">The table being read, when one is.</param>
/// <param name="Rows">Rows written so far, across every table.</param>
/// <param name="UnitsDone">Table ranges finished.</param>
/// <param name="UnitsTotal">Table ranges planned.</param>
/// <param name="Elapsed">Since the export started.</param>
public sealed record ExportProgress(
    string Phase,
    string? Table,
    long Rows,
    int UnitsDone,
    int UnitsTotal,
    TimeSpan Elapsed);
