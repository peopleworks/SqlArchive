using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using Microsoft.Data.SqlClient;
using SqlArchive.Core.Format;
using SqlSchemaDiff.Models;
using SqlSchemaDiff.Services;

namespace SqlArchive.Core.Export;

/// <summary>
/// Reads a whole SQL Server database and writes a <c>.sqlarchive</c>.
/// <para>
/// The shape of the file is not this class's to decide - <c>FORMAT.md</c> and the
/// <c>Format</c> namespace own that, and this fills it. What is decided here is
/// everything about the reading: which tables, at what point in time, through how many
/// connections, and where a large table is cut.
/// </para>
/// <para>
/// Nothing is held whole in memory at any point. A table's rows go from the reader
/// through the encoder into a spool file and from there into the zip, so a table larger
/// than the machine costs the same resident memory as a small one. That property is the
/// reason SyncJob.Core exists and it would be absurd to lose it here.
/// </para>
/// </summary>
public sealed class DatabaseExporter
{
    private readonly ExportOptions _options;

    public DatabaseExporter(ExportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrEmpty(options.ConnectionString);

        _options = options;
    }

    /// <summary>Writes the archive, and answers what went into it.</summary>
    /// <param name="archivePath">The file to create. Overwritten if it exists.</param>
    /// <param name="cancellationToken">Cancellation. A cancelled export leaves its spool behind when it is resumable.</param>
    public async Task<ExportResult> ExportAsync(string archivePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(archivePath);

        var stopwatch = Stopwatch.StartNew();
        var notices = new List<string>();
        var toolVersion = _options.ToolVersion ?? ToolVersion();

        var spool = ExportSpool.Open(
            _options.WorkingDirectory ?? archivePath + ".work",
            ExportFingerprint.Of(_options, toolVersion),
            _options.Resumable);

        if(spool.Resumed)
            notices.Add($"Resumed from the spool at '{spool.Directory}'; the tables already finished were not read again.");

        var succeeded = false;

        try
        {
            var source = await ReadSourceAsync(cancellationToken).ConfigureAwait(false);

            await using var session = await ConsistencySession
                .OpenAsync(_options.ConnectionString, _options.Consistent, _options.CommandTimeoutSeconds, cancellationToken)
                .ConfigureAwait(false);

            // Fired before a row is read and, under SNAPSHOT isolation, after the instant
            // has actually been fixed - see ConsistencySession. Anything written to the
            // source from here on is outside the archive, which is what makes the
            // consistency modes testable for what they do instead of what they report.
            if(_options.OnConsistencyEstablished is not null)
            {
                await _options.OnConsistencyEstablished(
                    new ArchiveConsistencyEstablished(session.Consistency, session.ReadConnectionString, session.SnapshotDatabase),
                    cancellationToken).ConfigureAwait(false);
            }

            Report("schema", null, 0, 0, 0, stopwatch);

            var extractor = new SqlServerSchemaExtractor();
            var extracted = await extractor.ExtractAsync(session.ReadConnectionString, cancellationToken).ConfigureAwait(false);
            notices.AddRange(extractor.Notices);

            var snapshot = SnapshotSelection.Restrict(extracted, Keep, source.Database, notices);
            var tables = SnapshotSelection.Tables(snapshot)
                .OrderBy(t => t.Schema, StringComparer.Ordinal)
                .ThenBy(t => t.Name, StringComparer.Ordinal)
                .ToList();

            var columns = new Dictionary<string, IReadOnlyList<ArchiveColumn>>(StringComparer.OrdinalIgnoreCase);
            var keys = new Dictionary<string, PartitionColumn?>(StringComparer.OrdinalIgnoreCase);

            var plan = await spool
                .LoadOrSavePlanAsync(() => PlanAsync(session, snapshot, tables, columns, keys, notices, cancellationToken))
                .ConfigureAwait(false);

            // A resumed export reads the plan back rather than the schema it was built
            // from, so the per-table detail the readers need is rebuilt here from the
            // tables that survived the same filters.
            Describe(snapshot, tables, plan, columns, keys);

            // Said out loud rather than left to be inferred. "Table by table" is about
            // tables not sharing an instant; a table read through several connections at
            // once does not share one with itself either. On a database nobody is writing
            // to - which is what an archive is usually taken of - the two are the same
            // thing, and on a live one they are not.
            if(session.Consistency == ArchiveConsistency.PerTable && plan.Tables.Any(t => t.Units.Count > 1))
            {
                notices.Add(
                    "Some tables were read in parallel ranges without a single point in time, so a row written " +
                    "while one of them was being read may be in the archive twice or not at all. Pass " +
                    "--consistent for a point in time, or turn the ranges off, if the database is being written " +
                    "to while it is exported.");
            }

            var results = await RunAsync(session, plan, columns, keys, spool, stopwatch, cancellationToken).ConfigureAwait(false);

            if(ShouldVerifyCounts(session))
                await VerifyCountsAsync(session, plan, results, cancellationToken).ConfigureAwait(false);

            var manifest = BuildManifest(snapshot, plan, results, source, session.Consistency, toolVersion);

            Report("packing", null, results.Values.Sum(r => r.Rows), results.Count, results.Count, stopwatch);

            var bytes = await PackAsync(archivePath, snapshot, plan, results, manifest, spool, cancellationToken)
                .ConfigureAwait(false);

            succeeded = true;
            notices.AddRange(session.Notices);

            return new ExportResult(
                archivePath,
                bytes,
                results.Values.Sum(r => r.Rows),
                plan.Tables.Count,
                results.Count,
                session.Consistency,
                stopwatch.Elapsed,
                notices,
                manifest);
        }
        finally
        {
            // A spool is kept only when somebody asked to be able to come back to it. On
            // a run that did not, a failure leaves behind a copy of the database on the
            // disk that nobody is going to use.
            if(succeeded || !_options.Resumable)
                spool.Delete();
        }
    }

    /// <summary>
    /// Turns the schema into a list of units: one per table read in a single pass, and
    /// one per range of a table that is split.
    /// </summary>
    private async Task<ExportPlan> PlanAsync(
        ConsistencySession session,
        DatabaseSnapshot snapshot,
        IReadOnlyList<TableModel> tables,
        Dictionary<string, IReadOnlyList<ArchiveColumn>> columns,
        Dictionary<string, PartitionColumn?> keys,
        IList<string> notices,
        CancellationToken cancellationToken)
    {
        var plan = new ExportPlan();
        var ordinal = 0;

        await using var scope = await session.AcquireAsync(cancellationToken).ConfigureAwait(false);

        foreach(var table in tables)
        {
            var identifier = Identifier(table.Schema, table.Name);

            // Passed the snapshot's alias types, without which ArchiveColumns throws on a
            // column declared as a user-defined type - which is exactly what it should do
            // rather than encode it as something it guessed.
            var archived = ArchiveColumns.For(table, snapshot.Types);

            var entry = new ExportTablePlan
            {
                Schema = table.Schema,
                Name = table.Name,
                DataSkipped = TableGlob.MatchesAny(_options.ExcludeData, table.Schema, table.Name),
                RowFilter = TableGlob.FirstMatch(_options.RowFilters, table.Schema, table.Name),
                OmittedColumns = ArchiveColumns.Omitted(table)
                    .ToDictionary(o => o.Column, o => o.Reason, StringComparer.Ordinal)
            };

            plan.Tables.Add(entry);

            if(entry.DataSkipped)
                continue;

            if(archived.Count == 0)
            {
                throw new ExportException(
                    $"{identifier} has no column the archive can carry - every one of them is computed, a " +
                    "rowversion, or a GENERATED ALWAYS period column, and SQL Server assigns all three itself. " +
                    "There is nothing to write and nothing a restore could put back. Exclude its data with a " +
                    "--no-data pattern, or leave the table out.");
            }

            var key = RangePlanner.ChooseColumn(table, archived);
            var estimate = await EstimateRowsAsync(scope, table, cancellationToken).ConfigureAwait(false);

            var ranges = await RangePlanner
                .PlanAsync(scope, table, key, estimate, _options, cancellationToken)
                .ConfigureAwait(false);

            if(ranges.Count > 1 && key is not null)
            {
                entry.PartitionColumn = key.Column.Name;
                entry.PartitionKind = Bounds.Name(PartitionKinds.Of(key.Column.Kind)!.Value);
                entry.PartitionReason = key.Reason;
            }
            else if(key is null && estimate >= _options.MinimumRowsToSplit && _options.Ranges != ExportRanges.Off)
            {
                notices.Add(
                    $"{identifier} has about {estimate.ToString("N0", CultureInfo.InvariantCulture)} rows and is " +
                    "read in one pass: it has no NOT NULL numeric or date column at the front of an index to " +
                    "split by. Splitting on anything else would be slower than one pass, or would risk leaving " +
                    "rows out.");
            }

            for(var i = 0; i < ranges.Count; i++)
            {
                entry.Units.Add(new ExportUnit
                {
                    Ordinal = ordinal++,
                    EntryName = ArchiveFormat.DataEntry(table.Schema, table.Name, ranges.Count == 1 ? null : i),
                    Lower = Bounds.Format(ranges[i].Lower),
                    Upper = Bounds.Format(ranges[i].Upper)
                });
            }

            columns[identifier] = archived;
            keys[identifier] = key;
        }

        return plan;
    }

    /// <summary>
    /// Fills in the per-table detail a resumed export did not compute, from the schema it
    /// did read.
    /// </summary>
    /// <remarks>
    /// The plan comes off the disk, deliberately, so that a table half exported under one
    /// set of cuts is never finished under another. The columns and the partition column
    /// are not in it: they follow from the schema, and the fingerprint has already
    /// established that this is the same database and the same filters.
    /// </remarks>
    private static void Describe(
        DatabaseSnapshot snapshot,
        IReadOnlyList<TableModel> tables,
        ExportPlan plan,
        Dictionary<string, IReadOnlyList<ArchiveColumn>> columns,
        Dictionary<string, PartitionColumn?> keys)
    {
        var byName = tables.ToDictionary(t => Identifier(t.Schema, t.Name), StringComparer.OrdinalIgnoreCase);

        foreach(var entry in plan.Tables)
        {
            var identifier = entry.Identifier;

            if(entry.DataSkipped || columns.ContainsKey(identifier))
                continue;

            if(!byName.TryGetValue(identifier, out var table))
            {
                throw new ExportResumeException(
                    $"The spool's plan names {identifier}, and the database does not have it any more. The schema " +
                    "changed between the two runs, so the halves would not be one archive.");
            }

            var archived = ArchiveColumns.For(table, snapshot.Types);
            columns[identifier] = archived;

            if(entry.PartitionColumn is null)
            {
                keys[identifier] = null;
                continue;
            }

            var chosen = RangePlanner.ChooseColumn(table, archived);

            // The plan's column is the one its bounds are about. If the schema now leads
            // to a different one, the bounds describe a column this run would not be
            // comparing against, and there is nothing good to be had from guessing which
            // of the two the half-written files belong to.
            if(chosen is null || !string.Equals(chosen.Column.Name, entry.PartitionColumn, StringComparison.OrdinalIgnoreCase))
            {
                throw new ExportResumeException(
                    $"The spool's plan splits {identifier} on [{entry.PartitionColumn}], and this run would split " +
                    $"it on [{chosen?.Column.Name ?? "nothing"}]. The indexes changed between the two runs.");
            }

            keys[identifier] = chosen;
        }
    }

    /// <summary>Reads every unit that has not already been read, up to the allowed parallelism.</summary>
    private async Task<Dictionary<int, ExportUnitResult>> RunAsync(
        ConsistencySession session,
        ExportPlan plan,
        Dictionary<string, IReadOnlyList<ArchiveColumn>> columns,
        Dictionary<string, PartitionColumn?> keys,
        ExportSpool spool,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        var work = plan.Tables
            .SelectMany(t => t.Units.Select((u, i) => (Table: t, Unit: u, Index: i)))
            .ToList();

        var results = new System.Collections.Concurrent.ConcurrentDictionary<int, ExportUnitResult>();
        var done = 0;
        var rows = 0L;

        var parallelism = session.SupportsParallelReads ? Math.Max(1, _options.Parallelism) : 1;

        await Parallel.ForEachAsync(
            work,
            new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = cancellationToken },
            async (item, token) =>
            {
                var result = spool.Completed(item.Unit)
                    ?? await ReadUnitAsync(session, item.Table, item.Unit, item.Index, columns, keys, spool, token)
                        .ConfigureAwait(false);

                results[item.Unit.Ordinal] = result;

                Report(
                    "reading",
                    item.Table.Identifier,
                    Interlocked.Add(ref rows, result.Rows),
                    Interlocked.Increment(ref done),
                    work.Count,
                    stopwatch);
            }).ConfigureAwait(false);

        return new Dictionary<int, ExportUnitResult>(results);
    }

    /// <summary>Reads one range of one table into its spool file, hashing the rows and the file as they go.</summary>
    private async Task<ExportUnitResult> ReadUnitAsync(
        ConsistencySession session,
        ExportTablePlan table,
        ExportUnit unit,
        int index,
        Dictionary<string, IReadOnlyList<ArchiveColumn>> columns,
        Dictionary<string, PartitionColumn?> keys,
        ExportSpool spool,
        CancellationToken cancellationToken)
    {
        var archived = columns[table.Identifier];
        var key = keys.GetValueOrDefault(table.Identifier);
        var range = unit.ToRange(table.PartitionKind, index);

        var select = "SELECT " + string.Join(", ", archived.Select(c => SqlRender.Quote(c.Name))) +
                     " FROM " + SqlRender.Quote(table.Schema, table.Name) +
                     range.Predicate(key is null ? string.Empty : SqlRender.Quote(key.Column.Name), table.RowFilter) +
                     ";";

        await using var scope = await session.AcquireAsync(cancellationToken).ConfigureAwait(false);
        await using var command = scope.Command(select);

        range.Bind(command, key?.Column);

        // SequentialAccess is what lets a varbinary(max) travel without being assembled
        // first. It works here because the encoder reads its columns strictly left to
        // right and probes a binary column's length with GetBytes, which the driver
        // answers in sequential mode - checked against SQL Server 2025 on a 200 KB value.
        await using var reader = await command
            .ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken).ConfigureAwait(false);

        long rows;
        string hash;

        var file = new FileStream(spool.RowsPath(unit), FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);

        await using(file.ConfigureAwait(false))
        {
            var hashing = new HashingWriteStream(file);

            await using(hashing.ConfigureAwait(false))
            {
                var writer = new JsonlRowWriter(hashing, archived);

                await using(writer.ConfigureAwait(false))
                    rows = await writer.WriteAllAsync(reader, cancellationToken).ConfigureAwait(false);

                hash = writer.Hash.Value;
            }

            var result = new ExportUnitResult
            {
                Rows = rows,
                RowHash = hash,
                Bytes = hashing.Bytes,
                Sha256 = hashing.Hash
            };

            // Written after the rows and never before, so a run killed half way through a
            // unit leaves rows with no marker - which are read again - rather than a
            // marker describing rows that are not all there.
            spool.Complete(unit, result);
            return result;
        }
    }

    /// <summary>
    /// Compares what was written against a <c>COUNT(*)</c> taken from the server.
    /// </summary>
    /// <remarks>
    /// This is the one check on the ranges that does not go through the range planner: if
    /// a boundary lost rows, the manifest and the data files would agree with each other
    /// and disagree with this. It is only run where the answer means something - under a
    /// database snapshot or a snapshot transaction, where the count and the read see the
    /// same instant. Table by table, a row inserted between the two would fail an export
    /// that did nothing wrong.
    /// </remarks>
    private async Task VerifyCountsAsync(
        ConsistencySession session,
        ExportPlan plan,
        IReadOnlyDictionary<int, ExportUnitResult> results,
        CancellationToken cancellationToken)
    {
        foreach(var table in plan.Tables)
        {
            if(table.DataSkipped || table.Units.Count == 0)
                continue;

            var written = table.Units.Sum(u => results[u.Ordinal].Rows);

            await using var scope = await session.AcquireAsync(cancellationToken).ConfigureAwait(false);

            var where = string.IsNullOrWhiteSpace(table.RowFilter) ? string.Empty : $" WHERE ({table.RowFilter})";

            await using var command = scope.Command(
                $"SELECT COUNT_BIG(*) FROM {SqlRender.Quote(table.Schema, table.Name)}{where};");

            var counted = Convert.ToInt64(
                await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);

            if(counted != written)
            {
                throw new ExportException(
                    $"{table.Identifier}: the server counts {counted.ToString("N0", CultureInfo.InvariantCulture)} " +
                    $"rows and the export wrote {written.ToString("N0", CultureInfo.InvariantCulture)}, across " +
                    $"{table.Units.Count} range(s) on [{table.PartitionColumn ?? "no partition column"}]. The " +
                    "archive is not written. Both numbers come from the same point in time, so this is a fault " +
                    "in the ranges rather than concurrent activity.");
            }
        }
    }

    /// <summary>Whether a count taken now would mean anything.</summary>
    private bool ShouldVerifyCounts(ConsistencySession session) =>
        _options.VerifyRowCounts ?? session.Consistency != ArchiveConsistency.PerTable;

    private ArchiveManifest BuildManifest(
        DatabaseSnapshot snapshot,
        ExportPlan plan,
        IReadOnlyDictionary<int, ExportUnitResult> results,
        ArchiveSource source,
        ArchiveConsistency consistency,
        string toolVersion)
    {
        var manifest = new ArchiveManifest
        {
            Tool = new ArchiveTool { Version = toolVersion },
            CreatedAt = DateTimeOffset.UtcNow,
            Source = source,
            Consistency = consistency,
            Schema = snapshot
        };

        foreach(var table in plan.Tables)
        {
            var entry = new ArchiveTableEntry
            {
                Schema = table.Schema,
                Name = table.Name,
                RowFilter = table.RowFilter,
                DataSkipped = table.DataSkipped,
                OmittedColumns = table.OmittedColumns
            };

            if(!table.DataSkipped)
            {
                var total = new RowHash.Accumulator();

                foreach(var unit in table.Units)
                {
                    var result = results[unit.Ordinal];

                    // The ranges are folded rather than re-read, which is sound only
                    // because the combining operation does not care about order. That is
                    // the property the hash was chosen for and this is where it is spent.
                    total.Add(Bytes(result.RowHash));
                    entry.DataFiles.Add(unit.EntryName);
                }

                entry.RowCount = table.Units.Sum(u => results[u.Ordinal].Rows);
                entry.RowHash = total.Value;
            }

            manifest.Tables.Add(entry);
        }

        return manifest;
    }

    /// <summary>Assembles the zip: the schema phases, the spooled rows, the README, and the manifest last.</summary>
    private async Task<long> PackAsync(
        string archivePath,
        DatabaseSnapshot snapshot,
        ExportPlan plan,
        IReadOnlyDictionary<int, ExportUnitResult> results,
        ArchiveManifest manifest,
        ExportSpool spool,
        CancellationToken cancellationToken)
    {
        var writer = ArchiveWriter.Create(archivePath, _options.Compression);

        await using(writer.ConfigureAwait(false))
        {
            // ConstraintsAfterData is what turns the composer's output into the shape a
            // restore wants: bare tables first, then rows, then the keys, indexes, checks
            // and foreign keys that would otherwise be maintained once per row.
            // RestartSequences goes with it so a restored database hands out the next
            // number rather than starting again at one.
            var phases = ScriptComposer.ComposePhases(
                snapshot, new ComposeOptions { ConstraintsAfterData = true, RestartSequences = true });

            foreach(var phase in phases)
            {
                await writer
                    .AddTextAsync(ArchiveFormat.SchemaEntry(phase.FileName), Render(phase), cancellationToken)
                    .ConfigureAwait(false);
            }

            foreach(var unit in plan.Units)
            {
                var entry = writer.CreateEntry(unit.EntryName);

                await using(entry.ConfigureAwait(false))
                {
                    var rows = new FileStream(
                        spool.RowsPath(unit), FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);

                    await using(rows.ConfigureAwait(false))
                        await rows.CopyToAsync(entry, cancellationToken).ConfigureAwait(false);
                }

                var declared = results[unit.Ordinal].Sha256;
                var actual = writer.EntryHashes[unit.EntryName];

                if(!string.Equals(declared, actual, StringComparison.Ordinal))
                {
                    throw new ExportException(
                        $"The spooled rows for '{unit.EntryName}' hashed {actual} on the way into the archive and " +
                        $"{declared} on the way out of the database. The spool file changed after it was written, " +
                        "so the manifest would describe rows that are not the ones that were read.");
                }
            }

            await writer
                .AddTextAsync(ArchiveFormat.ReadmeEntry, ArchiveReadme.Compose(manifest), cancellationToken)
                .ConfigureAwait(false);

            var byTable = plan.Tables
                .SelectMany(t => t.Units.Select(u => (u.EntryName, Table: manifest.Table(t.Schema, t.Name)!)))
                .ToDictionary(x => x.EntryName, x => x.Table, StringComparer.Ordinal);

            // Every entry's hash lands in exactly one of the two places the manifest keeps
            // them: a table's own if it holds that table's rows, and the top-level files
            // map otherwise. Between them they cover the archive once, with nothing twice
            // and nothing missing, which is what makes "is this file intact?" answerable.
            foreach(var (name, hash) in writer.EntryHashes)
            {
                if(byTable.TryGetValue(name, out var table))
                    table.FileHashes[name] = hash;
                else
                    manifest.Files[name] = hash;
            }

            await writer.WriteManifestAsync(manifest, cancellationToken).ConfigureAwait(false);
        }

        return new FileInfo(archivePath).Length;
    }

    /// <summary>
    /// One phase as a file somebody can run by hand, which is what makes the archive an
    /// archive rather than an input to this tool.
    /// </summary>
    private static string Render(ScriptPhase phase)
    {
        var text = new StringBuilder()
            .Append("-- ").Append(phase.FileName).Append(" - ").AppendLine(phase.Name)
            .AppendLine("-- Run the phases in the order of their numeric prefixes.")
            .AppendLine()
            .AppendLine(SqlRender.SessionOptionsPreamble)
            .AppendLine("GO")
            .AppendLine();

        if(phase.Batches.Count == 0)
        {
            // Written even when empty. A missing file reads as a phase somebody forgot;
            // an empty one reads as a database with nothing of that kind in it.
            text.AppendLine("-- Nothing in this database belongs to this phase.");
            return text.ToString();
        }

        foreach(var batch in phase.Batches)
        {
            // GO after the comment rather than before, so the comment is not stored as
            // part of a module's definition in sys.sql_modules.
            text.Append("-- ").AppendLine(batch.Describe)
                .AppendLine("GO")
                .AppendLine(SqlRender.EnsureTrailingGo(batch.Sql))
                .AppendLine();
        }

        return text.ToString();
    }

    /// <summary>Where the archive came from, read from the source database and not from the snapshot of it.</summary>
    private async Task<ArchiveSource> ReadSourceAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = new SqlCommand(
            """
            SELECT CONVERT(nvarchar(256), SERVERPROPERTY('ServerName')),
                   DB_NAME(),
                   CONVERT(nvarchar(256), SERVERPROPERTY('Edition')),
                   CONVERT(nvarchar(128), SERVERPROPERTY('ProductVersion')),
                   CONVERT(nvarchar(128), DATABASEPROPERTYEX(DB_NAME(), 'Collation'));
            """,
            connection)
        {
            CommandTimeout = _options.CommandTimeoutSeconds
        };

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);

        return new ArchiveSource
        {
            // The server's own name for itself, falling back to whatever was dialled -
            // ServerName is null on an instance whose @@SERVERNAME was never set.
            Server = reader.IsDBNull(0) ? connection.DataSource : reader.GetString(0),
            Database = reader.GetString(1),
            Edition = reader.IsDBNull(2) ? null : reader.GetString(2),
            ProductVersion = reader.IsDBNull(3) ? null : reader.GetString(3),

            // The database's collation, not the connection's: it is a property of what is
            // being archived, and it is what a restore has to reproduce.
            Collation = reader.IsDBNull(4) ? null : reader.GetString(4)
        };
    }

    /// <summary>
    /// The catalog's row count for a table.
    /// </summary>
    /// <remarks>
    /// From <c>sys.partitions</c> rather than from <c>COUNT(*)</c>, so that deciding how
    /// to read a hundred-million-row table does not start by reading it. It is an
    /// estimate and it is allowed to be stale: it chooses how many files there are and
    /// never which rows go in them.
    /// </remarks>
    private async Task<long> EstimateRowsAsync(ExportReadScope scope, TableModel table, CancellationToken cancellationToken)
    {
        await using var command = scope.Command(
            """
            SELECT ISNULL(SUM(p.rows), 0)
            FROM sys.partitions AS p
            WHERE p.object_id = OBJECT_ID(@table) AND p.index_id IN (0, 1);
            """);

        command.Parameters.Add(new SqlParameter("@table", SqlDbType.NVarChar, 520)
        {
            Value = SqlRender.Quote(table.Schema, table.Name)
        });

        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private bool Keep(string schema, string name)
    {
        if(_options.IncludeTables.Count > 0 && !TableGlob.MatchesAny(_options.IncludeTables, schema, name))
            return false;

        // Exclusion wins, so that "everything in sales except the audit log" is two
        // patterns rather than an enumeration.
        return !TableGlob.MatchesAny(_options.ExcludeTables, schema, name);
    }

    private void Report(string phase, string? table, long rows, int done, int total, Stopwatch stopwatch) =>
        _options.Progress?.Report(new ExportProgress(phase, table, rows, done, total, stopwatch.Elapsed));

    private static string Identifier(string schema, string name) => $"[{schema}].[{name}]";

    private static byte[] Bytes(string hex) => Convert.FromHexString(hex);

    private static string ToolVersion() =>
        typeof(DatabaseExporter).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+')[0]
        ?? typeof(DatabaseExporter).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";
}

/// <summary>What an export produced.</summary>
/// <param name="Path">The archive.</param>
/// <param name="Bytes">Its size on disk.</param>
/// <param name="Rows">Rows written, across every table.</param>
/// <param name="Tables">Tables in the manifest, including the ones whose data was skipped.</param>
/// <param name="Units">Entries of rows written - more than <paramref name="Tables"/> when a table was split.</param>
/// <param name="Consistency">What the export actually achieved, which is also what the manifest says.</param>
/// <param name="Elapsed">How long it took.</param>
/// <param name="Notices">Everything worth telling the operator: dropped foreign keys, tables read in one pass, a snapshot that would not drop.</param>
/// <param name="Manifest">The manifest as written, so a caller does not have to reopen the file to see it.</param>
public sealed record ExportResult(
    string Path,
    long Bytes,
    long Rows,
    int Tables,
    int Units,
    ArchiveConsistency Consistency,
    TimeSpan Elapsed,
    IReadOnlyList<string> Notices,
    ArchiveManifest Manifest);
