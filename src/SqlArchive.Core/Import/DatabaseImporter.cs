using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Microsoft.Data.SqlClient;
using SqlArchive.Core.Export;
using SqlArchive.Core.Format;
using SqlSchemaDiff.Models;
using SqlSchemaDiff.Services;
using SyncJob.Core.Publication;

namespace SqlArchive.Core.Import;

/// <summary>
/// Puts an archive back into a database.
/// <para>
/// The shape of the file is not this class's to decide either - it reads what
/// <c>DatabaseExporter</c> wrote, through the same <c>Format</c> namespace. What is
/// decided here is everything about the writing: whether the destination is brought to
/// the archive's shape by a diff or by the archive's own phases, in what order the two
/// halves of the schema go relative to the rows, and which tables are allowed to reach
/// the destination at all.
/// </para>
/// <para>
/// Nothing is held whole in memory at any point, for the same reason as on the way out:
/// a table's rows go from the zip entry through the decoder into <c>SqlBulkCopy</c>, so a
/// table larger than the machine costs the same resident memory as a small one.
/// </para>
/// <para>
/// <b>Two routes, and the destination chooses.</b> A destination with no tables in it is
/// restored by running the archive's own phases with the rows in the middle - bare
/// tables, rows, then the keys, indexes and foreign keys - which is the shape the archive
/// was written for and the reason the composer separates them. A destination that already
/// holds tables is a migration: its schema is read, diffed against the archive's, and
/// altered where that preserves rows. Diffing an empty database would work too and would
/// be worse - every table would be created with its keys and indexes attached, so every
/// row would load through an index that need not exist yet, and a foreign key would stand
/// between two tables before either had a row in it.
/// </para>
/// </summary>
public sealed class DatabaseImporter
{
    /// <summary>The archive's own phases run around the data. What an empty destination gets.</summary>
    private const string PhaseRoute = "phases";

    /// <summary>The diff applied before the data and the finalize phase after it. What a destination that already holds tables gets.</summary>
    private const string MigrationRoute = "diff";

    private readonly ImportOptions _options;

    public DatabaseImporter(ImportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrEmpty(options.ConnectionString);

        _options = options;
    }

    /// <summary>Restores the archive, and answers what it did.</summary>
    /// <param name="archivePath">The archive to read.</param>
    /// <param name="cancellationToken">Cancellation. A cancelled restore leaves its journal behind when it is resumable.</param>
    public async Task<ImportResult> ImportAsync(string archivePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(archivePath);

        var stopwatch = Stopwatch.StartNew();
        var notices = new List<string>();
        var toolVersion = _options.ToolVersion ?? ToolVersion();

        Report("opening", null, 0, 0, 0, stopwatch);

        using var archive = await ArchiveReader.OpenAsync(archivePath, cancellationToken).ConfigureAwait(false);
        var manifest = archive.Manifest;

        Refuse(manifest, notices);

        // A dry run writes nothing anywhere, journal included: the whole promise is that
        // running it changes nothing, and a working directory left on disk is a change
        // somebody has to notice and clean up.
        var journal = _options.DryRun
            ? null
            : ImportJournal.Open(
                _options.WorkingDirectory ?? archivePath + ".restore",
                ImportFingerprint.Of(manifest, _options, toolVersion),
                _options.Resumable);

        if(journal?.Resumed == true)
            notices.Add($"Resumed from the journal at '{journal.Directory}'; what an earlier run finished was not done again.");

        var succeeded = false;

        try
        {
            var plan = Plan(manifest, notices);
            var batches = 0;

            if(_options.Mode == ImportMode.SchemaOnly)
            {
                var (before, after) = SchemaScript.Phases(archive);

                batches += await RunPhasesAsync(archive, journal, "before", before, stopwatch, cancellationToken).ConfigureAwait(false);
                batches += await RunPhasesAsync(archive, journal, "after", after, stopwatch, cancellationToken).ConfigureAwait(false);

                succeeded = true;

                return Finish(archivePath, manifest, plan.Select(Nothing).ToList(), batches, notices, stopwatch);
            }

            var afterData = Array.Empty<string>().AsEnumerable();

            if(_options.Mode == ImportMode.Migrate)
            {
                var shape = await PrepareShapeAsync(archive, journal, manifest, notices, stopwatch, cancellationToken)
                    .ConfigureAwait(false);

                batches += shape.Batches;
                afterData = shape.AfterData;
            }

            var results = await LoadAsync(archivePath, journal, plan, notices, stopwatch, cancellationToken).ConfigureAwait(false);

            if(results.Any(r => r.Outcome is ImportTableOutcome.Refused or ImportTableOutcome.Failed))
            {
                notices.Add(
                    "Some tables were not published, so the statements that run after the data may fail against " +
                    "rows that are not there - a foreign key pointing at a table that was refused is the usual " +
                    "one. Every refusal is listed above with its reason; the destination holds what it held " +
                    "before for each of them.");
            }

            Report("finalize", null, results.Sum(r => r.Rows), results.Count, results.Count, stopwatch);

            batches += await RunPhasesAsync(archive, journal, "after", afterData, stopwatch, cancellationToken)
                .ConfigureAwait(false);

            succeeded = true;

            return Finish(archivePath, manifest, results, batches, notices, stopwatch);
        }
        finally
        {
            // A journal is kept only when somebody asked to be able to come back to it.
            // On a run that did not, one left behind is a directory of markers that will
            // silently skip work the day somebody passes --resume.
            if(journal is not null && (succeeded || !_options.Resumable))
                journal.Delete();
        }
    }

    /// <summary>
    /// Refuses an archive whose data this build has no business putting back.
    /// </summary>
    /// <remarks>
    /// <c>FORMAT.md</c> is explicit that a manifest from a newer writer is not an error to
    /// <i>parse</i> - the point of an archive is to be opened by whoever is there when the
    /// source is not - and that refusing happens where it matters, which is here. The
    /// schema phases are still SQL and still run, so <c>--schema-only</c> is allowed
    /// through with a notice.
    /// </remarks>
    private void Refuse(ArchiveManifest manifest, IList<string> notices)
    {
        if(manifest.RequiresNewerReader)
        {
            if(_options.Mode != ImportMode.SchemaOnly)
            {
                throw new ImportException(
                    $"The archive declares format version {manifest.FormatVersion.ToString(CultureInfo.InvariantCulture)} " +
                    $"and this build reads version {ArchiveFormat.CurrentVersion.ToString(CultureInfo.InvariantCulture)}. " +
                    "Its rows are refused rather than decoded on the assumption that nothing about the encoding " +
                    "changed, because a value read by the wrong rules is indistinguishable from one read by the " +
                    "right ones. Its schema phases are plain SQL and --schema-only will still run them.");
            }

            notices.Add(
                $"The archive is format version {manifest.FormatVersion.ToString(CultureInfo.InvariantCulture)} and " +
                "this build reads version " +
                $"{ArchiveFormat.CurrentVersion.ToString(CultureInfo.InvariantCulture)}. The schema phases are " +
                "being run because they are plain SQL; its rows would not be.");
        }

        if(manifest.Schema is null && _options.Mode != ImportMode.SchemaOnly)
        {
            throw new ImportException(
                "The manifest carries no schema snapshot, so there is no column list to decode the rows against " +
                "and nothing to diff the destination with. The archive can still be inspected.");
        }
    }

    /// <summary>
    /// Decides what happens to every table in the manifest before anything is done to any
    /// of them.
    /// </summary>
    private List<PlannedTable> Plan(ArchiveManifest manifest, IList<string> notices)
    {
        var snapshot = manifest.Schema;

        var models = snapshot is null
            ? new Dictionary<string, TableModel>(StringComparer.OrdinalIgnoreCase)
            : SnapshotSelection.Tables(snapshot)
                .ToDictionary(t => $"{t.Schema}.{t.Name}", StringComparer.OrdinalIgnoreCase);

        var plan = new List<PlannedTable>(manifest.Tables.Count);

        foreach(var entry in manifest.Tables)
        {
            if(!Keep(entry.Schema, entry.Name))
            {
                plan.Add(new PlannedTable(entry, null, "left out by --table or --exclude"));
                continue;
            }

            if(entry.DataSkipped)
            {
                plan.Add(new PlannedTable(entry, null, "the archive carries this table's schema and, deliberately, none of its rows"));
                continue;
            }

            if(!models.TryGetValue($"{entry.Schema}.{entry.Name}", out var model))
            {
                plan.Add(new PlannedTable(entry, null, "the manifest lists the table and its schema snapshot does not describe it, so there is no column list to decode its rows against"));
                continue;
            }

            var columns = ArchiveColumns.For(model, snapshot!.Types);

            if(columns.Count == 0)
            {
                plan.Add(new PlannedTable(entry, null, "every column of it is computed, a rowversion or a period column, so the archive carries no value for any of them"));
                continue;
            }

            plan.Add(new PlannedTable(entry, columns, null));
        }

        var filtered = plan.Count(p => p.Reason is not null);

        if(filtered > 0)
        {
            notices.Add(
                $"{Plural(filtered, "table")} of {Plural(plan.Count, "table")} in the archive will not have rows " +
                "published. Each one is listed in the summary with the reason.");
        }

        return plan;
    }

    /// <summary>
    /// Brings the destination to the archive's shape, by the route the destination itself
    /// chooses, and answers how many statements that took.
    /// </summary>
    private async Task<(int Batches, IReadOnlyList<string> AfterData)> PrepareShapeAsync(
        ArchiveReader archive,
        ImportJournal? journal,
        ArchiveManifest manifest,
        IList<string> notices,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        Report("schema", null, 0, 0, 0, stopwatch);

        var (beforeData, afterData) = SchemaScript.Phases(archive);
        var route = journal?.Route;

        if(route is null)
        {
            var extractor = new SqlServerSchemaExtractor();
            var current = await extractor.ExtractAsync(_options.ConnectionString, cancellationToken).ConfigureAwait(false);

            route = SnapshotSelection.Tables(current).Any() ? MigrationRoute : PhaseRoute;

            if(journal is not null)
                journal.Route = route;

            if(route == PhaseRoute)
            {
                notices.Add(
                    "The destination holds no tables, so the archive's own schema phases are run around the data - " +
                    "bare tables, then the rows, then the keys, indexes and foreign keys. That is the shape the " +
                    "archive was written for, and it is why a restore needs no load order: the foreign keys are " +
                    "not there while the tables are being filled.");
            }
            else
            {
                return (await DiffAsync(journal, manifest, current, notices, cancellationToken).ConfigureAwait(false),
                        SchemaScript.Finalize(archive));
            }
        }

        if(route == PhaseRoute)
        {
            var batches = await RunPhasesAsync(archive, journal, "before", beforeData, stopwatch, cancellationToken)
                .ConfigureAwait(false);

            return (batches, afterData);
        }

        // Resuming a migration whose diff was applied by the earlier run. Extracting the
        // destination again to diff it again would compare the archive against a database
        // that has already been altered to match it, which is a lot of work for an empty
        // script - and the journal has already established this is the same archive and
        // the same destination.
        if(journal?.Done(ImportJournal.SchemaUnit("diff")) == true)
        {
            notices.Add("The schema diff had already been applied by an earlier run and was not applied again.");
            return (0, SchemaScript.Finalize(archive));
        }

        var destination = await new SqlServerSchemaExtractor()
            .ExtractAsync(_options.ConnectionString, cancellationToken).ConfigureAwait(false);

        return (await DiffAsync(journal, manifest, destination, notices, cancellationToken).ConfigureAwait(false),
                SchemaScript.Finalize(archive));
    }

    /// <summary>Brings an existing destination to the archive's shape by diff, and answers how many statements that took.</summary>
    private async Task<int> DiffAsync(
        ImportJournal? journal,
        ArchiveManifest manifest,
        DatabaseSnapshot destination,
        IList<string> notices,
        CancellationToken cancellationToken)
    {
        // The archive is the desired state and the destination is current, which is the
        // way round that makes this a restore rather than a capture. Drops are off: a
        // table the destination has and the archive does not is somebody's, and a restore
        // that removed it would be doing something nobody asked for. A rebuild is on,
        // because the rebuild is SQLDiff's row-preserving one - that is the whole reason
        // this is a migration and not a drop.
        var diff = new SchemaDiffer().Diff(
            manifest.Schema!,
            destination,
            includeDrops: false,
            includeTableDrops: false,
            allowTableRebuild: true,
            addOnly: false);

        notices.Add(
            $"The destination already holds {Plural(SnapshotSelection.Tables(destination).Count(), "table")}, so " +
            $"this is a migration: {diff.Added.ToString(CultureInfo.InvariantCulture)} object(s) created, " +
            $"{diff.Changed.ToString(CultureInfo.InvariantCulture)} altered, and nothing dropped. Objects the " +
            "destination has and the archive does not are left alone.");

        if(!diff.HasChanges)
            return 0;

        var batches = SqlBatchSplitter.Split(diff.Script)
            .Select((sql, i) => new SchemaBatch("the schema diff", i, sql))
            .ToList();

        if(_options.DryRun)
            return batches.Count;

        var executed = await SchemaScript
            .ExecuteAsync(_options.ConnectionString, batches, _options.CommandTimeoutSeconds, cancellationToken)
            .ConfigureAwait(false);

        journal?.Complete(ImportJournal.SchemaUnit("diff"), diff.Script);
        return executed;
    }

    /// <summary>Runs one half of the archive's phases, unless an earlier run already did.</summary>
    private async Task<int> RunPhasesAsync(
        ArchiveReader archive,
        ImportJournal? journal,
        string half,
        IEnumerable<string> entries,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        var names = entries.ToList();

        if(names.Count == 0)
            return 0;

        var unit = ImportJournal.SchemaUnit(half);

        if(journal?.Done(unit) == true)
            return 0;

        Report("schema", null, 0, 0, 0, stopwatch);

        var batches = await SchemaScript.ReadAsync(archive, names, cancellationToken).ConfigureAwait(false);

        if(_options.DryRun)
            return batches.Count;

        var executed = await SchemaScript
            .ExecuteAsync(_options.ConnectionString, batches, _options.CommandTimeoutSeconds, cancellationToken)
            .ConfigureAwait(false);

        journal?.Complete(unit, string.Join(Environment.NewLine, names));
        return executed;
    }

    /// <summary>
    /// Publishes every table that is going to be published, with the destination's
    /// foreign keys switched off for the length of it.
    /// </summary>
    private async Task<List<ImportTableResult>> LoadAsync(
        string archivePath,
        ImportJournal? journal,
        List<PlannedTable> plan,
        IList<string> notices,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        var results = new ConcurrentDictionary<string, ImportTableResult>(StringComparer.Ordinal);

        foreach(var skipped in plan.Where(p => p.Columns is null))
            results[skipped.Key] = new ImportTableResult(skipped.Entry.Schema, skipped.Entry.Name, ImportTableOutcome.Skipped, 0, Reason: skipped.Reason);

        var work = new List<PlannedTable>();

        foreach(var table in plan.Where(p => p.Columns is not null))
        {
            if(journal?.Done(ImportJournal.TableUnit(table.Entry.Schema, table.Entry.Name)) == true)
            {
                // Not re-published rather than published again. Either would leave the
                // right rows in the destination - a replace is a replace - but reading a
                // table out of the archive twice is the cost the journal exists to avoid.
                results[table.Key] = new ImportTableResult(
                    table.Entry.Schema,
                    table.Entry.Name,
                    ImportTableOutcome.Skipped,
                    table.Entry.RowCount,
                    Reason: "an earlier run published it");

                continue;
            }

            work.Add(table);
        }

        if(work.Count == 0)
            return Ordered(plan, results);

        await using var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var published = work.Select(p => p.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var fence = await ForeignKeyFence
            .LowerAsync(connection, published, _options.DryRun, _options.CommandTimeoutSeconds, cancellationToken)
            .ConfigureAwait(false);

        if(fence.Lowered.Count > 0)
        {
            notices.Add(
                $"{Plural(fence.Lowered.Count, "foreign key")} of the destination " +
                (_options.DryRun ? "would be" : "were") +
                " switched off for the data phase and put back afterwards: every table here is replaced whole, " +
                "and SQL Server refuses to empty a table another key points at whichever way you empty it. They " +
                "are re-validated on the way back, which is the first moment at which validating them means " +
                "anything - the rows on both sides are the archive's.");
        }

        try
        {
            if(_options.DryRun)
            {
                foreach(var table in work)
                {
                    results[table.Key] = new ImportTableResult(
                        table.Entry.Schema, table.Entry.Name, ImportTableOutcome.WouldPublish, table.Entry.RowCount);
                }

                return Ordered(plan, results);
            }

            var fenced = fence.Lowered
                .SelectMany(k => new[] { $"{k.ParentSchema}.{k.ParentName}", $"{k.ReferencedSchema}.{k.ReferencedName}" })
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            await PublishAllAsync(archivePath, journal, work, fenced, results, stopwatch, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            if(!_options.DryRun)
            {
                var problems = await fence
                    .RaiseAsync(connection, _options.RevalidateForeignKeys, _options.CommandTimeoutSeconds, CancellationToken.None)
                    .ConfigureAwait(false);

                foreach(var problem in problems)
                    notices.Add(problem);
            }
        }

        return Ordered(plan, results);
    }

    private async Task PublishAllAsync(
        string archivePath,
        ImportJournal? journal,
        List<PlannedTable> work,
        IReadOnlySet<string> fenced,
        ConcurrentDictionary<string, ImportTableResult> results,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        // One factory and one publisher for the whole restore. Both are stateless per
        // call - the factory only remembers the names it generated itself, and this tool
        // names its own - so they are safe to share across the tables running at once.
        var publisher = new TablePublisher(_options, new StagingTableFactory
        {
            CommandTimeoutSeconds = _options.CommandTimeoutSeconds
        }, new SwapPublisher());

        var done = 0;
        var rows = 0L;

        await Parallel.ForEachAsync(
            work,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, _options.Parallelism), CancellationToken = cancellationToken },
            async (table, token) =>
            {
                ImportTableResult result;

                try
                {
                    result = await publisher
                        .PublishAsync(archivePath, table.Entry, table.Columns!, fenced.Contains(table.Key), token)
                        .ConfigureAwait(false);
                }
                catch(Exception ex) when(ex is not OperationCanceledException)
                {
                    // A table that threw was not published: every route to the
                    // destination is one transaction, so there is no half-loaded state
                    // for this to be hiding. Which is what makes carrying on a choice
                    // rather than a gamble.
                    if(!_options.ContinueOnError)
                        throw;

                    result = new ImportTableResult(
                        table.Entry.Schema, table.Entry.Name, ImportTableOutcome.Failed, 0, Reason: ex.Message);
                }

                results[table.Key] = result;

                // The marker goes on after the publication has committed, so a run killed
                // in the middle of one leaves no marker and the next run does it again -
                // which is safe, because the table still holds what it held before.
                if(result.Outcome is ImportTableOutcome.Published)
                {
                    journal?.Complete(
                        ImportJournal.TableUnit(table.Entry.Schema, table.Entry.Name),
                        result.Rows.ToString(CultureInfo.InvariantCulture));
                }

                Report(
                    "data",
                    table.Entry.Identifier,
                    Interlocked.Add(ref rows, result.Rows),
                    Interlocked.Increment(ref done),
                    work.Count,
                    stopwatch);
            }).ConfigureAwait(false);
    }

    private ImportResult Finish(
        string archivePath,
        ArchiveManifest manifest,
        IReadOnlyList<ImportTableResult> tables,
        int batches,
        IReadOnlyList<string> notices,
        Stopwatch stopwatch) =>
        new(
            archivePath,
            _options.Mode,
            _options.DryRun,
            tables.Where(t => t.Outcome is ImportTableOutcome.Published).Sum(t => t.Rows),
            batches,
            stopwatch.Elapsed,
            tables,
            notices,
            manifest);

    private static List<ImportTableResult> Ordered(
        List<PlannedTable> plan,
        ConcurrentDictionary<string, ImportTableResult> results) =>
        plan.Select(p => results[p.Key]).ToList();

    private static ImportTableResult Nothing(PlannedTable table) =>
        new(table.Entry.Schema, table.Entry.Name, ImportTableOutcome.Skipped, 0,
            Reason: "--schema-only: the shape is restored and not a row is read");

    private bool Keep(string schema, string name)
    {
        if(_options.IncludeTables.Count > 0 && !TableGlob.MatchesAny(_options.IncludeTables, schema, name))
            return false;

        // Exclusion wins, so that "everything in sales except the audit log" is two
        // patterns rather than an enumeration.
        return !TableGlob.MatchesAny(_options.ExcludeTables, schema, name);
    }

    private void Report(string phase, string? table, long rows, int done, int total, Stopwatch stopwatch) =>
        _options.Progress?.Report(new ImportProgress(phase, table, rows, done, total, stopwatch.Elapsed));

    private static string Plural(int count, string noun) =>
        count == 1 ? $"1 {noun}" : $"{count.ToString("N0", CultureInfo.InvariantCulture)} {noun}s";

    private static string ToolVersion() =>
        typeof(DatabaseImporter).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+')[0]
        ?? typeof(DatabaseImporter).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";

    /// <summary>One table of the manifest, and either the columns to decode it with or why it is not being published.</summary>
    private sealed record PlannedTable(ArchiveTableEntry Entry, IReadOnlyList<ArchiveColumn>? Columns, string? Reason)
    {
        public string Key => $"{Entry.Schema}.{Entry.Name}";
    }
}
