using Microsoft.Data.SqlClient;
using SqlArchive.Core.Format;
using SqlSchemaDiff.Models;
using SqlSchemaDiff.Services;

namespace SqlArchive.Core.Export;

/// <summary>
/// The history tables of a database's system-versioned tables, read back into the schema
/// snapshot as tables in their own right.
/// <para>
/// <c>SqlServerSchemaExtractor.ExtractAsync</c> leaves every history table out, and it is
/// right to for a schema diff: the <c>SYSTEM_VERSIONING</c> clause on the parent creates
/// one, so scripting it as well would produce a <c>CREATE</c> that collides. For an
/// archive it is the whole history of the table going missing without a word. So each
/// one is read back by name - <c>ExtractTableAsync</c> returns a history table when asked
/// for it, which the whole-database read never does - and added to the snapshot as an
/// ordinary table: a <c>CREATE TABLE</c> in <c>040_tables.sql</c>, its own entry in the
/// manifest, its own rows, count and hash. <c>090_finalize.sql</c> already names it in
/// <c>HISTORY_TABLE = ...</c>, so versioning adopts it, rows and all.
/// </para>
/// <para>
/// <b>Its shape is read, never derived.</b> Measured on SQL Server 2025: the parent's
/// identity is a plain column in the history, a computed column of the parent is a real
/// one there holding data, the period columns are plain <c>datetime2</c>, a rowversion
/// stays a <c>timestamp</c>, and every key, default and check is gone. A history table
/// built from the parent by rule gets at least one of those wrong.
/// </para>
/// </summary>
public static class HistoryTables
{
    /// <summary>
    /// Reads the history table of every system-versioned table in
    /// <paramref name="snapshot"/> and returns the snapshot with them added.
    /// </summary>
    /// <param name="extractor">
    /// The extractor. Its <c>Notices</c> are cleared by every per-table read, so the caller
    /// has to have taken what the whole-database read left in them before calling this.
    /// </param>
    /// <param name="connection">
    /// An open connection to the database the snapshot came from - the one the rows are
    /// read through, where the session has one, so the history's shape and its parent's
    /// come out of the same session.
    /// </param>
    /// <param name="transaction">The connection's pending transaction, or null.</param>
    /// <param name="snapshot">The whole-database snapshot.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public static async Task<HistoryTablesRead> AddAsync(
        SqlServerSchemaExtractor extractor,
        SqlConnection connection,
        SqlTransaction? transaction,
        DatabaseSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(extractor);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(snapshot);

        var present = SnapshotSelection.Tables(snapshot)
            .Select(t => $"{t.Schema}.{t.Name}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var objects = new List<DbSchemaObject>(snapshot.Objects.Count);
        var read = new List<HistoryTableRead>();
        var missing = new List<string>();
        var notices = new List<string>();

        foreach(var entry in snapshot.Objects)
        {
            objects.Add(entry);

            if(entry.Type != DbObjectType.Table || entry.Table is not { } parent || HistoryLink.HistoryOf(parent) is not { } history)
                continue;

            // Already there when the whole-database read did not skip it. Archived all
            // the same, just not twice.
            if(!present.Add($"{history.Schema}.{history.Name}"))
                continue;

            var model = await extractor
                .ExtractTableAsync(connection, transaction, history.Schema, history.Name, cancellationToken)
                .ConfigureAwait(false);

            notices.AddRange(extractor.Notices);

            if(model is null)
            {
                missing.Add($"[{history.Schema}].[{history.Name}]");
                continue;
            }

            // Placed right after its parent, so 040_tables.sql reads in pairs. The order
            // means nothing to the server: neither table has an edge to the other, and
            // versioning is not turned on until the finalize phase.
            objects.Add(new DbSchemaObject
            {
                Type = DbObjectType.Table,
                Schema = model.Schema,
                Name = model.Name,
                Definition = SqlRender.BuildTableCreateScript(model),
                Table = model
            });

            read.Add(new HistoryTableRead(model.Schema, model.Name, parent.Schema, parent.Name));
        }

        return new HistoryTablesRead(HistoryLink.With(snapshot, objects), read, missing, notices);
    }

    /// <summary>
    /// The extractor's notices with its "skipped ... history table" line taken out for
    /// every history table that was read after all - left in, it would tell the operator
    /// the one thing that is no longer true.
    /// </summary>
    public static IEnumerable<string> WithoutSkipsOf(IEnumerable<string> notices, HistoryTablesRead read)
    {
        ArgumentNullException.ThrowIfNull(notices);
        ArgumentNullException.ThrowIfNull(read);

        var skips = read.Read
            .Select(h => $"skipped [{h.Schema}].[{h.Name}]: history table")
            .ToArray();

        return notices.Where(n => !skips.Any(s => n.StartsWith(s, StringComparison.OrdinalIgnoreCase)));
    }
}

/// <summary>One history table read back from the catalog, and the table it belongs to.</summary>
public sealed record HistoryTableRead(string Schema, string Name, string ParentSchema, string ParentName);

/// <summary>What <see cref="HistoryTables.AddAsync"/> found.</summary>
/// <param name="Snapshot">The snapshot with every history table in it as a table.</param>
/// <param name="Read">Each history table added, with the table it is the history of.</param>
/// <param name="Missing">
/// History tables a parent names that the catalog could not produce - a table dropped
/// between the two reads. Empty in every case this build has seen, and never ignored.
/// </param>
/// <param name="Notices">What the per-table reads had to say.</param>
public sealed record HistoryTablesRead(
    DatabaseSnapshot Snapshot,
    IReadOnlyList<HistoryTableRead> Read,
    IReadOnlyList<string> Missing,
    IReadOnlyList<string> Notices);
