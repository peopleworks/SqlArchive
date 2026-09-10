using System.Data;
using Microsoft.Data.SqlClient;
using SqlArchive.Core.Format;
using SqlSchemaDiff.Services;

namespace SqlArchive.Core.Verify;

/// <summary>
/// What a manifest would say about a table that is in a database rather than in an
/// archive: how many rows it has, and the sum of the SHA-256 of each of them encoded the
/// way <c>FORMAT.md</c> says.
/// </summary>
/// <param name="Rows">Rows read.</param>
/// <param name="RowHash">
/// Lowercase hex, no prefix, directly comparable with <see cref="ArchiveTableEntry.RowHash"/>.
/// </param>
public sealed record TableDigest(long Rows, string RowHash);

/// <summary>
/// Reads a live table and answers with its <see cref="TableDigest"/>.
/// <para>
/// Two callers, and they are asking the same question of different tables. <c>verify</c>
/// asks it of a restored or a production table, to say whether it still matches the
/// archive. <c>import</c> asks it of the staging table it has just filled, because the
/// guard here is exact rather than heuristic: the manifest says how many rows there
/// should be and what they should hash to, so a truncated archive or a half-read entry
/// is caught before the swap instead of after it.
/// </para>
/// <para>
/// It is deliberately not the exporter. The exporter cuts a table into ranges, spools
/// them, resumes and writes files; none of that is wanted when the answer is two values
/// and no bytes are kept. What the two share is the only part that has to agree - the
/// encoder, through <see cref="JsonlRowWriter"/> writing into <see cref="Stream.Null"/>.
/// Sharing the encoder is not an optimisation, it is the definition: if this hashed rows
/// its own way, the two sides of every comparison would be answering different questions.
/// </para>
/// </summary>
public static class LiveTableDigest
{
    /// <summary>
    /// Reads <paramref name="schema"/>.<paramref name="name"/> through
    /// <paramref name="connection"/>, which must be open, and hashes it.
    /// </summary>
    /// <param name="connection">
    /// An open connection. The caller owns it, and owns its transaction: <c>verify</c>
    /// reads several tables through connections of its own, and <c>import</c> reads
    /// staging through the connection that is about to publish it.
    /// </param>
    /// <param name="schema">Schema of the table to read.</param>
    /// <param name="name">Name of the table to read.</param>
    /// <param name="columns">
    /// The archived columns, in the archive's order. From the manifest side of the
    /// comparison and never from the live table: the encoding is positional, so reading
    /// the live table's own column order would hash a different thing whenever the two
    /// disagree - which is exactly the case being investigated.
    /// </param>
    /// <param name="rowFilter">
    /// The <c>--where</c> the table was exported with, or null. Composed the same way
    /// export composes it, wrapped in parentheses, so that both sides of a comparison
    /// cover the same rows.
    /// </param>
    /// <param name="commandTimeoutSeconds">Zero for no limit.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public static async Task<TableDigest> ComputeAsync(
        SqlConnection connection,
        string schema,
        string name,
        IReadOnlyList<ArchiveColumn> columns,
        string? rowFilter = null,
        int commandTimeoutSeconds = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(columns);

        if(columns.Count == 0)
        {
            throw new ArgumentException(
                "A table with no archived columns has nothing to hash; the caller has to decide what that means.",
                nameof(columns));
        }

        var qualified = SqlRender.Quote(schema, name);

        if(connection.State != ConnectionState.Open)
        {
            throw new InvalidOperationException(
                $"the connection handed in for {qualified} is {connection.State}; this reads through the " +
                "caller's connection and never opens one of its own, so that it can be given the connection " +
                "already inside a transaction");
        }

        var select = "SELECT " + string.Join(", ", columns.Select(c => SqlRender.Quote(c.Name))) +
                     " FROM " + qualified +
                     (string.IsNullOrWhiteSpace(rowFilter) ? string.Empty : $" WHERE ({rowFilter})") +
                     ";";

        await using var command = new SqlCommand(select, connection)
        {
            CommandTimeout = commandTimeoutSeconds
        };

        // SequentialAccess for the same reason the exporter uses it: it is what lets a
        // varbinary(max) be hashed without being assembled in memory first. The encoder
        // reads its columns strictly left to right, which is the condition for it.
        await using var reader = await command
            .ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken).ConfigureAwait(false);

        var writer = new JsonlRowWriter(Stream.Null, columns);

        await using(writer.ConfigureAwait(false))
        {
            var rows = await writer.WriteAllAsync(reader, cancellationToken).ConfigureAwait(false);
            return new TableDigest(rows, writer.Hash.Value);
        }
    }

    /// <summary>
    /// The same, opening and closing a connection of its own. The shape for a caller
    /// hashing one table, or hashing several in parallel with a connection each.
    /// </summary>
    public static async Task<TableDigest> ComputeAsync(
        string connectionString,
        string schema,
        string name,
        IReadOnlyList<ArchiveColumn> columns,
        string? rowFilter = null,
        int commandTimeoutSeconds = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        return await ComputeAsync(
            connection, schema, name, columns, rowFilter, commandTimeoutSeconds, cancellationToken)
            .ConfigureAwait(false);
    }
}
