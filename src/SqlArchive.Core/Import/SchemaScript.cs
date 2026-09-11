using System.Globalization;
using Microsoft.Data.SqlClient;
using SqlArchive.Core.Format;
using SqlSchemaDiff.Services;

namespace SqlArchive.Core.Import;

/// <summary>
/// The archive's own <c>schema/*.sql</c> entries, split into batches and run against the
/// destination.
/// <para>
/// The files are executed rather than recomposed from the manifest's snapshot on purpose.
/// <c>FORMAT.md</c> says the phases are executable by hand, and that promise is only worth
/// anything if the tool executes the same text: a restore that ran something it composed
/// itself would be testing a script nobody can read beside a file nobody runs.
/// </para>
/// <para>
/// The split into before and after the data is what the numeric prefixes are for.
/// Everything up to <c>040_tables</c> is shape the rows need; from <c>050_indexes</c> on
/// is work that would otherwise be done once per row, and <c>070_foreignkeys</c> in
/// particular is why a restore needs no topological sort - the keys are not there while
/// the tables are being filled. <c>090_finalize</c> carries the identity reseeds, the
/// sequence positions and <c>SYSTEM_VERSIONING = ON</c>, all three of which are wrong
/// before the rows and right after them.
/// </para>
/// </summary>
public static class SchemaScript
{
    /// <summary>
    /// The last phase that runs before the data. Phases are numbered in tens with room
    /// between them, and this is the boundary the composer was built around rather than
    /// a number this file chose.
    /// </summary>
    private const int LastPhaseBeforeData = 40;

    /// <summary>
    /// The first phase that belongs to finalizing rather than to shape. Everything from
    /// here on is a statement the diff cannot express and the destination needs anyway.
    /// </summary>
    private const int FirstFinalizePhase = 90;

    /// <summary>
    /// The archive's schema entries, in execution order, split at the data.
    /// </summary>
    public static (IReadOnlyList<string> BeforeData, IReadOnlyList<string> AfterData) Phases(ArchiveReader archive)
    {
        ArgumentNullException.ThrowIfNull(archive);

        var before = new List<string>();
        var after = new List<string>();

        foreach(var entry in archive.SchemaEntries)
            (Number(entry) <= LastPhaseBeforeData ? before : after).Add(entry);

        return (before, after);
    }

    /// <summary>
    /// The finalize phase alone: the sequence positions, the <c>SYSTEM_TIME</c> periods and
    /// <c>SYSTEM_VERSIONING = ON</c>.
    /// </summary>
    /// <remarks>
    /// What a migration runs after its data, and all it runs. The diff has already made
    /// the destination's shape right - it creates a new table with its keys and indexes
    /// attached and alters an existing one - so running <c>050_indexes</c> over it would
    /// try to create every index a second time. Finalize is the exception because the diff
    /// cannot express it: SQLDiff's renderer deliberately never puts
    /// <c>SYSTEM_VERSIONING</c> in a <c>CREATE TABLE</c>, since versioning can only be
    /// turned on for a table that already has its primary key. A sequence restart and a
    /// <c>SYSTEM_VERSIONING = ON</c> naming the history the table already has are both
    /// idempotent - checked against SQL Server 2025. An <c>ADD PERIOD</c> is not, and a
    /// migration's destination always has the period by then; see
    /// <see cref="WithoutPeriodsAlreadyThereAsync"/>, which takes those out.
    /// </remarks>
    public static IReadOnlyList<string> Finalize(ArchiveReader archive)
    {
        ArgumentNullException.ThrowIfNull(archive);

        return archive.SchemaEntries.Where(e => Number(e) >= FirstFinalizePhase).ToList();
    }

    /// <summary>
    /// The batches without every <c>ADD PERIOD FOR SYSTEM_TIME</c> whose table already has
    /// its period - which is what makes the finalize phase safe to run after a migration.
    /// </summary>
    /// <remarks>
    /// <b>Why it has to exist.</b> An archive's <c>090_finalize.sql</c> adds the period of
    /// every temporal table, because on a fresh restore the tables were created without it.
    /// After a migration the destination already has it: the diff created the table with
    /// its period, or added it to one that lacked it, and the data phase put it back
    /// itself - see <c>TemporalPublisher</c>. Adding it again is refused, and refused at
    /// compile time, so no <c>TRY</c> could catch it: "Temporal SYSTEM_TIME period is
    /// already defined on table", error 13597, measured on SQL Server 2025. The other two
    /// kinds of statement in the phase - a sequence restart and <c>SYSTEM_VERSIONING =
    /// ON</c> naming the history it already has - are idempotent, the second measured too.
    /// <para>
    /// Decided by the destination's catalog, not by which route was taken, so a table the
    /// diff could not give its period still gets it here.
    /// </para>
    /// </remarks>
    public static async Task<IReadOnlyList<SchemaBatch>> WithoutPeriodsAlreadyThereAsync(
        string connectionString,
        IReadOnlyList<SchemaBatch> batches,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(connectionString);
        ArgumentNullException.ThrowIfNull(batches);

        if(!batches.Any(b => PeriodAddedBy(b.Sql) is not null))
            return batches;

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var kept = new List<SchemaBatch>(batches.Count);

        foreach(var batch in batches)
        {
            if(PeriodAddedBy(batch.Sql) is { } table &&
               await DestinationCatalog.HasPeriodAsync(connection, table, commandTimeoutSeconds, cancellationToken)
                   .ConfigureAwait(false))
            {
                continue;
            }

            kept.Add(batch);
        }

        return kept;
    }

    /// <summary>
    /// The table a batch adds a <c>SYSTEM_TIME</c> period to, quoted as the batch names it,
    /// or null when the batch is anything else.
    /// </summary>
    /// <remarks>
    /// The batch is SqlSchemaDiff's <c>SqlRender.BuildPeriodAdd</c> as the exporter wrote
    /// it into the archive: the <c>ALTER TABLE</c> first, then an <c>ADD HIDDEN</c> for each
    /// hidden period column. Those go with it, because they only make sense on a period
    /// that statement created.
    /// </remarks>
    public static string? PeriodAddedBy(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);

        var match = PeriodAdd.Match(sql);
        return match.Success ? match.Groups["table"].Value : null;
    }

    private static readonly System.Text.RegularExpressions.Regex PeriodAdd = new(
        @"^\s*ALTER\s+TABLE\s+(?<table>\[(?:[^\]]|\]\])+\]\s*\.\s*\[(?:[^\]]|\]\])+\])\s+ADD\s+PERIOD\s+FOR\s+SYSTEM_TIME\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// Reads the entries and returns every batch in them, in order, with the entry each
    /// one came from.
    /// </summary>
    public static async Task<IReadOnlyList<SchemaBatch>> ReadAsync(
        ArchiveReader archive,
        IEnumerable<string> entries,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(entries);

        var batches = new List<SchemaBatch>();

        foreach(var entry in entries)
        {
            var text = await archive.ReadTextAsync(entry, cancellationToken).ConfigureAwait(false);

            // SqlBatchSplitter and not a regular expression over the lines: GO is a
            // client convention, not T-SQL, and one inside a comment or a string literal
            // is just characters. The phase files begin with a comment block, which a
            // line-matching splitter cuts in half.
            var split = SqlBatchSplitter.Split(text);

            for(var i = 0; i < split.Count; i++)
                batches.Add(new SchemaBatch(entry, i, split[i]));
        }

        return batches;
    }

    /// <summary>
    /// Runs the batches against the destination, retrying the ones that failed until a
    /// whole pass makes no progress.
    /// </summary>
    /// <remarks>
    /// The retry is not defensive programming, it is the composer's documented contract:
    /// a module can call a function that a later batch of the same phase creates when the
    /// catalog did not record the edge, and a computed column can call a scalar function
    /// that only exists after the modules phase. The composer marks those batches
    /// <c>Retryable</c> and says a restore driver re-runs them until a pass makes no
    /// progress. The flag does not survive into the archive's text - the files are SQL
    /// and nothing else - so every batch is treated as retryable, which costs a second
    /// attempt at the ones that were going to fail anyway and gets the same answer.
    /// <para>
    /// One connection for the whole group, because the phase files set session options
    /// in a batch of their own and those options belong to the connection.
    /// </para>
    /// </remarks>
    /// <returns>How many batches were executed.</returns>
    public static async Task<int> ExecuteAsync(
        string connectionString,
        IReadOnlyList<SchemaBatch> batches,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(connectionString);
        ArgumentNullException.ThrowIfNull(batches);

        if(batches.Count == 0)
            return 0;

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var pending = batches.ToList();
        var executed = 0;

        while(pending.Count > 0)
        {
            var failed = new List<SchemaBatch>();
            var errors = new List<string>();
            var numbers = new List<int>();

            foreach(var batch in pending)
            {
                try
                {
                    await DestinationCatalog.ExecuteAsync(connection, batch.Sql, commandTimeoutSeconds, cancellationToken)
                        .ConfigureAwait(false);

                    executed++;
                }
                catch(SqlException ex)
                {
                    failed.Add(batch);
                    errors.Add($"{batch.Describe}: {ex.Message}");
                    numbers.Add(ex.Number);
                }
            }

            if(failed.Count == 0)
                return executed;

            // A pass that fixed nothing will not fix anything on the next one either.
            if(failed.Count == pending.Count)
            {
                // The finalize phase is where a restored timeline is handed back to SQL
                // Server, and where it can still be refused - most usefully, over a clock.
                // The server's rule is quoted below; this says what it means here.
                var temporal = TemporalRefusal.Explain(numbers);

                throw new ImportException(
                    $"{Plural(failed.Count, "statement")} of the archive's schema would not run against the " +
                    "destination, and re-running them changed nothing, so this is not an ordering problem. " +
                    "The destination is left with whatever the statements before them did - SQL Server's DDL is " +
                    "transactional per statement, not per script." +
                    (temporal is null
                        ? string.Empty
                        : Environment.NewLine + Environment.NewLine + temporal + " The rows are already in; the " +
                          "statements that failed are the ones listed, and they can be run by hand from the " +
                          "archive's schema/090_finalize.sql once the cause is gone.") +
                    Environment.NewLine + Environment.NewLine +
                    string.Join(Environment.NewLine, errors));
            }

            pending = failed;
        }

        return executed;
    }

    /// <summary>The phase number in an entry name, or <see cref="int.MaxValue"/> when it has none.</summary>
    /// <remarks>
    /// An unnumbered entry sorts last and runs after the data. A newer writer that adds a
    /// phase this build has never heard of is far more likely to have added it at the end
    /// - that is where finalize work goes - and running it late is the reading that can
    /// only be too cautious, while running it early would put statements in front of the
    /// rows they are about.
    /// </remarks>
    private static int Number(string entryName)
    {
        var name = entryName[ArchiveFormat.SchemaDirectory.Length..];
        var digits = 0;

        while(digits < name.Length && char.IsAsciiDigit(name[digits]))
            digits++;

        return digits == 0
            ? int.MaxValue
            : int.Parse(name[..digits], CultureInfo.InvariantCulture);
    }

    private static string Plural(int count, string noun) =>
        count == 1 ? $"1 {noun}" : $"{count.ToString("N0", CultureInfo.InvariantCulture)} {noun}s";
}

/// <summary>One executable batch of one schema phase.</summary>
/// <param name="Entry">The archive entry it came from, so a failure can say which file to open.</param>
/// <param name="Index">Its position in that file, counting from zero.</param>
/// <param name="Sql">The statement or statements, without the <c>GO</c>.</param>
public sealed record SchemaBatch(string Entry, int Index, string Sql)
{
    /// <summary>Where this batch is, in the words an error message uses.</summary>
    public string Describe => $"{Entry}, batch {(Index + 1).ToString(CultureInfo.InvariantCulture)}";
}
