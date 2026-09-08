using SqlSchemaDiff.Models;

namespace SqlArchive.Core.Export;

/// <summary>
/// Cuts a schema snapshot down to the tables the export is keeping.
/// <para>
/// A table left out by a glob is left out of the schema too, not only out of the data.
/// The alternative - keeping every <c>CREATE TABLE</c> and listing only some tables in
/// the manifest - produces an archive whose schema promises tables that have no rows and
/// no entry saying why, which is the same failure that <c>dataSkipped</c> exists to
/// avoid. Here the archive is exactly the tables it names.
/// </para>
/// </summary>
public static class SnapshotSelection
{
    /// <summary>
    /// The snapshot with the unselected tables removed, and with whatever else referred
    /// to them removed with them.
    /// </summary>
    /// <param name="snapshot">The extracted schema.</param>
    /// <param name="keep">Whether a table, by schema and name, is being archived.</param>
    /// <param name="databaseName">
    /// The name to record. Under a database snapshot the extractor read a database called
    /// <c>Ventas_sqlarchive_1a2b3c4d</c> that will not exist in an hour, and the archive
    /// has to say <c>Ventas</c> - both because that is where the rows came from and
    /// because a later diff matches on it.
    /// </param>
    /// <param name="notices">Where each removal is written down, because a removal is a decision the operator should see.</param>
    public static DatabaseSnapshot Restrict(
        DatabaseSnapshot snapshot,
        Func<string, string, bool> keep,
        string? databaseName,
        IList<string> notices)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(keep);
        ArgumentNullException.ThrowIfNull(notices);

        var kept = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dropped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach(var table in Tables(snapshot))
        {
            if(keep(table.Schema, table.Name))
                kept.Add(Key(table.Schema, table.Name));
            else
                dropped.Add(Key(table.Schema, table.Name));
        }

        // A system-versioned table and its history table are one object as far as the
        // server is concerned: the finalize phase turns versioning back on and names the
        // history table, and it cannot do that if the glob took only one of the pair.
        foreach(var table in Tables(snapshot))
        {
            if(!kept.Contains(Key(table.Schema, table.Name)) || table.HistoryTableName is not { Length: > 0 })
                continue;

            var history = Key(table.HistoryTableSchema ?? table.Schema, table.HistoryTableName);

            if(dropped.Remove(history))
            {
                kept.Add(history);
                notices.Add(
                    $"[{table.Schema}].[{table.Name}] is system-versioned, so its history table " +
                    $"[{table.HistoryTableSchema}].[{table.HistoryTableName}] is archived with it even though the " +
                    "table filters left it out.");
            }
        }

        var objects = new List<DbSchemaObject>(snapshot.Objects.Count);

        foreach(var entry in snapshot.Objects)
        {
            if(entry.Type == DbObjectType.Table)
            {
                if(dropped.Contains(Key(entry.Schema, entry.Name)))
                    continue;

                objects.Add(WithoutDanglingForeignKeys(entry, dropped, notices));
                continue;
            }

            // A trigger belongs to its table and cannot be created without it.
            if(entry.Type == DbObjectType.Trigger && entry.Trigger is { } trigger &&
               dropped.Contains(Key(trigger.ParentSchema, trigger.ParentName)))
            {
                notices.Add(
                    $"Trigger [{entry.Schema}].[{entry.Name}] is not archived: its table " +
                    $"[{trigger.ParentSchema}].[{trigger.ParentName}] was left out by the table filters.");

                continue;
            }

            objects.Add(entry);
        }

        return new DatabaseSnapshot
        {
            DatabaseName = databaseName ?? snapshot.DatabaseName,
            GeneratedAtUtc = snapshot.GeneratedAtUtc,
            Schemas = snapshot.Schemas,
            SchemaOwners = snapshot.SchemaOwners,
            Types = snapshot.Types,
            Objects = objects,
            FormatVersion = snapshot.FormatVersion,
            GeneratedBy = snapshot.GeneratedBy
        };
    }

    /// <summary>Every table in the snapshot, as its model.</summary>
    public static IEnumerable<TableModel> Tables(DatabaseSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        foreach(var entry in snapshot.Objects)
        {
            if(entry.Type == DbObjectType.Table && entry.Table is { } table)
                yield return table;
        }
    }

    /// <summary>
    /// A kept table whose foreign keys point at a table that was not kept, with those
    /// keys removed. Without this the foreign-key phase of the schema does not run, and
    /// an archive whose schema will not execute is not an archive.
    /// </summary>
    private static DbSchemaObject WithoutDanglingForeignKeys(
        DbSchemaObject entry,
        HashSet<string> dropped,
        IList<string> notices)
    {
        var table = entry.Table!;

        var dangling = table.ForeignKeys
            .Where(fk => dropped.Contains(Key(fk.ReferencedSchema, fk.ReferencedTable)))
            .ToList();

        if(dangling.Count == 0)
            return entry;

        // Clone is a shallow copy, so the list has to be replaced rather than edited -
        // editing it would reach through into the snapshot the caller still holds.
        var trimmed = table.Clone();
        trimmed.ForeignKeys = table.ForeignKeys.Except(dangling).ToList();

        foreach(var fk in dangling)
        {
            notices.Add(
                $"Foreign key [{fk.Name}] on [{table.Schema}].[{table.Name}] is not archived: it points at " +
                $"[{fk.ReferencedSchema}].[{fk.ReferencedTable}], which the table filters left out.");
        }

        return new DbSchemaObject
        {
            Type = entry.Type,
            Schema = entry.Schema,
            Name = entry.Name,
            Definition = entry.Definition,
            Dependencies = entry.Dependencies,
            Table = trimmed,
            UsesAnsiNulls = entry.UsesAnsiNulls,
            UsesQuotedIdentifier = entry.UsesQuotedIdentifier,
            Trigger = entry.Trigger,
            Sequence = entry.Sequence,
            TableType = entry.TableType,
            Synonym = entry.Synonym
        };
    }

    private static string Key(string schema, string name) => $"{schema}.{name}";
}
