using System.Collections;
using System.Data;
using System.Data.Common;
using System.Data.SqlTypes;
using SqlArchive.Core.Format;

namespace SqlArchive.Core.Import;

/// <summary>
/// One table's rows out of the archive, in the shape <c>SqlBulkCopy</c> reads: a
/// <see cref="DbDataReader"/> that never has more than one row in it.
/// <para>
/// This is the whole of the "bulk loader" the design says import needs, and it is
/// deliberately thin. SyncJob's <c>SqlTableCopier</c> copies between two SQL Servers and
/// takes a source connection string and a <c>SELECT</c>; the rows here come out of a
/// file, and the judgment that engine carries - column matching against a live source,
/// incremental watermarks, retries around a flaky link - is all about the half that does
/// not exist in a restore. What is left is an adapter, and an adapter is not an engine.
/// </para>
/// <para>
/// The values come back already narrowed to the CLR type of the column they are going
/// into - <c>int</c> and not <c>long</c> for an <c>int</c> column - because
/// <see cref="RowDecoder"/> decodes for the format and <c>SqlBulkCopy</c> converts for
/// the wire, and the two meet here. A <c>decimal</c> arrives as <see cref="SqlDecimal"/>
/// and stays one: that is the path through <c>SqlBulkCopy</c> that keeps all 38 digits,
/// and .NET's own <c>decimal</c> holds 28.
/// </para>
/// </summary>
public sealed class ArchiveDataReader : DbDataReader
{
    private readonly ArchiveTableReader _table;
    private readonly IReadOnlyList<ArchiveColumn> _columns;
    private readonly Dictionary<string, int> _ordinals;

    private bool _open = true;

    /// <param name="table">The table's rows, already opened against the archive. This does not dispose it.</param>
    /// <param name="columns">The archived columns, in the archive's order - which is the order the rows are encoded in.</param>
    public ArchiveDataReader(ArchiveTableReader table, IReadOnlyList<ArchiveColumn> columns)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(columns);

        _table = table;
        _columns = columns;
        _ordinals = new Dictionary<string, int>(columns.Count, StringComparer.OrdinalIgnoreCase);

        for(var i = 0; i < columns.Count; i++)
        {
            // A table cannot have two columns of the same name, so a collision here is
            // an archive whose manifest disagrees with itself, and taking the first
            // would load one of the two into both.
            if(!_ordinals.TryAdd(columns[i].Name, i))
            {
                throw new ImportException(
                    $"{table.Table.Identifier} declares the column '{columns[i].Name}' twice in the archive's " +
                    "column list, so there is no way to say which of the two a value belongs to.");
            }
        }
    }

    /// <summary>Rows read so far - what the guard compares against the manifest's count.</summary>
    public long Rows => _table.Rows;

    /// <summary>The running hash of the rows read - what the guard compares against the manifest's hash.</summary>
    public string RowHash => _table.Hash.Value;

    public override int FieldCount => _columns.Count;

    public override bool HasRows => _table.Table.RowCount > 0;

    public override bool IsClosed => !_open;

    public override int Depth => 0;

    public override int RecordsAffected => -1;

    public override object this[int ordinal] => GetValue(ordinal);

    public override object this[string name] => GetValue(GetOrdinal(name));

    public override bool Read() =>
        // SqlBulkCopy takes the asynchronous path for a DbDataReader, so this is here for
        // completeness and for a caller that wants to drain the rows itself. There is no
        // synchronisation context in a console or a test host for it to deadlock against.
        ReadAsync(CancellationToken.None).GetAwaiter().GetResult();

    public override Task<bool> ReadAsync(CancellationToken cancellationToken) =>
        _table.ReadAsync(cancellationToken).AsTask();

    public override object GetValue(int ordinal)
    {
        var value = _table.Current[ordinal];
        return value is null ? DBNull.Value : Narrow(_columns[ordinal], value);
    }

    public override int GetValues(object[] values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var count = Math.Min(values.Length, _columns.Count);

        for(var i = 0; i < count; i++)
            values[i] = GetValue(i);

        return count;
    }

    public override bool IsDBNull(int ordinal) => _table.Current[ordinal] is null;

    public override string GetName(int ordinal) => _columns[ordinal].Name;

    public override string GetDataTypeName(int ordinal) => _columns[ordinal].TypeName;

    public override Type GetFieldType(int ordinal) => ClrType(_columns[ordinal]);

    public override int GetOrdinal(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return _ordinals.TryGetValue(name, out var ordinal)
            ? ordinal
            : throw new IndexOutOfRangeException(
                $"{_table.Table.Identifier} has no archived column called '{name}'.");
    }

    public override bool NextResult() => false;

    public override Task<bool> NextResultAsync(CancellationToken cancellationToken) => Task.FromResult(false);

    public override IEnumerator GetEnumerator() => new DbEnumerator(this, closeReader: false);

    public override bool GetBoolean(int ordinal) => (bool)GetValue(ordinal);

    public override byte GetByte(int ordinal) => (byte)GetValue(ordinal);

    public override char GetChar(int ordinal) => (char)GetValue(ordinal);

    public override DateTime GetDateTime(int ordinal) => (DateTime)GetValue(ordinal);

    public override decimal GetDecimal(int ordinal) => GetValue(ordinal) switch
    {
        SqlDecimal value => value.Value,
        var value => (decimal)value
    };

    public override double GetDouble(int ordinal) => (double)GetValue(ordinal);

    public override float GetFloat(int ordinal) => (float)GetValue(ordinal);

    public override Guid GetGuid(int ordinal) => (Guid)GetValue(ordinal);

    public override short GetInt16(int ordinal) => (short)GetValue(ordinal);

    public override int GetInt32(int ordinal) => (int)GetValue(ordinal);

    public override long GetInt64(int ordinal) => (long)GetValue(ordinal);

    public override string GetString(int ordinal) => (string)GetValue(ordinal);

    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
    {
        var bytes = (byte[])GetValue(ordinal);

        if(buffer is null)
            return bytes.Length;

        var available = (int)Math.Min(bytes.Length - dataOffset, length);
        if(available <= 0)
            return 0;

        Array.Copy(bytes, dataOffset, buffer, bufferOffset, available);
        return available;
    }

    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
    {
        var text = GetString(ordinal);

        if(buffer is null)
            return text.Length;

        var available = (int)Math.Min(text.Length - dataOffset, length);
        if(available <= 0)
            return 0;

        text.CopyTo((int)dataOffset, buffer, bufferOffset, available);
        return available;
    }

    public override DataTable GetSchemaTable() =>
        throw new NotSupportedException(
            "This reader carries the archive's column list, which is what the caller already has; " +
            "SqlBulkCopy does not ask for a schema table and nothing else here should either.");

    public override void Close() => _open = false;

    /// <summary>
    /// The decoded value as the CLR type the destination column takes.
    /// </summary>
    /// <remarks>
    /// <see cref="RowDecoder"/> reads every integer as a <c>long</c>, because that is
    /// what the format says and what a hand-written reader would do. SqlBulkCopy converts
    /// what it is given against the destination's metadata, and a <c>long</c> arriving at
    /// an <c>int</c> column is a conversion it is entitled to refuse. Narrowing here
    /// rather than widening the format keeps the decision where the destination's type is
    /// known, and it is checked: a value that does not fit throws rather than wrapping.
    /// </remarks>
    private static object Narrow(ArchiveColumn column, object value) => column.TypeName switch
    {
        "tinyint" => checked((byte)(long)value),
        "smallint" => checked((short)(long)value),
        "int" => checked((int)(long)value),
        _ => value
    };

    private static Type ClrType(ArchiveColumn column) => column.TypeName switch
    {
        "bit" => typeof(bool),
        "tinyint" => typeof(byte),
        "smallint" => typeof(short),
        "int" => typeof(int),
        "bigint" => typeof(long),

        // Deliberately the SQL type and not decimal: that is the branch of SqlBulkCopy's
        // conversion that keeps all 38 digits a decimal(38,10) can hold, and .NET's
        // decimal holds 28. The exporter reads the same column through SqlDecimal for the
        // same reason - see FORMAT.md.
        "decimal" or "numeric" => typeof(SqlDecimal),

        "money" or "smallmoney" => typeof(decimal),
        "float" => typeof(double),
        "real" => typeof(float),
        "date" or "datetime" or "smalldatetime" or "datetime2" => typeof(DateTime),
        "time" => typeof(TimeSpan),
        "datetimeoffset" => typeof(DateTimeOffset),
        "uniqueidentifier" => typeof(Guid),
        "binary" or "varbinary" or "image" or "timestamp" or "rowversion" => typeof(byte[]),

        // hierarchyid, geography and geometry travel as their own serialisation in a
        // varbinary parameter, which is what lets a restore run without a reference to
        // Microsoft.SqlServer.Types and therefore on Linux.
        "hierarchyid" or "geography" or "geometry" => typeof(byte[]),

        _ => typeof(string)
    };
}
