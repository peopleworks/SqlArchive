using System.Data;
using Microsoft.Data.SqlClient;
using SqlSchemaDiff.Services;

namespace SqlArchive.Core.Import;

/// <summary>
/// The few questions a restore has to ask the destination's own catalog, rather than the
/// archive, because the answer decides what is done to it.
/// <para>
/// Not a second schema reader. Everything about the <i>shape</i> comes from
/// <c>SqlServerSchemaExtractor</c> like everywhere else in this tool; what is here is the
/// state of a particular table at a particular moment - is this column an identity now,
/// is this foreign key trusted now - which a snapshot taken before the schema was altered
/// cannot answer.
/// </para>
/// </summary>
internal static class DestinationCatalog
{
    /// <summary>The columns of one table as they are, in <c>column_id</c> order. Empty when the table is not there.</summary>
    public static async Task<IReadOnlyList<DestinationColumn>> ColumnsAsync(
        SqlConnection connection,
        string schema,
        string name,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        const string sql =
            """
            SELECT c.name, c.is_identity, c.is_computed, c.generated_always_type, t.name,
                   c.is_nullable, CASE WHEN c.default_object_id <> 0 THEN 1 ELSE 0 END
            FROM sys.columns AS c
            JOIN sys.types AS t ON t.user_type_id = c.user_type_id
            WHERE c.object_id = OBJECT_ID(@table)
            ORDER BY c.column_id;
            """;

        await using var command = new SqlCommand(sql, connection) { CommandTimeout = commandTimeoutSeconds };
        command.Parameters.Add(new SqlParameter("@table", SqlDbType.NVarChar, 520) { Value = SqlRender.Quote(schema, name) });

        var columns = new List<DestinationColumn>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while(await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var typeName = reader.GetString(4);

            columns.Add(new DestinationColumn(
                reader.GetString(0),
                reader.GetBoolean(1),
                reader.GetBoolean(2),
                reader.GetByte(3) != 0,
                typeName.Equals("timestamp", StringComparison.OrdinalIgnoreCase) ||
                typeName.Equals("rowversion", StringComparison.OrdinalIgnoreCase),
                reader.GetBoolean(5),
                reader.GetInt32(6) == 1));
        }

        return columns;
    }

    /// <summary>
    /// Every foreign key in the database, with the two tables it joins and whether it is
    /// enabled and trusted today.
    /// </summary>
    /// <remarks>
    /// All of them in one round trip, and the ones that matter picked out here. A restore
    /// that asked per table would ask once per table, and the answer for the whole
    /// database is a few hundred rows.
    /// </remarks>
    public static async Task<IReadOnlyList<DestinationForeignKey>> ForeignKeysAsync(
        SqlConnection connection,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        const string sql =
            """
            SELECT fk.name,
                   SCHEMA_NAME(parent.schema_id), parent.name,
                   SCHEMA_NAME(referenced.schema_id), referenced.name,
                   fk.is_disabled, fk.is_not_trusted
            FROM sys.foreign_keys AS fk
            JOIN sys.tables AS parent ON parent.object_id = fk.parent_object_id
            JOIN sys.tables AS referenced ON referenced.object_id = fk.referenced_object_id;
            """;

        await using var command = new SqlCommand(sql, connection) { CommandTimeout = commandTimeoutSeconds };

        var keys = new List<DestinationForeignKey>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while(await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            keys.Add(new DestinationForeignKey(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetBoolean(5),
                reader.GetBoolean(6)));
        }

        return keys;
    }

    /// <summary>Runs one statement. Used for the handful of ALTERs a restore issues outside the phases.</summary>
    public static async Task ExecuteAsync(
        SqlConnection connection,
        string sql,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = commandTimeoutSeconds };
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>One column of the destination as the catalog has it now.</summary>
/// <param name="Name">Its name.</param>
/// <param name="IsIdentity">Whether the server assigns its values.</param>
/// <param name="IsComputed">Whether it has no value of its own.</param>
/// <param name="IsGeneratedAlways">
/// A <c>ROW START</c> or <c>ROW END</c> period column. True whether or not system
/// versioning is on, which is the point: the column is there and refuses to be written
/// either way.
/// </param>
/// <param name="IsRowVersion">A <c>rowversion</c>, which the server stamps.</param>
/// <param name="IsNullable">Whether a row may leave it out.</param>
/// <param name="HasDefault">Whether a row that leaves it out gets something rather than failing.</param>
public sealed record DestinationColumn(
    string Name,
    bool IsIdentity,
    bool IsComputed,
    bool IsGeneratedAlways,
    bool IsRowVersion,
    bool IsNullable,
    bool HasDefault)
{
    /// <summary>False when SQL Server assigns the value and refuses to be told it.</summary>
    public bool IsWritable => !IsComputed && !IsGeneratedAlways && !IsRowVersion;
}

/// <summary>One foreign key of the destination, and the state a restore has to put back.</summary>
/// <param name="Name">The constraint's name.</param>
/// <param name="ParentSchema">Schema of the table that carries the key.</param>
/// <param name="ParentName">The table that carries the key.</param>
/// <param name="ReferencedSchema">Schema of the table it points at.</param>
/// <param name="ReferencedName">The table it points at.</param>
/// <param name="IsDisabled">Whether it was already switched off before this restore touched anything.</param>
/// <param name="IsNotTrusted">Whether SQL Server had already stopped trusting it.</param>
public sealed record DestinationForeignKey(
    string Name,
    string ParentSchema,
    string ParentName,
    string ReferencedSchema,
    string ReferencedName,
    bool IsDisabled,
    bool IsNotTrusted)
{
    /// <summary>The three-part name a message uses, so a reader can find the constraint.</summary>
    public string Identifier => $"[{ParentSchema}].[{ParentName}].[{Name}]";

    /// <summary>The table that carries the key - the one an ALTER has to name.</summary>
    public string Parent => SqlRender.Quote(ParentSchema, ParentName);

    /// <summary>The statement that switches the key off for the length of the data phase.</summary>
    public string Lower => $"ALTER TABLE {Parent} NOCHECK CONSTRAINT {SqlRender.Quote(Name)};";

    /// <summary>
    /// The statement that puts the key back into the state it was in.
    /// </summary>
    /// <param name="revalidate">
    /// Whether a key that was trusted is validated on the way back. A key that was
    /// <i>already</i> untrusted goes back untrusted whatever this says: enabling it WITH
    /// CHECK would silently improve the destination, and a restore that improves things
    /// nobody asked about is a restore nobody can predict.
    /// </param>
    public string Raise(bool revalidate) =>
        $"ALTER TABLE {Parent} WITH {(revalidate && !IsNotTrusted ? "CHECK" : "NOCHECK")} " +
        $"CHECK CONSTRAINT {SqlRender.Quote(Name)};";
}
