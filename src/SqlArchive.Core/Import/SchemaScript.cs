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
internal static class SchemaScript
{
    /// <summary>
    /// The last phase that runs before the data. Phases are numbered in tens with room
    /// between them, and this is the boundary the composer was built around rather than
    /// a number this file chose.
    /// </summary>
    private const int LastPhaseBeforeData = 40;

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
                }
            }

            if(failed.Count == 0)
                return executed;

            // A pass that fixed nothing will not fix anything on the next one either.
            if(failed.Count == pending.Count)
            {
                throw new ImportException(
                    $"{Plural(failed.Count, "statement")} of the archive's schema would not run against the " +
                    "destination, and re-running them changed nothing, so this is not an ordering problem. " +
                    "The destination is left with whatever the statements before them did - SQL Server's DDL is " +
                    "transactional per statement, not per script." +
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
internal sealed record SchemaBatch(string Entry, int Index, string Sql)
{
    /// <summary>Where this batch is, in the words an error message uses.</summary>
    public string Describe => $"{Entry}, batch {(Index + 1).ToString(CultureInfo.InvariantCulture)}";
}
