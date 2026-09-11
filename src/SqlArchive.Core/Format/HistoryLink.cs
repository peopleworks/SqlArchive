using SqlSchemaDiff.Models;

namespace SqlArchive.Core.Format;

/// <summary>
/// Which table of a snapshot is the history of which, read from the one place the
/// snapshot records it.
/// <para>
/// <b>No field of the manifest's table entries names a history table's parent, and none
/// is needed.</b> The parent's own model says it - <c>historyTableSchema</c> and
/// <c>historyTableName</c> - and the history's model says what it is, <c>temporalType</c>
/// <c>HISTORY_TABLE</c>. Both are in the <c>schema</c> an archive already carries, and a
/// third statement of the same fact beside a table's row count would be one more place
/// for the archive to disagree with itself. Everything in this build reads the link here.
/// </para>
/// </summary>
public static class HistoryLink
{
    /// <summary>
    /// The history table <paramref name="table"/> names for itself, or null when it is not
    /// system-versioned. A history table in the parent's own schema may be recorded
    /// without one, which then means the parent's.
    /// </summary>
    public static (string Schema, string Name)? HistoryOf(TableModel table)
    {
        ArgumentNullException.ThrowIfNull(table);

        return string.IsNullOrWhiteSpace(table.HistoryTableName)
            ? null
            : (string.IsNullOrWhiteSpace(table.HistoryTableSchema) ? table.Schema : table.HistoryTableSchema,
               table.HistoryTableName);
    }

    /// <summary>
    /// Every history table the snapshot's tables name, keyed <c>schema.name</c>, with the
    /// table it is the history of.
    /// </summary>
    public static IReadOnlyDictionary<string, TableModel> Parents(DatabaseSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var parents = new Dictionary<string, TableModel>(StringComparer.OrdinalIgnoreCase);

        foreach(var entry in snapshot.Objects)
        {
            if(entry.Type == DbObjectType.Table && entry.Table is { } table && HistoryOf(table) is { } history)
                parents[Key(history.Schema, history.Name)] = table;
        }

        return parents;
    }

    /// <summary>
    /// The snapshot with every history table taken out: the shape a schema diff compares.
    /// </summary>
    /// <remarks>
    /// A migration's diff has to be given this rather than the archive's whole snapshot.
    /// The destination's side never contains a history table - the extractor skips them -
    /// so a diff over the whole archive would propose creating one that SQL Server has
    /// either already created from the parent's <c>SYSTEM_VERSIONING</c> clause or is
    /// about to, and the <c>CREATE</c> collides. The parent's shape is what the diff
    /// aligns, and SQL Server carries a column change on a versioned table into its history
    /// by itself.
    /// </remarks>
    public static DatabaseSnapshot Without(DatabaseSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var histories = Parents(snapshot);

        if(histories.Count == 0)
            return snapshot;

        return With(
            snapshot,
            snapshot.Objects
                .Where(o => o.Type != DbObjectType.Table || !histories.ContainsKey(Key(o.Schema, o.Name)))
                .ToList());
    }

    /// <summary>The same snapshot with another list of objects. Every other property is carried across.</summary>
    public static DatabaseSnapshot With(DatabaseSnapshot snapshot, List<DbSchemaObject> objects)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(objects);

        return new DatabaseSnapshot
        {
            DatabaseName = snapshot.DatabaseName,
            GeneratedAtUtc = snapshot.GeneratedAtUtc,
            Schemas = snapshot.Schemas,
            SchemaOwners = snapshot.SchemaOwners,
            Types = snapshot.Types,
            Objects = objects,
            FormatVersion = snapshot.FormatVersion,
            GeneratedBy = snapshot.GeneratedBy
        };
    }

    private static string Key(string schema, string name) => $"{schema}.{name}";
}
