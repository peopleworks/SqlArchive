using System.Buffers;
using System.Buffers.Text;
using System.Globalization;

namespace SqlArchive.Core.Format;

/// <summary>
/// Writes the canonical JSON of one row into a growable UTF-8 buffer.
/// <para>
/// This is hand-written rather than delegated to <c>Utf8JsonWriter</c>, and that is the
/// single most deliberate decision in this package. The bytes it produces <b>are</b> the
/// definition of equality: two rows are the same row when these bytes are the same. A
/// library's escaping policy is free to change between versions - .NET's own relaxed
/// encoder escapes characters outside the Basic Multilingual Plane today and has no
/// obligation to tomorrow - and the day it changes, every hash in every archive written
/// before it silently stops matching the same rows re-read afterwards. Forty lines of
/// escaping owned here cost less than that.
/// </para>
/// <para>
/// The rules, in full: escape <c>"</c> and <c>\</c>, escape the five control characters
/// that have short forms, escape every other character below U+0020 and U+007F as
/// <c>\u00xx</c> with lowercase hex, and write everything else as raw UTF-8. Nothing
/// else is escaped - not <c>&lt;</c>, not <c>&amp;</c>, not U+2028 - because none of it
/// has to be, and an archive that a person can read is worth more than one that is safe
/// to paste into a script tag.
/// </para>
/// </summary>
public sealed class CanonicalJsonBuffer
{
    /// <summary>U+007F. Not required to be escaped by JSON, and escaped here anyway:
    /// a delete character sitting raw in a line makes the file unreadable in a terminal
    /// for no gain.</summary>
    private const char Delete = '\u007F';

    private byte[] _buffer;
    private int _length;

    public CanonicalJsonBuffer(int capacity = 4096) =>
        _buffer = new byte[Math.Max(capacity, 64)];

    /// <summary>The bytes written so far. Valid until the next write.</summary>
    public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _length);

    /// <summary>The same bytes, for an asynchronous write. Valid until the next write.</summary>
    public ReadOnlyMemory<byte> WrittenMemory => _buffer.AsMemory(0, _length);

    public int Length => _length;

    public void Clear() => _length = 0;

    public void WriteByte(byte value)
    {
        Ensure(1);
        _buffer[_length++] = value;
    }

    public void Write(ReadOnlySpan<byte> value)
    {
        Ensure(value.Length);
        value.CopyTo(_buffer.AsSpan(_length));
        _length += value.Length;
    }

    /// <summary>Writes characters that are known to be ASCII - a number, base64, a date.</summary>
    public void WriteAscii(ReadOnlySpan<char> value)
    {
        Ensure(value.Length);

        for(var i = 0; i < value.Length; i++)
            _buffer[_length + i] = (byte)value[i];

        _length += value.Length;
    }

    public void WriteNull() => WriteAscii("null");

    public void WriteBoolean(bool value) => WriteAscii(value ? "true" : "false");

    public void WriteInt64(long value) => WriteAscii(value.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// Shortest round-trippable, which on .NET Core 3.0 and later is what the default
    /// format gives and is what <c>R</c> also gives. Exponential notation appears for
    /// the extremes (<c>1.7976931348623157E+308</c>) and is valid JSON.
    /// </summary>
    public void WriteDouble(double value)
    {
        if(double.IsFinite(value))
            WriteAscii(value.ToString(CultureInfo.InvariantCulture));
        else
            // SQL Server cannot store these in a float column, so this is unreachable
            // from an export. It is here so that a caller feeding values from somewhere
            // else gets a legal document rather than a bare NaN token, which is not JSON.
            WriteString(value.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Formatted as a <see cref="float"/>, not as the double it widens to: a
    /// <c>real</c> holding 0.1 is <c>0.1</c>, while the same bits read as a double are
    /// <c>0.10000000149011612</c>. Both round-trip; only one of them is the value.
    /// </summary>
    public void WriteSingle(float value)
    {
        if(float.IsFinite(value))
            WriteAscii(value.ToString(CultureInfo.InvariantCulture));
        else
            WriteString(value.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Writes a JSON string. Throws on an unpaired UTF-16 surrogate rather than writing
    /// the replacement character in its place - see <see cref="WriteStringCore"/>.
    /// </summary>
    public void WriteString(string value) => WriteStringCore(value, column: null);

    /// <summary>A JSON string that names the column, so a refusal says where it came from.</summary>
    public void WriteString(string value, string column) => WriteStringCore(value, column);

    public void WriteProperty(string name, bool first)
    {
        if(!first)
            WriteByte((byte)',');

        WriteStringCore(name, column: null);
        WriteByte((byte)':');
    }

    /// <summary>Base64 in the standard alphabet, padded, written as a JSON string.</summary>
    public void WriteBase64(ReadOnlySpan<byte> value)
    {
        WriteByte((byte)'"');

        var length = Base64.GetMaxEncodedToUtf8Length(value.Length);
        Ensure(length);

        var status = Base64.EncodeToUtf8(value, _buffer.AsSpan(_length, length), out _, out var written);
        if(status != OperationStatus.Done)
            throw new ArchiveFormatException($"Base64 encoding of {value.Length} bytes did not complete: {status}.");

        _length += written;
        WriteByte((byte)'"');
    }

    private void WriteStringCore(string value, string? column)
    {
        WriteByte((byte)'"');

        var start = 0;
        var i = 0;

        while(i < value.Length)
        {
            var c = value[i];

            if(char.IsSurrogate(c))
            {
                if(char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                {
                    i += 2;
                    continue;
                }

                // Microsoft.Data.SqlClient decodes nvarchar with UTF-16 replacement, so an
                // unpaired surrogate stored in SQL Server arrives here as U+FFFD and this
                // is unreachable from an export - which is itself worth knowing and is
                // asserted by a live test. If a future driver stops sanitising, the value
                // has no UTF-8 representation, and every library's answer is to substitute
                // U+FFFD quietly. That substitution would be ours, in our bytes, in the
                // definition of equality. So: refuse.
                throw new ArchiveEncodingException(
                    column ?? "?",
                    $"the value contains an unpaired UTF-16 surrogate (U+{(int)c:X4}) at index {i}, which has no " +
                    "UTF-8 representation. Writing it would mean substituting U+FFFD and recording a value that is " +
                    "not the one in the database.");
            }

            var escape = EscapeFor(c);
            if(escape.Length == 0)
            {
                i++;
                continue;
            }

            Flush(value, start, i);
            WriteAscii(escape);
            i++;
            start = i;
        }

        Flush(value, start, i);
        WriteByte((byte)'"');
    }

    private void Flush(string value, int start, int end)
    {
        if(end <= start)
            return;

        var span = value.AsSpan(start, end - start);
        Ensure(ArchiveFormat.Utf8.GetMaxByteCount(span.Length));
        _length += ArchiveFormat.Utf8.GetBytes(span, _buffer.AsSpan(_length));
    }

    private static string EscapeFor(char c) => c switch
    {
        '"' => "\\\"",
        '\\' => "\\\\",
        '\b' => "\\b",
        '\f' => "\\f",
        '\n' => "\\n",
        '\r' => "\\r",
        '\t' => "\\t",
        < ' ' or Delete => Unicode(c),
        _ => string.Empty
    };

    private static string Unicode(char c) =>
        string.Create(6, c, static (span, value) =>
        {
            span[0] = '\\';
            span[1] = 'u';
            const string hex = "0123456789abcdef";
            span[2] = hex[(value >> 12) & 0xF];
            span[3] = hex[(value >> 8) & 0xF];
            span[4] = hex[(value >> 4) & 0xF];
            span[5] = hex[value & 0xF];
        });

    private void Ensure(int extra)
    {
        if(_length + extra <= _buffer.Length)
            return;

        var size = _buffer.Length;
        while(size < _length + extra)
            size *= 2;

        Array.Resize(ref _buffer, size);
    }
}
