using System.Data.SqlTypes;
using System.Text;
using SqlArchive.Core.Format;

namespace SqlArchive.Core.Tests;

/// <summary>
/// The JSONL entries: writing them, reading them back, and the two directions agreeing.
/// </summary>
public sealed class FormatJsonlTests
{
    private static readonly ArchiveColumn[] Columns =
    [
        new("Id", "int"),
        new("Name", "nvarchar"),
        new("Balance", "decimal"),
        new("When", "datetime2"),
        new("Photo", "varbinary")
    ];

    private static object?[] Row(long id, string? name) =>
    [
        id,
        name,
        SqlDecimal.Parse("1250.0000"),
        new DateTime(2026, 8, 27, 9, 14, 22).AddTicks(1234567),
        new byte[] { 1, 2, 3 }
    ];

    private static async Task<byte[]> WriteAsync(params object?[][] rows)
    {
        var stream = new MemoryStream();
        var writer = new JsonlRowWriter(stream, Columns);

        await using(writer)
        {
            foreach(var row in rows)
                await writer.WriteAsync(row);
        }

        return stream.ToArray();
    }

    [Fact]
    public async Task EveryLineEndsWithASingleNewlineOnEveryPlatform()
    {
        var bytes = await WriteAsync(Row(1, "a"), Row(2, "b"));
        var text = Encoding.UTF8.GetString(bytes);

        Assert.DoesNotContain('\r', text);
        Assert.Equal(2, text.Count(c => c == '\n'));
        Assert.EndsWith("\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThereIsNoByteOrderMark()
    {
        var bytes = await WriteAsync(Row(1, "a"));

        Assert.Equal((byte)'{', bytes[0]);
    }

    /// <summary>
    /// The terminator is not part of a row's hash. If it were, the last row of a range
    /// file and the same row in the middle of a whole-table file would hash differently,
    /// and re-reading a table with other range boundaries would look like drift.
    /// </summary>
    [Fact]
    public async Task TheLineTerminatorIsNotHashed()
    {
        var stream = new MemoryStream();
        var writer = new JsonlRowWriter(stream, Columns);
        string value;

        await using(writer)
        {
            await writer.WriteAsync(Row(1, "a"));
            value = writer.Hash.Value;
        }

        var line = Encoding.UTF8.GetString(stream.ToArray()).TrimEnd('\n');

        var expected = new RowHash.Accumulator();
        expected.AddRow(Encoding.UTF8.GetBytes(line));

        Assert.Equal(expected.Value, value);
    }

    [Fact]
    public async Task RowsSurviveTheRoundTrip()
    {
        var bytes = await WriteAsync(Row(1, "Acme"), Row(2, "Böhm & Co"), Row(3, null));

        var read = new List<object?[]>();
        var reader = new JsonlRowReader(new MemoryStream(bytes), Columns);

        await using(reader)
        {
            while(await reader.ReadAsync())
                read.Add(reader.Current.ToArray());
        }

        Assert.Equal(3, read.Count);
        Assert.Equal(1L, read[0][0]);
        Assert.Equal("Acme", read[0][1]);
        Assert.Equal(SqlDecimal.Parse("1250.0000").ToString(), read[0][2]!.ToString());
        Assert.Equal(new DateTime(2026, 8, 27, 9, 14, 22).AddTicks(1234567), read[0][3]);
        Assert.Equal(new byte[] { 1, 2, 3 }, (byte[])read[0][4]!);
        Assert.Null(read[2][1]);
    }

    /// <summary>
    /// The invariant the whole format rests on: a row that goes out and comes back
    /// encodes to the same bytes. If this ever failed, a restore would produce a
    /// destination that verify could not match, and nobody would find out until the
    /// restore.
    /// </summary>
    [Fact]
    public async Task ARowDecodedAndEncodedAgainIsByteIdentical()
    {
        var bytes = await WriteAsync(Row(1, "Acme"), Row(2, null));
        var buffer = new CanonicalJsonBuffer();
        var reader = new JsonlRowReader(new MemoryStream(bytes), Columns);

        await using(reader)
        {
            while(await reader.ReadAsync())
            {
                var line = reader.CurrentLine.ToArray();
                RowEncoder.Encode(Columns, reader.Current, buffer);

                Assert.Equal(line, buffer.WrittenSpan.ToArray());
            }
        }
    }

    /// <summary>
    /// The hash a reader reports is over the bytes on disk, not over a re-encoding of
    /// what they decoded to. That is what makes it answer "is this the file the manifest
    /// describes?" rather than the weaker question.
    /// </summary>
    [Fact]
    public async Task ReadingAndWritingAgreeOnTheHash()
    {
        var stream = new MemoryStream();
        var writer = new JsonlRowWriter(stream, Columns);
        string written;

        await using(writer)
        {
            await writer.WriteAsync(Row(1, "a"));
            await writer.WriteAsync(Row(2, "b"));
            written = writer.Hash.Value;
        }

        var reader = new JsonlRowReader(new MemoryStream(stream.ToArray()), Columns);

        await using(reader)
        {
            while(await reader.ReadAsync()) { }

            Assert.Equal(written, reader.Hash.Value);
            Assert.Equal(2, reader.Hash.Rows);
        }
    }

    /// <summary>
    /// An archive copied through something that translated the line endings is damaged
    /// but readable, and the carriage return is not part of the row.
    /// </summary>
    [Fact]
    public async Task CarriageReturnsAreStrippedBeforeHashing()
    {
        var unix = await WriteAsync(Row(1, "a"), Row(2, "b"));
        var windows = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(unix).Replace("\n", "\r\n", StringComparison.Ordinal));

        var first = new JsonlRowReader(new MemoryStream(unix), Columns);
        var second = new JsonlRowReader(new MemoryStream(windows), Columns);

        await using(first)
        await using(second)
        {
            while(await first.ReadAsync()) { }
            while(await second.ReadAsync()) { }

            Assert.Equal(first.Hash.Value, second.Hash.Value);
            Assert.Equal(2, second.Rows);
        }
    }

    [Fact]
    public async Task ALastLineWithNoTerminatorIsStillARow()
    {
        var bytes = await WriteAsync(Row(1, "a"), Row(2, "b"));
        var truncated = bytes[..^1];

        var reader = new JsonlRowReader(new MemoryStream(truncated), Columns);

        await using(reader)
        {
            while(await reader.ReadAsync()) { }

            Assert.Equal(2, reader.Rows);
        }
    }

    /// <summary>
    /// A varbinary(max) is one line. The reader's buffer starts at 64 KB and has to grow
    /// rather than give up.
    /// </summary>
    [Fact]
    public async Task ARowLargerThanTheReadBufferIsRead()
    {
        var columns = new[] { new ArchiveColumn("Blob", "varbinary") };
        var blob = new byte[300 * 1024];
        Random.Shared.NextBytes(blob);

        var stream = new MemoryStream();
        var writer = new JsonlRowWriter(stream, columns);

        await using(writer)
            await writer.WriteAsync(new object?[] { blob });

        var reader = new JsonlRowReader(new MemoryStream(stream.ToArray()), columns);

        await using(reader)
        {
            Assert.True(await reader.ReadAsync());
            Assert.Equal(blob, (byte[])reader.Current[0]!);
            Assert.False(await reader.ReadAsync());
        }
    }

    [Fact]
    public async Task ManyRowsCrossTheFlushThreshold()
    {
        var rows = Enumerable.Range(1, 5000).Select(i => Row(i, $"row {i}")).ToArray();
        var bytes = await WriteAsync(rows);

        var reader = new JsonlRowReader(new MemoryStream(bytes), Columns);

        await using(reader)
        {
            while(await reader.ReadAsync()) { }

            Assert.Equal(5000, reader.Rows);
        }
    }

    /// <summary>
    /// A column the line does not carry is refused rather than loaded as its default. A
    /// default that was never in the source is not a restore.
    /// </summary>
    [Fact]
    public void AMissingColumnIsAnError()
    {
        var line = Encoding.UTF8.GetBytes("""{"Id":1,"Name":"a","Balance":"1.0000","When":"2026-01-01T00:00:00"}""");

        var error = Assert.Throws<ArchiveFormatException>(() => RowDecoder.Decode(Columns, line));

        Assert.Contains("no value for column 'Photo'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AColumnTheTableDoesNotHaveIsAnError()
    {
        var line = Encoding.UTF8.GetBytes(
            """{"Id":1,"Name":"a","Balance":"1.0000","When":"2026-01-01T00:00:00","Photo":null,"Extra":1}""");

        var error = Assert.Throws<ArchiveFormatException>(() => RowDecoder.Decode(Columns, line));

        Assert.Contains("'Extra'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARepeatedColumnIsAnError()
    {
        var line = Encoding.UTF8.GetBytes(
            """{"Id":1,"Id":2,"Name":"a","Balance":"1.0000","When":"2026-01-01T00:00:00","Photo":null}""");

        var error = Assert.Throws<ArchiveFormatException>(() => RowDecoder.Decode(Columns, line));

        Assert.Contains("twice", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Property order is part of the canonical form, but a line that says the same thing
    /// in another order is still legal JSON saying the same thing, and reading it costs a
    /// scan rather than a refusal.
    /// </summary>
    [Fact]
    public void PropertiesOutOfOrderStillRead()
    {
        var line = Encoding.UTF8.GetBytes(
            """{"Photo":null,"When":"2026-01-01T00:00:00","Balance":"1.0000","Name":"a","Id":1}""");

        var values = RowDecoder.Decode(Columns, line);

        Assert.Equal(1L, values[0]);
        Assert.Equal("a", values[1]);
    }

    [Fact]
    public void AValueOfTheWrongShapeSaysWhichColumn()
    {
        var line = Encoding.UTF8.GetBytes(
            """{"Id":1,"Name":"a","Balance":"not a number","When":"2026-01-01T00:00:00","Photo":null}""");

        var error = Assert.Throws<ArchiveFormatException>(() => RowDecoder.Decode(Columns, line));

        Assert.Contains("'Balance' (decimal)", error.Message, StringComparison.Ordinal);
    }
}
