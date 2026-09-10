using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.SqlClient;
using SqlArchive.Core.Export;
using SqlArchive.Core.Format;
using SqlSchemaDiff.Models;
using SqlSchemaDiff.Services;

namespace SqlArchive.Core.Verify;

/// <summary>
/// Answers whether two things match, with one manifest and three questions.
/// <para>
/// <b>Is the archive intact?</b> The file hashes in the manifest against the entries
/// actually in the zip, and the entries in the zip against what the manifest declares.
/// No server is touched. This is the archival use, and it is the only one of the three
/// still answerable when the source database has been gone for years.
/// </para>
/// <para>
/// <b>Does a restored database match the archive?</b> The schema through the same differ
/// a restore-as-migration would use, and per table the row count and the content hash
/// through <see cref="LiveTableDigest"/>.
/// </para>
/// <para>
/// <b>Has a live database drifted from the archive?</b> The same code pointed at
/// production. It is the audit use, and nothing here requires the database to have come
/// from the archive.
/// </para>
/// <para>
/// Every per-table comparison goes through <see cref="LiveTableDigest"/>, which reads a
/// live table through the archive's own encoder. A second way of hashing a table would
/// mean the two sides of a comparison were answering different questions.
/// </para>
/// </summary>
public sealed class ArchiveVerifier
{
    private readonly VerifyOptions _options;

    public ArchiveVerifier(VerifyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <summary>Opens the archive and verifies it.</summary>
    public async Task<VerifyReport> VerifyAsync(string archivePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(archivePath);

        using var archive = await ArchiveReader.OpenAsync(archivePath, cancellationToken).ConfigureAwait(false);
        return await VerifyAsync(archive, archivePath, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The same, on an archive somebody else opened - so that a caller holding a reader
    /// does not open the file twice.
    /// </summary>
    /// <param name="archive">The archive. Not disposed here.</param>
    /// <param name="name">What to call it in the report.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<VerifyReport> VerifyAsync(
        ArchiveReader archive,
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(archive);

        var stopwatch = Stopwatch.StartNew();
        var notices = new List<string>();
        var manifest = archive.Manifest;

        if(manifest.RequiresNewerReader)
        {
            notices.Add(
                $"The manifest declares format version {manifest.FormatVersion.ToString(CultureInfo.InvariantCulture)} " +
                $"and this build reads {ArchiveFormat.CurrentVersion.ToString(CultureInfo.InvariantCulture)}. " +
                "What follows is what this build can still make out, and an entry it does not recognise is " +
                "reported rather than counted against the archive.");
        }

        var integrity = await CheckIntegrityAsync(archive, cancellationToken).ConfigureAwait(false);

        SchemaVerdict? schema = null;
        IReadOnlyList<TableVerdict> tables = [];
        string? database = null;

        if(_options.ConnectionString is { Length: > 0 } connectionString)
        {
            Report("schema", null, 0, 0);

            var extractor = new SqlServerSchemaExtractor();
            var live = await extractor.ExtractAsync(connectionString, cancellationToken).ConfigureAwait(false);

            notices.AddRange(extractor.Notices);
            database = live.DatabaseName;

            schema = CompareSchema(manifest, live, notices);
            tables = await CompareTablesAsync(manifest, live, connectionString, schema, notices, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            notices.Add(
                "No database was named, so this compared the archive against itself and touched no server. That is " +
                "the question an archive exists to answer: whether this file is still what it says it is. Pass " +
                "--against to compare it with a database.");
        }

        return new VerifyReport
        {
            Archive = name,
            Database = database,
            CheckedAt = DateTimeOffset.UtcNow,
            Elapsed = stopwatch.Elapsed,
            Integrity = integrity,
            Schema = schema,
            Tables = tables,
            Notices = notices
        };
    }

    // ------------------------------------------------------------- is the archive intact?

    /// <summary>
    /// Every entry hashed against what the manifest declares, and the manifest's own
    /// account of the archive checked against what is in it.
    /// </summary>
    /// <remarks>
    /// Both directions on purpose. A manifest that agrees with itself proves nothing: an
    /// entry declared and absent is a truncated archive, and an entry present and
    /// undeclared is one the manifest does not cover, which <c>FORMAT.md</c> says cannot
    /// happen. Entries are read one at a time because a <c>ZipArchive</c> is one cursor
    /// over one file, and reading it from several threads would corrupt the reading
    /// rather than the archive.
    /// </remarks>
    private async Task<IntegrityVerdict> CheckIntegrityAsync(ArchiveReader archive, CancellationToken cancellationToken)
    {
        var manifest = archive.Manifest;

        var present = archive.Entries
            .Select(e => e.Name)
            .Where(n => !string.Equals(n, ArchiveFormat.ManifestEntry, StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);

        var declared = manifest.DeclaredEntries().ToHashSet(StringComparer.Ordinal);

        var data = manifest.Tables
            .SelectMany(t => t.DataFiles)
            .ToHashSet(StringComparer.Ordinal);

        var verdicts = new List<EntryVerdict>(declared.Count + present.Count);
        var done = 0;

        foreach(var entry in declared.Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var expected = manifest.HashOf(entry);

            if(!present.Contains(entry))
            {
                verdicts.Add(new EntryVerdict(entry, EntryState.Missing, expected, null));
                continue;
            }

            // --schema-only says no table is read on either side, and decompressing a
            // table's rows to hash them is reading it. On a hundred-gigabyte archive that
            // is the whole cost of the command.
            if(_options.SchemaOnly && data.Contains(entry))
            {
                verdicts.Add(new EntryVerdict(entry, EntryState.NotChecked, expected, null));
                continue;
            }

            if(string.IsNullOrEmpty(expected))
            {
                // dbdumper's manifest is like this throughout: it names its files and
                // records no hash for any of them. Not evidence of anything - just a
                // question this archive cannot answer.
                verdicts.Add(new EntryVerdict(entry, EntryState.NotHashed, null, null));
                continue;
            }

            var actual = await archive.ComputeEntryHashAsync(entry, cancellationToken).ConfigureAwait(false);

            verdicts.Add(new EntryVerdict(
                entry,
                string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase) ? EntryState.Intact : EntryState.Corrupt,
                expected,
                actual));

            Report("integrity", entry, ++done, declared.Count);
        }

        foreach(var entry in present.Except(declared, StringComparer.Ordinal).Order(StringComparer.Ordinal))
            verdicts.Add(new EntryVerdict(entry, EntryState.Undeclared, null, null));

        return new IntegrityVerdict
        {
            Entries = verdicts,

            // A manifest from a later version is allowed to describe entries this build
            // has never heard of, so an unrecognised one is reported and not held against
            // the archive. FORMAT.md makes the same call about reading a version you do
            // not know: describe what you can rather than refuse the file.
            UndeclaredCounts = !manifest.RequiresNewerReader
        };
    }

    // ------------------------------------------------------------- does the schema line up?

    private SchemaVerdict? CompareSchema(ArchiveManifest manifest, DatabaseSnapshot live, List<string> notices)
    {
        if(manifest.Schema is not { } archived)
        {
            notices.Add(
                "The manifest carries no schema snapshot, so there was nothing to compare the database's schema " +
                "against. Only the tables the manifest names were checked.");

            return null;
        }

        var ignored = new List<string>();
        var filtered = _options.IncludeTables.Count > 0 || _options.ExcludeTables.Count > 0;

        var (left, right) = ComparableSchema.Prepare(archived, live, Keep, filtered, ignored);

        // The archive is the source and the database the target, which is the direction a
        // restore would run in: what the differ calls "added" is what an import would have
        // to create, so it is what the archive has and the database does not.
        var diff = new SchemaDiffer().Diff(
            left,
            right,
            includeDrops: true,
            includeTableDrops: true,
            allowTableRebuild: false,
            addOnly: false);

        return new SchemaVerdict
        {
            OnlyInArchive = diff.AddedObjects,
            OnlyInDatabase = diff.RemovedObjects,
            Differing = diff.ChangedObjects,
            Ignored = ignored
        };
    }

    // ------------------------------------------------------------- do the rows match?

    private async Task<IReadOnlyList<TableVerdict>> CompareTablesAsync(
        ArchiveManifest manifest,
        DatabaseSnapshot live,
        string connectionString,
        SchemaVerdict? schema,
        List<string> notices,
        CancellationToken cancellationToken)
    {
        var liveTables = SnapshotSelection.Tables(live)
            .ToDictionary(t => Key(t.Schema, t.Name), StringComparer.OrdinalIgnoreCase);

        var archiveTables = manifest.Schema is { } archived
            ? SnapshotSelection.Tables(archived).ToDictionary(t => Key(t.Schema, t.Name), StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, TableModel>(StringComparer.OrdinalIgnoreCase);

        // Alias types come from the archive's snapshot and never from the database: a
        // column declared dbo.Dinero is encoded as the decimal(19,4) that type is built
        // from, and which decimal that is has to be the archive's answer.
        var aliasTypes = manifest.Schema?.Types ?? [];

        var differing = (schema?.Differing ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var wanted = manifest.Tables.Where(t => Keep(t.Schema, t.Name)).ToList();
        var skipped = manifest.Tables.Count - wanted.Count;

        if(skipped > 0)
        {
            notices.Add(
                $"{skipped.ToString(CultureInfo.InvariantCulture)} of the archive's " +
                $"{manifest.Tables.Count.ToString(CultureInfo.InvariantCulture)} tables were left out by --table " +
                "or --exclude and were not compared at all.");
        }

        var verdicts = new ConcurrentBag<TableVerdict>();
        var done = 0;

        await Parallel.ForEachAsync(
            wanted,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, _options.Parallelism),
                CancellationToken = cancellationToken
            },
            async (entry, token) =>
            {
                var verdict = await CompareTableAsync(
                    entry,
                    archiveTables.GetValueOrDefault(Key(entry.Schema, entry.Name)),
                    liveTables.GetValueOrDefault(Key(entry.Schema, entry.Name)),
                    aliasTypes,
                    differing.Contains(entry.Identifier),
                    connectionString,
                    token).ConfigureAwait(false);

                verdicts.Add(verdict);
                Report("tables", verdict.Identifier, Interlocked.Increment(ref done), wanted.Count);
            }).ConfigureAwait(false);

        // Tables the database has and the manifest does not name. Discovered from the
        // live schema rather than from the manifest, because a list built out of the
        // manifest could never contain one.
        var named = manifest.Tables.Select(t => Key(t.Schema, t.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach(var table in SnapshotSelection.Tables(live))
        {
            if(named.Contains(Key(table.Schema, table.Name)) || !Keep(table.Schema, table.Name))
                continue;

            verdicts.Add(new TableVerdict
            {
                Schema = table.Schema,
                Name = table.Name,
                Outcome = TableOutcome.OnlyInDatabase,
                Differences = ["the database has this table and the archive does not."]
            });
        }

        return verdicts
            .OrderBy(v => v.Schema, StringComparer.OrdinalIgnoreCase)
            .ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>One table, on both sides, said in the words the report prints.</summary>
    private async Task<TableVerdict> CompareTableAsync(
        ArchiveTableEntry entry,
        TableModel? archived,
        TableModel? live,
        IReadOnlyList<AliasTypeModel> aliasTypes,
        bool schemaDiffers,
        string connectionString,
        CancellationToken cancellationToken)
    {
        var differences = new List<string>();
        string? limitation = null;

        if(live is null)
        {
            return new TableVerdict
            {
                Schema = entry.Schema,
                Name = entry.Name,
                Outcome = TableOutcome.MissingFromDatabase,
                ArchiveRows = entry.DataSkipped ? null : entry.RowCount,
                ArchiveHash = entry.RowHash,
                RowFilter = entry.RowFilter,
                DataSkipped = entry.DataSkipped,
                Differences = ["the archive has this table and the database does not."]
            };
        }

        if(schemaDiffers)
        {
            var words = TableShape.Describe(archived, live);

            if(words.Count > 0)
                differences.AddRange(words);
            else
                differences.Add("the two are shaped differently in a way this report cannot name; the schema section lists the object.");
        }

        var outcome = schemaDiffers ? TableOutcome.SchemaDiffers : TableOutcome.Matches;

        if(_options.SchemaOnly)
        {
            return Build(entry, outcome == TableOutcome.Matches ? TableOutcome.NotCompared : outcome, differences,
                "--schema-only: the rows were not read on either side.", null, null, false);
        }

        if(entry.DataSkipped)
        {
            return Build(entry, outcome == TableOutcome.Matches ? TableOutcome.NotVerifiable : outcome, differences,
                "the archive carries this table's schema and deliberately not its rows, so there is nothing here to " +
                "compare them with. The manifest says so, which is why this is not reported as a difference.",
                null, null, false);
        }

        IReadOnlyList<ArchiveColumn> columns = [];

        if(archived is not null)
        {
            try
            {
                columns = ArchiveColumns.For(archived, aliasTypes);
            }
            catch(ArchiveEncodingException refused)
            {
                // A column of a type this build cannot encode. The export refused to
                // archive it and this refuses to pretend it could have read it back.
                limitation = $"the rows could not be read: {refused.Message}";
            }
        }

        long rows;
        string? hash = null;
        var contentCompared = false;

        try
        {
            // A manifest with no row hash - dbdumper's, throughout - can only be checked
            // against a count, and a count is a COUNT rather than a full read of a table
            // whose answer would then be compared against nothing.
            if(string.IsNullOrEmpty(entry.RowHash) || columns.Count == 0)
            {
                rows = await CountAsync(connectionString, entry, cancellationToken).ConfigureAwait(false);

                // ??= rather than =: a column of a type this build cannot encode has
                // already said something more specific than "no columns".
                limitation ??= columns.Count == 0
                    ? "the manifest carries no columns for this table, so only the row count could be compared."
                    : "the manifest carries no row hash for this table, so only the row count could be compared - " +
                      "a count cannot see an UPDATE.";
            }
            else
            {
                var digest = await LiveTableDigest.ComputeAsync(
                    connectionString,
                    entry.Schema,
                    entry.Name,
                    columns,
                    entry.RowFilter,
                    _options.CommandTimeoutSeconds,
                    cancellationToken).ConfigureAwait(false);

                rows = digest.Rows;
                hash = digest.RowHash;
                contentCompared = true;
            }
        }
        catch(SqlException failed)
        {
            differences.Add($"the table could not be read: {failed.Message}");

            return Build(entry, schemaDiffers ? TableOutcome.SchemaDiffers : TableOutcome.Unreadable,
                differences, limitation, null, null, false);
        }

        if(rows != entry.RowCount)
        {
            differences.Add(
                $"the archive declares {entry.RowCount.ToString("N0", CultureInfo.InvariantCulture)} rows and the " +
                $"database holds {rows.ToString("N0", CultureInfo.InvariantCulture)}.");

            if(outcome == TableOutcome.Matches)
                outcome = TableOutcome.RowCountDiffers;
        }
        else if(contentCompared && !string.Equals(hash, entry.RowHash, StringComparison.OrdinalIgnoreCase))
        {
            // The case a row count cannot see, and the reason the hash is in the manifest
            // at all: a thousand modified rows are still a thousand rows.
            // The two hashes are fields of the verdict rather than words in the sentence:
            // sixty-four characters twice would push everything worth reading off the
            // side of a terminal, and a build that wants them reads them from the JSON.
            differences.Add(
                $"{rows.ToString("N0", CultureInfo.InvariantCulture)} rows on both sides, and the content differs.");

            if(outcome == TableOutcome.Matches)
                outcome = TableOutcome.ContentDiffers;
        }

        if(!string.IsNullOrEmpty(entry.RowFilter))
        {
            limitation ??=
                $"the archive holds only the rows matching '{entry.RowFilter}', and both sides were read through " +
                "that filter. Rows outside it were not compared and are not reported as differences.";
        }

        // Counts agreeing where the content was never compared is not a match. It is the
        // manifest failing to answer, and reporting it green would be the one lie this
        // tool exists to stop telling: a thousand modified rows are still a thousand rows.
        if(outcome == TableOutcome.Matches && !contentCompared)
            outcome = TableOutcome.NotVerifiable;

        return Build(entry, outcome, differences, limitation, rows, hash, contentCompared);
    }

    private static TableVerdict Build(
        ArchiveTableEntry entry,
        TableOutcome outcome,
        IReadOnlyList<string> differences,
        string? limitation,
        long? rows,
        string? hash,
        bool contentCompared) =>
        new()
        {
            Schema = entry.Schema,
            Name = entry.Name,
            Outcome = outcome,
            ArchiveRows = entry.DataSkipped ? null : entry.RowCount,
            DatabaseRows = rows,
            ArchiveHash = entry.RowHash,
            DatabaseHash = hash,
            RowFilter = entry.RowFilter,
            DataSkipped = entry.DataSkipped,
            ContentCompared = contentCompared,
            Differences = differences,
            Limitation = limitation
        };

    /// <summary>
    /// <c>COUNT_BIG</c> through the same filter the archive was written with, for a table
    /// whose manifest cannot answer for the content.
    /// </summary>
    private async Task<long> CountAsync(
        string connectionString,
        ArchiveTableEntry entry,
        CancellationToken cancellationToken)
    {
        var where = string.IsNullOrWhiteSpace(entry.RowFilter) ? string.Empty : $" WHERE ({entry.RowFilter})";

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = new SqlCommand(
            $"SELECT COUNT_BIG(*) FROM {SqlRender.Quote(entry.Schema, entry.Name)}{where};",
            connection)
        {
            CommandTimeout = _options.CommandTimeoutSeconds
        };

        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private bool Keep(string schema, string name)
    {
        if(_options.IncludeTables.Count > 0 && !TableGlob.MatchesAny(_options.IncludeTables, schema, name))
            return false;

        return !TableGlob.MatchesAny(_options.ExcludeTables, schema, name);
    }

    private void Report(string phase, string? table, int done, int total) =>
        _options.Progress?.Report(new VerifyProgress(phase, table, done, total));

    private static string Key(string schema, string name) => $"{schema}.{name}";
}
