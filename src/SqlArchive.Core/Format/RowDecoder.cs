using System.Data;
using System.Data.SqlTypes;
using System.Globalization;
using System.Text.Json;

namespace SqlArchive.Core.Format;

/// <summary>
/// Reads a canonical JSONL line back into the .NET values a restore hands to SQL Server.
/// <para>
/// This is the encoding table read backwards, and it lives beside the table rather than
/// in the import package on purpose: a format whose two directions are written by two
/// people at two times is a format that eventually disagrees with itself.
/// </para>
/// <para>
/// It is strict. A property the columns do not account for, or a column the line does
/// not carry, is an error rather than something to skip: this is the one place where
/// quietly carrying on means loading a table with a column silently left at its default.
/// </para>
/// </summary>
public static class RowDecoder
{
    /// <summary>
    /// Decodes one line into <paramref name="destination"/>, which must have one slot
    /// per column. Nulls come back as <see langword="null"/>, not <see cref="DBNull"/>;
    /// a caller building parameters converts.
    /// </summary>
    public static void Decode(IReadOnlyList<ArchiveColumn> columns, ReadOnlySpan<byte> line, object?[] destination)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(destination);

        if(destination.Length != columns.Count)
            throw new ArgumentException(
                $"The row needs {columns.Count} slots and has {destination.Length}.", nameof(destination));

        Array.Clear(destination);
        Span<bool> seen = columns.Count <= 128 ? stackalloc bool[columns.Count] : new bool[columns.Count];

        var reader = new Utf8JsonReader(line, isFinalBlock: true, state: default);

        Expect(ref reader, JsonTokenType.StartObject);

        var next = 0;

        while(reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var index = Match(columns, ref reader, next);

            if(seen[index])
                throw new ArchiveFormatException($"The row names column '{columns[index].Name}' twice.");

            if(!reader.Read())
                throw new ArchiveFormatException($"The row ends after the name of column '{columns[index].Name}'.");

            destination[index] = ReadValue(columns[index], ref reader);
            seen[index] = true;
            next = index + 1;
        }

        if(reader.TokenType != JsonTokenType.EndObject)
            throw new ArchiveFormatException($"The row does not end with an object; found {reader.TokenType}.");

        for(var i = 0; i < columns.Count; i++)
        {
            if(!seen[i])
                throw new ArchiveFormatException(
                    $"The row carries no value for column '{columns[i].Name}'. A missing column is refused rather " +
                    "than loaded as its default, because a default that was never in the source is not a restore.");
        }
    }

    /// <summary>Decodes one line into a fresh array.</summary>
    public static object?[] Decode(IReadOnlyList<ArchiveColumn> columns, ReadOnlySpan<byte> line)
    {
        ArgumentNullException.ThrowIfNull(columns);

        var values = new object?[columns.Count];
        Decode(columns, line, values);
        return values;
    }

    /// <summary>
    /// The parameter type a decoded value goes back to the server as. The encoding table
    /// read backwards one more step, kept here so a restore does not have to reinvent it.
    /// </summary>
    /// <remarks>
    /// <c>hierarchyid</c>, <c>geography</c> and <c>geometry</c> go back as
    /// <see cref="SqlDbType.VarBinary"/>. That is not a workaround: SQL Server converts a
    /// varbinary to a CLR type column implicitly, and it means a restore needs no
    /// reference to <c>Microsoft.SqlServer.Types</c> and therefore runs on Linux.
    /// Verified against a live server for all three.
    /// </remarks>
    public static SqlDbType ParameterType(ArchiveColumn column)
    {
        ArgumentNullException.ThrowIfNull(column);

        return column.TypeName switch
        {
            "bit" => SqlDbType.Bit,
            "tinyint" => SqlDbType.TinyInt,
            "smallint" => SqlDbType.SmallInt,
            "int" => SqlDbType.Int,
            "bigint" => SqlDbType.BigInt,
            "decimal" or "numeric" => SqlDbType.Decimal,
            "money" => SqlDbType.Money,
            "smallmoney" => SqlDbType.SmallMoney,
            "float" => SqlDbType.Float,
            "real" => SqlDbType.Real,
            "date" => SqlDbType.Date,
            "time" => SqlDbType.Time,
            "datetime" => SqlDbType.DateTime,
            "smalldatetime" => SqlDbType.SmallDateTime,
            "datetime2" => SqlDbType.DateTime2,
            "datetimeoffset" => SqlDbType.DateTimeOffset,
            "char" => SqlDbType.Char,
            "varchar" => SqlDbType.VarChar,
            "text" => SqlDbType.Text,
            "nchar" => SqlDbType.NChar,
            "nvarchar" => SqlDbType.NVarChar,
            "ntext" => SqlDbType.NText,
            "xml" => SqlDbType.Xml,
            "uniqueidentifier" => SqlDbType.UniqueIdentifier,
            "binary" => SqlDbType.Binary,
            "image" => SqlDbType.Image,
            "varbinary" or "timestamp" or "rowversion" => SqlDbType.VarBinary,
            "hierarchyid" or "geography" or "geometry" => SqlDbType.VarBinary,
            _ => throw new ArchiveEncodingException(column.Name, $"type '{column.TypeName}' has no parameter type.")
        };
    }

    /// <summary>
    /// Property order is part of the canonical form, so the common case is a hit on the
    /// column that comes next and no search at all. A line whose properties are in a
    /// different order still reads - it is legal JSON saying the same thing - it just
    /// costs a scan.
    /// </summary>
    private static int Match(IReadOnlyList<ArchiveColumn> columns, ref Utf8JsonReader reader, int next)
    {
        if(next < columns.Count && reader.ValueTextEquals(columns[next].Name))
            return next;

        for(var i = 0; i < columns.Count; i++)
        {
            if(reader.ValueTextEquals(columns[i].Name))
                return i;
        }

        throw new ArchiveFormatException(
            $"The row names a column, '{reader.GetString()}', that the table does not have. Refusing rather than " +
            "skipping it: a value with nowhere to go means the archive and the schema disagree about the table.");
    }

    private static object? ReadValue(ArchiveColumn column, ref Utf8JsonReader reader)
    {
        if(reader.TokenType == JsonTokenType.Null)
            return null;

        try
        {
            return column.Kind switch
            {
                SqlValueKind.Boolean => reader.GetBoolean(),
                SqlValueKind.Integer => reader.GetInt64(),
                SqlValueKind.Decimal => SqlDecimal.Parse(reader.GetString()!),
                SqlValueKind.Money => decimal.Parse(reader.GetString()!, NumberStyles.Number, CultureInfo.InvariantCulture),
                SqlValueKind.Double => reader.TokenType == JsonTokenType.String
                    ? double.Parse(reader.GetString()!, NumberStyles.Float, CultureInfo.InvariantCulture)
                    : reader.GetDouble(),
                SqlValueKind.Single => reader.TokenType == JsonTokenType.String
                    ? float.Parse(reader.GetString()!, NumberStyles.Float, CultureInfo.InvariantCulture)
                    : reader.GetSingle(),
                SqlValueKind.Date => DateTime.ParseExact(
                    reader.GetString()!, RowEncoder.DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None),
                SqlValueKind.DateTime => DateTime.ParseExact(
                    reader.GetString()!, DateTimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None),
                SqlValueKind.Time => TimeSpan.ParseExact(reader.GetString()!, TimeFormats, CultureInfo.InvariantCulture),
                SqlValueKind.DateTimeOffset => DateTimeOffset.ParseExact(
                    reader.GetString()!, DateTimeOffsetFormats, CultureInfo.InvariantCulture, DateTimeStyles.None),
                SqlValueKind.String => reader.GetString(),
                SqlValueKind.Guid => Guid.ParseExact(reader.GetString()!, "D"),
                SqlValueKind.Binary or SqlValueKind.UserDefinedBinary => reader.GetBytesFromBase64(),
                _ => throw new ArchiveEncodingException(column.Name, $"unhandled value kind {column.Kind}.")
            };
        }
        catch(Exception ex) when(ex is FormatException or InvalidOperationException or OverflowException or ArgumentException)
        {
            throw new ArchiveFormatException(
                $"Column '{column.Name}' ({column.TypeName}) could not be read back from the archive: {ex.Message}", ex);
        }
    }

    // The encoder writes the fraction only where there is one, so both shapes are legal
    // and a reader that accepted only the long one would reject half the archive.
    private static readonly string[] DateTimeFormats =
    [
        "yyyy-MM-ddTHH:mm:ss",
        "yyyy-MM-ddTHH:mm:ss.FFFFFFF"
    ];

    private static readonly string[] DateTimeOffsetFormats =
    [
        "yyyy-MM-ddTHH:mm:sszzz",
        "yyyy-MM-ddTHH:mm:ss.FFFFFFFzzz"
    ];

    private static readonly string[] TimeFormats =
    [
        @"hh\:mm\:ss",
        @"hh\:mm\:ss\.FFFFFFF"
    ];

    private static void Expect(ref Utf8JsonReader reader, JsonTokenType expected)
    {
        if(!reader.Read() || reader.TokenType != expected)
            throw new ArchiveFormatException($"Expected {expected} at the start of the row.");
    }
}
