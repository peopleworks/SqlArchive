using System.Data.Common;
using SqlSchemaDiff.Models;

namespace SqlArchive.Core.Format;

/// <summary>
/// One column as the codec sees it: a name, the system type its values are encoded by,
/// and the precision and scale a restore needs to hand the value back.
/// </summary>
public sealed class ArchiveColumn
{
    public ArchiveColumn(string name, string typeName, byte precision = 0, byte scale = 0, int maxLength = 0)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(typeName);

        Name = name;
        TypeName = Normalise(typeName);
        Precision = precision;
        Scale = scale;
        MaxLength = maxLength;
        Kind = KindOf(TypeName, name);
    }

    public string Name { get; }

    /// <summary>
    /// The system type name in lower case: <c>decimal</c>, <c>nvarchar</c>,
    /// <c>geography</c>. Never an alias type - see <see cref="ArchiveColumns"/>, which
    /// resolves those to what they are built from, because that is what determines the
    /// bytes on the wire and therefore the encoding.
    /// </summary>
    /// <remarks>
    /// Two names can describe one type, so this is not something to compare across the
    /// two ways of building a column. A column declared <c>numeric(19,4)</c> comes back
    /// as <c>numeric</c> from a snapshot, because that is what <c>sys.types</c> kept, and
    /// as <c>decimal</c> from a live reader, because that is the type on the wire - they
    /// are one type under two spellings. <see cref="Kind"/> is what decides the encoding,
    /// and it agrees.
    /// </remarks>
    public string TypeName { get; }

    public byte Precision { get; }

    public byte Scale { get; }

    /// <summary>
    /// <c>sys.columns.max_length</c> in bytes, with -1 for MAX. Zero when the column
    /// was described by a live reader rather than by a snapshot; the codec does not need
    /// it, a restore that builds parameters does.
    /// </summary>
    public int MaxLength { get; }

    public SqlValueKind Kind { get; }

    /// <summary>
    /// A driver reports a CLR user-defined type three-part and database-qualified -
    /// <c>Ventas.sys.geography</c> - so the database name would end up deciding how a
    /// value is encoded. Only the last part means anything.
    /// </summary>
    private static string Normalise(string typeName)
    {
        var last = typeName.LastIndexOf('.');
        var name = last >= 0 ? typeName[(last + 1)..] : typeName;
        return name.Trim('[', ']').ToLowerInvariant();
    }

    private static SqlValueKind KindOf(string typeName, string column) => typeName switch
    {
        "bit" => SqlValueKind.Boolean,
        "tinyint" or "smallint" or "int" or "bigint" => SqlValueKind.Integer,
        "decimal" or "numeric" => SqlValueKind.Decimal,
        "money" or "smallmoney" => SqlValueKind.Money,
        "float" => SqlValueKind.Double,
        "real" => SqlValueKind.Single,
        "date" => SqlValueKind.Date,
        "time" => SqlValueKind.Time,
        "datetime" or "smalldatetime" or "datetime2" => SqlValueKind.DateTime,
        "datetimeoffset" => SqlValueKind.DateTimeOffset,
        "char" or "varchar" or "nchar" or "nvarchar" or "text" or "ntext" or "xml" => SqlValueKind.String,
        "uniqueidentifier" => SqlValueKind.Guid,
        "binary" or "varbinary" or "image" or "timestamp" or "rowversion" => SqlValueKind.Binary,
        "hierarchyid" or "geography" or "geometry" => SqlValueKind.UserDefinedBinary,

        "sql_variant" => throw new ArchiveEncodingException(
            column,
            "sql_variant is not supported in format version 1. Every other type says what it is in the schema; " +
            "a sql_variant says it per row, and the base type, precision and collation of each value would have " +
            "to be carried alongside it for a restore not to guess. Exclude the table's data, or drop the column."),

        _ => throw new ArchiveEncodingException(
            column,
            $"type '{typeName}' is not one this build knows how to encode. Refusing rather than writing a " +
            "best-effort string: an archive is read once, years later, and a value that was guessed at is " +
            "indistinguishable from one that was right.")
    };
}

/// <summary>
/// Which of a table's columns carry data into the archive, and in which order.
/// <para>
/// Two kinds of column are left out, and both for the same reason: SQL Server will not
/// accept them back. A computed column has no value of its own, and a <c>rowversion</c>
/// is stamped by the server from a counter no <c>INSERT</c> can write to. Writing either
/// would produce an archive whose restore fails on the first row, or - worse - one whose
/// <c>verify</c> can never pass because the destination necessarily holds different
/// values.
/// </para>
/// <para>
/// The two columns of a <c>SYSTEM_TIME</c> period are <b>not</b> in that list, although
/// they used to be. SQL Server refuses to be told them only while the period exists, and
/// a restore can create the table without it, load the rows with their own
/// <c>ValidFrom</c> and <c>ValidTo</c>, and add the period afterwards - measured against
/// SQL Server 2025, and the reason <c>ComposeOptions.PeriodAfterData</c> exists. Without
/// them every restored row starts at the instant of the restore, and <c>FOR SYSTEM_TIME
/// AS OF</c> any moment before it answers nothing.
/// </para>
/// <para>
/// A history table goes through the same rule as any other table, and that is enough:
/// SQL Server keeps the parent's identity as a plain column there, and its computed
/// columns as real ones holding data, so the columns the parent leaves out are exactly
/// the ones its history carries. The rule is read off the history table's own catalog
/// entry and never derived from the parent's.
/// </para>
/// </summary>
public static class ArchiveColumns
{
    /// <summary><c>sys.columns.generated_always_type</c> of a period's <c>ROW START</c> column.</summary>
    public const byte PeriodStart = 1;

    /// <summary><c>sys.columns.generated_always_type</c> of a period's <c>ROW END</c> column.</summary>
    public const byte PeriodEnd = 2;

    /// <summary>
    /// The archived columns of a table, in <c>column_id</c> order, with alias types
    /// resolved to the system type underneath them.
    /// </summary>
    /// <param name="table">The table, as the snapshot describes it.</param>
    /// <param name="aliasTypes">
    /// The snapshot's <see cref="DatabaseSnapshot.Types"/>. A column declared
    /// <c>dbo.Dinero</c> is encoded as the <c>decimal(19,4)</c> that type is built from,
    /// which is also exactly what a live reader reports for the same column.
    /// </param>
    public static IReadOnlyList<ArchiveColumn> For(TableModel table, IEnumerable<AliasTypeModel>? aliasTypes = null)
    {
        ArgumentNullException.ThrowIfNull(table);

        var aliases = (aliasTypes ?? Array.Empty<AliasTypeModel>())
            .ToDictionary(t => $"{t.Schema}.{t.Name}", StringComparer.OrdinalIgnoreCase);

        var columns = new List<ArchiveColumn>(table.Columns.Count);

        foreach(var column in table.Columns)
        {
            if(!IsArchived(column))
                continue;

            var typeName = column.TypeName;
            var precision = column.Precision;
            var scale = column.Scale;
            var maxLength = column.MaxLength;

            if(column.IsUserDefinedType &&
               aliases.TryGetValue($"{column.TypeSchema}.{column.TypeName}", out var alias))
            {
                typeName = alias.BaseTypeName;
                precision = alias.Precision;
                scale = alias.Scale;
                maxLength = alias.MaxLength;
            }

            columns.Add(new ArchiveColumn(column.Name, typeName, precision, scale, maxLength));
        }

        return columns;
    }

    /// <summary>
    /// The columns of an open reader, in the order the query put them. Used by export
    /// and by <c>verify</c> against a live database, both of which have a result set in
    /// hand and no need to consult a snapshot.
    /// </summary>
    /// <remarks>
    /// The driver reports the system type behind an alias type, so this path and
    /// <see cref="For"/> agree without either of them knowing about the other. That is
    /// asserted by a live test rather than assumed.
    /// </remarks>
    public static IReadOnlyList<ArchiveColumn> ForReader(DbDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        var columns = new List<ArchiveColumn>(reader.FieldCount);

        for(var i = 0; i < reader.FieldCount; i++)
            columns.Add(new ArchiveColumn(reader.GetName(i), reader.GetDataTypeName(i)));

        return columns;
    }

    /// <summary>
    /// False for a column whose value the archive neither carries nor compares.
    /// </summary>
    public static bool IsArchived(ColumnModel column)
    {
        ArgumentNullException.ThrowIfNull(column);

        // A computed column is derived from the others. SQL Server refuses an INSERT
        // that names one, and its value in the destination follows from the values that
        // were restored, so carrying it would add bytes and a way to be wrong.
        if(column.IsComputed)
            return false;

        // A rowversion is assigned by the server from a counter that belongs to one
        // database. It cannot be inserted - "Cannot insert an explicit value into a
        // timestamp column" - and the restored row necessarily gets a different one, so
        // a hash that included it could never match after a restore. That holds in a
        // history table too, where SQL Server keeps the parent's rowversion as a
        // timestamp column of its own: declaring it binary(8) instead so the old values
        // could be written is refused when versioning adopts the table (13525), and a
        // column cannot be altered to timestamp afterwards (4927). Measured on 2025.
        if(IsRowVersion(column.TypeName))
            return false;

        // ROW START and ROW END of a SYSTEM_TIME period are data. They are refused by an
        // INSERT only while the period exists - even with SYSTEM_VERSIONING off - and a
        // restore creates the table without the period, loads them, and adds it after.
        // Any other kind of GENERATED ALWAYS column - the transaction and sequence
        // columns of a ledger table - is still the server's to write.
        return column.GeneratedAlwaysType == 0 || IsPeriodColumn(column);
    }

    /// <summary>True for the <c>ROW START</c> or <c>ROW END</c> column of a <c>SYSTEM_TIME</c> period.</summary>
    public static bool IsPeriodColumn(ColumnModel column)
    {
        ArgumentNullException.ThrowIfNull(column);
        return column.GeneratedAlwaysType is PeriodStart or PeriodEnd;
    }

    /// <summary>
    /// The columns a table declares but the archive does not carry, with the reason.
    /// Written into the README so someone reading the archive without the tool is not
    /// left wondering where a column went.
    /// </summary>
    public static IReadOnlyList<(string Column, string Reason)> Omitted(TableModel table)
    {
        ArgumentNullException.ThrowIfNull(table);

        var omitted = new List<(string, string)>();

        foreach(var column in table.Columns)
        {
            if(column.IsComputed)
                omitted.Add((column.Name, "computed"));
            else if(IsRowVersion(column.TypeName))
                omitted.Add((column.Name, "rowversion"));
            else if(!IsArchived(column))
                omitted.Add((column.Name, "GENERATED ALWAYS"));
        }

        return omitted;
    }

    private static bool IsRowVersion(string typeName) =>
        typeName.Equals("timestamp", StringComparison.OrdinalIgnoreCase) ||
        typeName.Equals("rowversion", StringComparison.OrdinalIgnoreCase);
}
