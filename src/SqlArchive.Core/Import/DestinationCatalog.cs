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
                reader.GetByte(3),
                typeName.Equals("timestamp", StringComparison.OrdinalIgnoreCase) ||
                typeName.Equals("rowversion", StringComparison.OrdinalIgnoreCase),
                reader.GetBoolean(5),
                reader.GetInt32(6) == 1));
        }

        return columns;
    }

    /// <summary>
    /// Every table of the destination that has a <c>SYSTEM_TIME</c> period, with its
    /// history table when it is system-versioned, as the catalog has them now.
    /// </summary>
    /// <remarks>
    /// Read from <c>sys.periods</c> rather than from <c>temporal_type</c>, because a period
    /// is what refuses a written <c>ValidFrom</c> and it can exist with versioning off -
    /// which is exactly the state <c>temporal_type</c> reports as 0.
    /// </remarks>
    public static async Task<IReadOnlyList<DestinationTemporalTable>> TemporalTablesAsync(
        SqlConnection connection,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        // sys.tables.history_retention_period arrived in SQL Server 2017, and a batch
        // naming a column that does not exist does not compile at all - so it is asked
        // about before it is asked for.
        var retention = await ScalarAsync(
            connection, "SELECT COL_LENGTH('sys.tables', 'history_retention_period');", commandTimeoutSeconds, cancellationToken)
            .ConfigureAwait(false) is not (null or DBNull);

        var sql =
            $"""
             SELECT SCHEMA_NAME(t.schema_id), t.name,
                    sc.name, sc.is_hidden, ec.name, ec.is_hidden,
                    CASE WHEN t.temporal_type = 2 THEN 1 ELSE 0 END,
                    SCHEMA_NAME(h.schema_id), h.name,
                    {(retention ? "t.history_retention_period, t.history_retention_period_unit_desc" : "NULL, NULL")}
             FROM sys.periods AS p
             JOIN sys.tables AS t ON t.object_id = p.object_id
             JOIN sys.columns AS sc ON sc.object_id = p.object_id AND sc.column_id = p.start_column_id
             JOIN sys.columns AS ec ON ec.object_id = p.object_id AND ec.column_id = p.end_column_id
             LEFT JOIN sys.tables AS h ON h.object_id = t.history_table_id
             WHERE p.period_type = 1;
             """;

        await using var command = new SqlCommand(sql, connection) { CommandTimeout = commandTimeoutSeconds };

        var tables = new List<DestinationTemporalTable>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while(await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            tables.Add(new DestinationTemporalTable(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetBoolean(3),
                reader.GetString(4),
                reader.GetBoolean(5),
                reader.GetInt32(6) == 1,
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetInt32(9),
                reader.IsDBNull(10) ? null : reader.GetString(10)));
        }

        return tables;
    }

    /// <summary>Whether a table has a <c>SYSTEM_TIME</c> period right now.</summary>
    public static async Task<bool> HasPeriodAsync(
        SqlConnection connection,
        string quotedTable,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            "SELECT CASE WHEN EXISTS (SELECT 1 FROM sys.periods WHERE object_id = OBJECT_ID(@table) AND period_type = 1) THEN 1 ELSE 0 END;",
            connection)
        {
            CommandTimeout = commandTimeoutSeconds
        };

        command.Parameters.Add(new SqlParameter("@table", SqlDbType.NVarChar, 520) { Value = quotedTable });

        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private static async Task<object?> ScalarAsync(
        SqlConnection connection,
        string sql,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = commandTimeoutSeconds };
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
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
/// <param name="GeneratedAlwaysType">
/// <c>sys.columns.generated_always_type</c>: 0 for an ordinary column, 1 and 2 for the
/// two columns of a <c>SYSTEM_TIME</c> period, higher for a ledger table's. A period
/// column refuses to be written for as long as the period exists, whether or not system
/// versioning is on.
/// </param>
/// <param name="IsRowVersion">A <c>rowversion</c>, which the server stamps.</param>
/// <param name="IsNullable">Whether a row may leave it out.</param>
/// <param name="HasDefault">Whether a row that leaves it out gets something rather than failing.</param>
public sealed record DestinationColumn(
    string Name,
    bool IsIdentity,
    bool IsComputed,
    byte GeneratedAlwaysType,
    bool IsRowVersion,
    bool IsNullable,
    bool HasDefault)
{
    /// <summary>Any kind of <c>GENERATED ALWAYS</c> column, a period's or a ledger's.</summary>
    public bool IsGeneratedAlways => GeneratedAlwaysType != 0;

    /// <summary>
    /// The <c>ROW START</c> or <c>ROW END</c> of a <c>SYSTEM_TIME</c> period. Refused by a
    /// plain <c>INSERT</c>, and written all the same by a restore, which takes the period
    /// off for the length of one transaction - see <c>TemporalPublisher</c>.
    /// </summary>
    public bool IsPeriod => GeneratedAlwaysType is Format.ArchiveColumns.PeriodStart or Format.ArchiveColumns.PeriodEnd;

    /// <summary>False when a plain <c>INSERT</c> cannot write it: SQL Server assigns the value and refuses to be told it.</summary>
    public bool IsWritable => !IsComputed && !IsGeneratedAlways && !IsRowVersion;
}

/// <summary>
/// A table of the destination that has a <c>SYSTEM_TIME</c> period, as it is right now -
/// everything a restore has to take off to write its rows' own periods, and put back
/// exactly as it found it.
/// </summary>
/// <param name="Schema">Its schema.</param>
/// <param name="Name">Its name.</param>
/// <param name="PeriodStart">The <c>ROW START</c> column.</param>
/// <param name="PeriodStartHidden">Whether that column is <c>HIDDEN</c>.</param>
/// <param name="PeriodEnd">The <c>ROW END</c> column.</param>
/// <param name="PeriodEndHidden">Whether that column is <c>HIDDEN</c>.</param>
/// <param name="IsVersioned">Whether <c>SYSTEM_VERSIONING</c> is on.</param>
/// <param name="HistorySchema">The history table's schema, when versioning is on.</param>
/// <param name="HistoryName">The history table, when versioning is on.</param>
/// <param name="RetentionPeriod">
/// <c>history_retention_period</c>: -1 for infinite, null where the server predates it.
/// </param>
/// <param name="RetentionUnit"><c>history_retention_period_unit_desc</c>, beside it.</param>
public sealed record DestinationTemporalTable(
    string Schema,
    string Name,
    string PeriodStart,
    bool PeriodStartHidden,
    string PeriodEnd,
    bool PeriodEndHidden,
    bool IsVersioned,
    string? HistorySchema,
    string? HistoryName,
    int? RetentionPeriod,
    string? RetentionUnit)
{
    /// <summary><c>schema.name</c>, the key the importer plans by.</summary>
    public string Key => $"{Schema}.{Name}";

    /// <summary>The history table's key, when there is one.</summary>
    public string? HistoryKey => IsVersioned && HistoryName is not null ? $"{HistorySchema}.{HistoryName}" : null;

    /// <summary>The table, quoted.</summary>
    public string Quoted => SqlRender.Quote(Schema, Name);

    /// <summary>The history table, quoted, when there is one.</summary>
    public string? HistoryQuoted => HistoryKey is null ? null : SqlRender.Quote(HistorySchema!, HistoryName!);

    /// <summary>
    /// The <c>SYSTEM_VERSIONING = ON</c> that puts this table back as it was found: the same
    /// history table, the same retention, and the consistency check SQL Server runs by
    /// default - which is what refuses a history that would not answer <c>AS OF</c>
    /// coherently.
    /// </summary>
    public string VersioningOn
    {
        get
        {
            var options = $"HISTORY_TABLE = {HistoryQuoted}, DATA_CONSISTENCY_CHECK = ON";

            // Only a finite retention is restated. INFINITE is what a SET without the
            // option leaves anyway, and a server that predates the option has none.
            if(RetentionPeriod is > 0 && RetentionUnit is { Length: > 0 } unit && !unit.Equals("INFINITE", StringComparison.OrdinalIgnoreCase))
                options += $", HISTORY_RETENTION_PERIOD = {RetentionPeriod.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)} {unit}";

            return $"ALTER TABLE {Quoted} SET (SYSTEM_VERSIONING = ON ({options}));";
        }
    }
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
