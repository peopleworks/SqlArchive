using System.Data.Common;
using System.Data.SqlTypes;
using System.Globalization;

namespace SqlArchive.Core.Format;

/// <summary>
/// Turns one row into the canonical JSONL line that defines it.
/// <para>
/// A row is a JSON object on a single line, its properties in the order the columns were
/// given, with no whitespace anywhere. The full table of what each SQL type becomes is
/// in <c>FORMAT.md</c> and is normative: it is not an implementation detail of the
/// exporter, it is the definition of what it means for two rows to be equal.
/// </para>
/// <para>
/// Both entry points end in the same place. <see cref="Encode(IReadOnlyList{ArchiveColumn}, DbDataReader, CanonicalJsonBuffer)"/>
/// pulls values out of a live reader and hands them to
/// <see cref="Encode(IReadOnlyList{ArchiveColumn}, IReadOnlyList{object?}, CanonicalJsonBuffer)"/>,
/// which is what actually writes. That is deliberate: a row read from SQL Server and the
/// same row decoded back out of a JSONL file have to produce identical bytes, and the
/// cheapest way to be sure of that is for there to be only one piece of code that can.
/// </para>
/// </summary>
public static class RowEncoder
{
    /// <summary>
    /// The date and time formats. <c>FFFFFFF</c> writes only the fractional digits the
    /// value has, and drops the point with them when there are none.
    /// <para>
    /// The trimming is not tidiness. SyncJob found against a real server, and this
    /// package confirmed again on SQL Server 2025, that <c>datetime</c> refuses any
    /// string with seven fractional digits in it - <c>'2026-01-15T10:00:00.0000000'</c>
    /// raises "conversion failed when converting date and/or time from character string"
    /// while <c>'2026-01-15T10:00:00'</c> converts - so a padded form would produce an
    /// archive whose lines cannot be pasted back into the server they came from. A value
    /// that does carry seven digits can only have come from a <c>datetime2</c>, which
    /// accepts them.
    /// </para>
    /// </summary>
    public const string DateTimeFormat = "yyyy-MM-ddTHH:mm:ss.FFFFFFF";

    public const string DateFormat = "yyyy-MM-dd";

    public const string DateTimeOffsetFormat = "yyyy-MM-ddTHH:mm:ss.FFFFFFFzzz";

    /// <summary>
    /// <c>TimeSpan</c> does not behave like <c>DateTime</c> here: with a zero fraction
    /// it leaves the point behind and writes <c>10:00:00.</c>, which is neither ISO 8601
    /// nor accepted by SQL Server. So a whole-second time is written with the other
    /// format instead of relying on the specifier to drop the point.
    /// </summary>
    private const string TimeFormat = @"hh\:mm\:ss\.FFFFFFF";

    private const string WholeSecondTimeFormat = @"hh\:mm\:ss";

    /// <summary>
    /// Encodes the row the reader is positioned on. The buffer is cleared first, so the
    /// same buffer is reused for every row of a table.
    /// </summary>
    public static void Encode(IReadOnlyList<ArchiveColumn> columns, DbDataReader reader, CanonicalJsonBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(buffer);

        var values = new object?[columns.Count];

        for(var i = 0; i < columns.Count; i++)
            values[i] = Read(columns[i], reader, i);

        Encode(columns, values, buffer);
    }

    /// <summary>
    /// Encodes a row already in .NET values - what <see cref="RowDecoder"/> produces, and
    /// what a unit test can build by hand.
    /// </summary>
    public static void Encode(IReadOnlyList<ArchiveColumn> columns, IReadOnlyList<object?> values, CanonicalJsonBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(buffer);

        if(values.Count != columns.Count)
            throw new ArgumentException(
                $"The row has {values.Count} values and the table has {columns.Count} columns.", nameof(values));

        buffer.Clear();
        buffer.WriteByte((byte)'{');

        for(var i = 0; i < columns.Count; i++)
        {
            buffer.WriteProperty(columns[i].Name, first: i == 0);
            WriteValue(columns[i], values[i], buffer);
        }

        buffer.WriteByte((byte)'}');
    }

    /// <summary>
    /// Pulls one value out of the reader as the .NET type its encoding is defined over.
    /// </summary>
    /// <remarks>
    /// Two of these accessors are load-bearing and neither is the obvious one.
    /// <para>
    /// <c>decimal</c> and <c>numeric</c> go through <see cref="SqlDecimal"/> because
    /// SQL Server allows 38 digits of precision and .NET's <see cref="decimal"/> holds
    /// 28: <c>GetValue</c> on a <c>decimal(38,10)</c> column carrying its largest value
    /// throws <c>OverflowException: Conversion overflows</c>. An exporter that used the
    /// ordinary accessor would fall over on exactly the data that most needs archiving.
    /// </para>
    /// <para>
    /// <c>money</c> goes through <see cref="SqlMoney"/> and then to
    /// <see cref="SqlMoney.Value"/>, never to <c>SqlMoney.ToString()</c>. That method
    /// writes a <i>variable</i> number of decimals - at least two, with anything beyond
    /// them trimmed - so one column would archive <c>1</c> as <c>"1.00"</c> and
    /// <c>1.0001</c> as <c>"1.0001"</c>, at two different scales, and a value would
    /// encode differently depending on whether it happened to be round.
    /// <see cref="SqlMoney.Value"/> gives the four decimals the type always has, which is
    /// what <c>decimal</c> and <c>numeric</c> do with their declared scale.
    /// </para>
    /// </remarks>
    private static object? Read(ArchiveColumn column, DbDataReader reader, int ordinal)
    {
        if(reader.IsDBNull(ordinal))
            return null;

        return column.Kind switch
        {
            SqlValueKind.Boolean => reader.GetBoolean(ordinal),
            SqlValueKind.Integer => ReadInteger(column, reader, ordinal),
            SqlValueKind.Decimal => reader.GetFieldValue<SqlDecimal>(ordinal),
            SqlValueKind.Money => reader.GetFieldValue<SqlMoney>(ordinal).Value,
            SqlValueKind.Double => reader.GetDouble(ordinal),
            SqlValueKind.Single => reader.GetFloat(ordinal),
            SqlValueKind.Date or SqlValueKind.DateTime => reader.GetDateTime(ordinal),
            SqlValueKind.Time => reader.GetFieldValue<TimeSpan>(ordinal),
            SqlValueKind.DateTimeOffset => reader.GetFieldValue<DateTimeOffset>(ordinal),
            SqlValueKind.String => reader.GetString(ordinal),
            SqlValueKind.Guid => reader.GetGuid(ordinal),

            // GetBytes rather than GetValue, and that is what makes hierarchyid,
            // geography and geometry work at all: their .NET values cannot be
            // materialised without Microsoft.SqlServer.Types, which is a Windows-only
            // package this library does not reference, while their raw serialisation
            // comes down the wire like any other varbinary and goes straight back in
            // through a varbinary parameter.
            SqlValueKind.Binary or SqlValueKind.UserDefinedBinary => ReadBytes(reader, ordinal),

            _ => throw new ArchiveEncodingException(column.Name, $"unhandled value kind {column.Kind}.")
        };
    }

    private static object ReadInteger(ArchiveColumn column, DbDataReader reader, int ordinal) => column.TypeName switch
    {
        "tinyint" => (long)reader.GetByte(ordinal),
        "smallint" => (long)reader.GetInt16(ordinal),
        "int" => (long)reader.GetInt32(ordinal),
        _ => reader.GetInt64(ordinal)
    };

    private static byte[] ReadBytes(DbDataReader reader, int ordinal)
    {
        var length = reader.GetBytes(ordinal, 0, null, 0, 0);
        var bytes = new byte[length];

        var read = 0L;
        while(read < length)
            read += reader.GetBytes(ordinal, read, bytes, (int)read, (int)Math.Min(int.MaxValue, length - read));

        return bytes;
    }

    private static void WriteValue(ArchiveColumn column, object? value, CanonicalJsonBuffer buffer)
    {
        if(value is null or DBNull)
        {
            buffer.WriteNull();
            return;
        }

        switch(column.Kind)
        {
            case SqlValueKind.Boolean:
                // A JSON boolean, not 1 and 0. bit is a two-valued type and JSON has a
                // two-valued type; writing it as a number would leave a reader guessing
                // whether the 1 in a column it does not know the type of is a bit or an
                // int, and would make the archive lie about what the value is.
                buffer.WriteBoolean(Convert.ToBoolean(value, CultureInfo.InvariantCulture));
                return;

            case SqlValueKind.Integer:
                buffer.WriteInt64(Convert.ToInt64(value, CultureInfo.InvariantCulture));
                return;

            case SqlValueKind.Decimal:
                // A JSON string, because a JSON number would be read back through a
                // double by most parsers and a decimal(38,10) does not fit in one.
                // SqlDecimal.ToString writes every digit the type declares and no more:
                // a decimal(19,4) holding one is "1.0000", which is neither padding nor
                // loss - it is the value as the column defines it.
                buffer.WriteString(DecimalText(column, value), column.Name);
                return;

            case SqlValueKind.Money:
                buffer.WriteString(
                    Convert.ToDecimal(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
                    column.Name);
                return;

            case SqlValueKind.Double:
                buffer.WriteDouble(Convert.ToDouble(value, CultureInfo.InvariantCulture));
                return;

            case SqlValueKind.Single:
                buffer.WriteSingle(Convert.ToSingle(value, CultureInfo.InvariantCulture));
                return;

            case SqlValueKind.Date:
                buffer.WriteString(AsDateTime(value).ToString(DateFormat, CultureInfo.InvariantCulture), column.Name);
                return;

            case SqlValueKind.DateTime:
                buffer.WriteString(AsDateTime(value).ToString(DateTimeFormat, CultureInfo.InvariantCulture), column.Name);
                return;

            case SqlValueKind.Time:
                buffer.WriteString(TimeText(column, value), column.Name);
                return;

            case SqlValueKind.DateTimeOffset:
                buffer.WriteString(
                    ((DateTimeOffset)value).ToString(DateTimeOffsetFormat, CultureInfo.InvariantCulture),
                    column.Name);
                return;

            case SqlValueKind.String:
                buffer.WriteString((string)value, column.Name);
                return;

            case SqlValueKind.Guid:
                // Lower case, because every other hex string this format writes - the
                // row hash, the file hashes - is lower case, and one of the two had to
                // be picked for the bytes to be stable. SQL Server displays them upper
                // case and reads either.
                buffer.WriteString(((Guid)value).ToString("D", CultureInfo.InvariantCulture), column.Name);
                return;

            case SqlValueKind.Binary:
            case SqlValueKind.UserDefinedBinary:
                buffer.WriteBase64((byte[])value);
                return;

            default:
                throw new ArchiveEncodingException(column.Name, $"unhandled value kind {column.Kind}.");
        }
    }

    private static string DecimalText(ArchiveColumn column, object value) => value switch
    {
        SqlDecimal sql => sql.ToString(),
        decimal clr => clr.ToString(CultureInfo.InvariantCulture),
        _ => throw new ArchiveEncodingException(
            column.Name, $"a decimal column cannot carry a {value.GetType().Name}.")
    };

    private static DateTime AsDateTime(object value) => value switch
    {
        DateTime moment => moment,
        DateOnly day => day.ToDateTime(TimeOnly.MinValue),
        _ => (DateTime)Convert.ChangeType(value, typeof(DateTime), CultureInfo.InvariantCulture)
    };

    private static string TimeText(ArchiveColumn column, object value)
    {
        var span = value switch
        {
            TimeSpan time => time,
            TimeOnly time => time.ToTimeSpan(),
            _ => throw new ArchiveEncodingException(
                column.Name, $"a time column cannot carry a {value.GetType().Name}.")
        };

        if(span < TimeSpan.Zero || span >= TimeSpan.FromDays(1))
            throw new ArchiveEncodingException(
                column.Name,
                $"the value {span} is outside the range a SQL Server time column can hold, so it has no " +
                "representation in this format.");

        return span.Ticks % TimeSpan.TicksPerSecond == 0
            ? span.ToString(WholeSecondTimeFormat, CultureInfo.InvariantCulture)
            : span.ToString(TimeFormat, CultureInfo.InvariantCulture);
    }
}
