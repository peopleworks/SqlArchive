namespace SqlArchive.Core.Import;

/// <summary>
/// Everything a restore needs to know that is not the archive: where it is going, which
/// tables, how much of the archive is being put back, and how hard to push.
/// </summary>
public sealed class ImportOptions
{
    /// <summary>The database to restore into. Pointed at the database itself, not at <c>master</c>.</summary>
    /// <remarks>
    /// It does not have to be empty and it does not have to be new. That is the whole
    /// point of <see cref="ImportMode.Migrate"/>, and it is why this takes a database
    /// rather than creating one: creating it would mean choosing a collation, a
    /// filegroup layout and a recovery model on the operator's behalf, and every one of
    /// those is a decision the archive does not carry.
    /// </remarks>
    public required string ConnectionString { get; init; }

    /// <summary>Which of the three restores this is. See <see cref="ImportMode"/>.</summary>
    public ImportMode Mode { get; init; } = ImportMode.Migrate;

    /// <summary>
    /// What goes into the journal's fingerprint as the build that wrote it. Defaults to
    /// this assembly's informational version.
    /// </summary>
    public string? ToolVersion { get; init; }

    /// <summary>
    /// Globs of tables whose rows are published; empty means all of them. Matched the
    /// same way <see cref="Export.TableGlob"/> matches on the way out.
    /// <para>
    /// These select <b>rows</b> and never schema. The schema phases are the archive's own
    /// files, run whole, because they are written to be run by hand and a file half
    /// executed is not something the archive can describe. A table left out here is
    /// created and left empty, and the summary says so.
    /// </para>
    /// </summary>
    public IList<string> IncludeTables { get; init; } = [];

    /// <summary>Globs of tables whose rows are not published. Applied after <see cref="IncludeTables"/>, and wins.</summary>
    public IList<string> ExcludeTables { get; init; } = [];

    /// <summary>How many tables are staged and published at once.</summary>
    public int Parallelism { get; init; } = Math.Min(Environment.ProcessorCount, 8);

    /// <summary>Rows per <c>SqlBulkCopy</c> batch. Also how often progress moves.</summary>
    public int BatchSize { get; init; } = 10_000;

    /// <summary>Seconds a single statement may take. Zero, the default, is no limit, which is what a large table needs.</summary>
    public int CommandTimeoutSeconds { get; init; }

    /// <summary>
    /// Where the journal that makes a restore resumable is kept. Defaults to the archive
    /// path with <c>.restore</c> appended.
    /// </summary>
    /// <remarks>
    /// Unlike the export's spool this holds no rows - the rows are already on disk, in
    /// the archive. It holds only what has been done, which is what a resume needs and
    /// all it needs.
    /// </remarks>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// Keep the journal, and pick it up on the next run. Off by default: a journal left
    /// behind by a restore nobody is going to retry is a directory of empty files that
    /// will silently skip work on the day somebody does retry.
    /// </summary>
    public bool Resumable { get; init; }

    /// <summary>
    /// Say what would be altered and what would be published, and write nothing.
    /// <para>
    /// The schema diff is real - the destination is read and compared - because a
    /// dry run whose answer came from guessing at the destination would be answering a
    /// different question from the one the next run asks.
    /// </para>
    /// </summary>
    public bool DryRun { get; init; }

    /// <summary>
    /// Carry on with the remaining tables when one fails or is refused.
    /// <para>
    /// Safe by construction rather than by care: every table is published in one
    /// transaction of its own, so the ones that succeeded are whole and the ones that
    /// failed were never touched. There is no half-loaded state for this flag to leave
    /// behind.
    /// </para>
    /// </summary>
    public bool ContinueOnError { get; init; }

    /// <summary>
    /// Re-validate the foreign keys this restore had to switch off, and fail when one of
    /// them no longer holds.
    /// <para>
    /// True by default. A restore that leaves the destination's keys untrusted has
    /// published data nobody has checked, and the check is a scan of tables that were
    /// just written and are still in memory.
    /// </para>
    /// </summary>
    public bool RevalidateForeignKeys { get; init; } = true;

    public IProgress<ImportProgress>? Progress { get; init; }
}

/// <summary>The three restores. They are answers to different questions, not settings of one.</summary>
public enum ImportMode
{
    /// <summary>
    /// The default, and the one the whole design hangs off: the destination is brought
    /// to the archive's shape by a diff rather than by a drop, so a restore over a
    /// database that already holds rows keeps the rows the diff can keep.
    /// </summary>
    Migrate,

    /// <summary>Run the archive's schema phases in order and stop. Not a row is read.</summary>
    SchemaOnly,

    /// <summary>
    /// Publish the rows into a destination that already has the shape. The shape is
    /// checked rather than assumed, and a mismatch names the table and the column.
    /// </summary>
    DataOnly
}

/// <summary>Where a restore has got to. Reported often enough to tell a slow restore from a stuck one.</summary>
/// <param name="Phase">What is happening: reading the archive, the schema, the data, the finalize.</param>
/// <param name="Table">The table being published, when one is.</param>
/// <param name="Rows">Rows loaded so far, across every table.</param>
/// <param name="UnitsDone">Tables finished - published, refused or skipped.</param>
/// <param name="UnitsTotal">Tables planned.</param>
/// <param name="Elapsed">Since the restore started.</param>
public sealed record ImportProgress(
    string Phase,
    string? Table,
    long Rows,
    int UnitsDone,
    int UnitsTotal,
    TimeSpan Elapsed);
