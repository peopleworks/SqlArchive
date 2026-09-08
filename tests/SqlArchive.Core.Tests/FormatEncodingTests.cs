using System.Data.SqlTypes;
using System.Text;
using SqlArchive.Core.Format;

namespace SqlArchive.Core.Tests;

/// <summary>
/// One test per row of the encoding table in FORMAT.md, asserted on the exact bytes.
/// <para>
/// Byte-exact and not "parses to the same value", because the bytes are the definition of
/// equality. A change that leaves every value meaning the same thing and writes it
/// differently - one more digit of precision, one fewer escape - changes every hash in
/// every archive already written, and the only place that can be noticed is here.
/// </para>
/// </summary>
public sealed class FormatEncodingTests
{
    private static string Encode(string typeName, object? value, string column = "C")
    {
        var columns = new[] { new ArchiveColumn(column, typeName) };
        var buffer = new CanonicalJsonBuffer();

        RowEncoder.Encode(columns, new object?[] { value }, buffer);

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    [Theory]
    [InlineData("int")]
    [InlineData("nvarchar")]
    [InlineData("varbinary")]
    [InlineData("bit")]
    public void NullIsJsonNullWhateverTheType(string type) =>
        Assert.Equal("""{"C":null}""", Encode(type, null));

    [Fact]
    public void NullIsNotTheEmptyString() =>
        Assert.NotEqual(Encode("nvarchar", null), Encode("nvarchar", string.Empty));

    [Theory]
    [InlineData(true, """{"C":true}""")]
    [InlineData(false, """{"C":false}""")]
    public void BitIsAJsonBoolean(bool value, string expected) =>
        Assert.Equal(expected, Encode("bit", value));

    [Theory]
    [InlineData("tinyint", (byte)255, """{"C":255}""")]
    [InlineData("smallint", short.MinValue, """{"C":-32768}""")]
    [InlineData("int", int.MaxValue, """{"C":2147483647}""")]
    [InlineData("bigint", long.MinValue, """{"C":-9223372036854775808}""")]
    public void IntegersAreJsonNumbers(string type, object value, string expected) =>
        Assert.Equal(expected, Encode(type, value));

    /// <summary>
    /// The largest and smallest a decimal(38,10) can hold. .NET's own decimal cannot
    /// represent either - it carries 28 significant digits and this needs 38 - which is
    /// why the encoder reads through SqlDecimal and why this test exists.
    /// </summary>
    [Fact]
    public void DecimalKeepsAllThirtyEightDigits()
    {
        var value = SqlDecimal.Parse("9999999999999999999999999999.9999999999");

        Assert.Equal("""{"C":"9999999999999999999999999999.9999999999"}""", Encode("decimal", value));
    }

    [Fact]
    public void DecimalIsAStringSoAJsonParserCannotRoundItThroughADouble() =>
        Assert.Contains('"', Encode("decimal", SqlDecimal.Parse("1.5")));

    /// <summary>
    /// The zeros a decimal(19,4) shows are not padding. They are what the type says the
    /// value has, and dropping them would make a column's values encode differently
    /// depending on which of them happened to be whole.
    /// </summary>
    [Fact]
    public void DecimalKeepsTheScaleTheTypeDeclares()
    {
        Assert.Equal("""{"C":"1.0000"}""", Encode("decimal", new SqlDecimal(1.0000m)));
        Assert.Equal("""{"C":"1"}""", Encode("numeric", SqlDecimal.Parse("1")));
    }

    [Fact]
    public void DecimalHasNoExponent() =>
        Assert.DoesNotContain('E', Encode("decimal", SqlDecimal.Parse("0.0000000001")));

    /// <summary>
    /// money always has four decimal places, and all four are written, whatever the
    /// value happens to be.
    /// </summary>
    [Fact]
    public void MoneyKeepsAllFourDecimals()
    {
        Assert.Equal("""{"C":"922337203685477.5807"}""", Encode("money", new SqlMoney(922337203685477.5807m).Value));
        Assert.Equal("""{"C":"1.0000"}""", Encode("money", new SqlMoney(1m).Value));
        Assert.Equal("""{"C":"-3.5000"}""", Encode("money", new SqlMoney(-3.5m).Value));
    }

    /// <summary>
    /// Why the encoder goes through SqlMoney.Value and not SqlMoney.ToString(). That
    /// method writes at least two decimals and trims anything beyond them, so one column
    /// would carry two different scales depending on whether a value happened to be
    /// round - and the canonical form is what equality is defined over.
    /// </summary>
    [Fact]
    public void SqlMoneyToStringWritesAVariableNumberOfDecimals()
    {
        Assert.Equal("1.00", new SqlMoney(1m).ToString());
        Assert.Equal("1.0001", new SqlMoney(1.0001m).ToString());

        Assert.Equal(Encode("money", new SqlMoney(1m).Value).Length,
                     Encode("money", new SqlMoney(1.0001m).Value).Length);
    }

    [Fact]
    public void SmallMoneyKeepsFourDecimalsToo() =>
        Assert.Equal("""{"C":"214748.3647"}""", Encode("smallmoney", new SqlMoney(214748.3647m).Value));

    [Theory]
    [InlineData(0.1d, """{"C":0.1}""")]
    [InlineData(-0d, """{"C":-0}""")]
    [InlineData(1e300, """{"C":1E+300}""")]
    [InlineData(double.MaxValue, """{"C":1.7976931348623157E+308}""")]
    [InlineData(double.Epsilon, """{"C":5E-324}""")]
    public void FloatIsShortestRoundTrippable(double value, string expected) =>
        Assert.Equal(expected, Encode("float", value));

    /// <summary>
    /// The reason real is formatted as a single and not as the double it widens to. Both
    /// round-trip; only one of them is the value that is in the column.
    /// </summary>
    [Fact]
    public void RealIsFormattedAtSinglePrecision()
    {
        Assert.Equal("""{"C":0.1}""", Encode("real", 0.1f));
        Assert.Equal("0.10000000149011612", ((double)0.1f).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void EveryFloatValueSurvivesTheRoundTrip()
    {
        var columns = new[] { new ArchiveColumn("C", "float") };
        var buffer = new CanonicalJsonBuffer();

        foreach(var value in new[] { 0.1, 1d / 3, Math.PI, double.MaxValue, double.MinValue, double.Epsilon, -0.0 })
        {
            RowEncoder.Encode(columns, new object?[] { value }, buffer);
            var back = RowDecoder.Decode(columns, buffer.WrittenSpan);

            Assert.Equal(BitConverter.DoubleToInt64Bits(value), BitConverter.DoubleToInt64Bits((double)back[0]!));
        }
    }

    [Fact]
    public void DateIsTheDateAndNothingElse() =>
        Assert.Equal("""{"C":"2026-01-15"}""", Encode("date", new DateTime(2026, 1, 15)));

    /// <summary>
    /// The fraction is trimmed to the digits the value has, and the point goes with it.
    /// A padded form would produce lines that the server they came from will not read
    /// back: datetime refuses any string with seven fractional digits in it, zeros
    /// included.
    /// </summary>
    [Theory]
    [InlineData(0L, """{"C":"2026-01-15T10:00:00"}""")]
    [InlineData(30000L, """{"C":"2026-01-15T10:00:00.003"}""")]
    [InlineData(1000000L, """{"C":"2026-01-15T10:00:00.1"}""")]
    [InlineData(1234567L, """{"C":"2026-01-15T10:00:00.1234567"}""")]
    public void DateTimeIsIsoWithATheFractionTrimmed(long ticks, string expected) =>
        Assert.Equal(expected, Encode("datetime2", new DateTime(2026, 1, 15, 10, 0, 0).AddTicks(ticks)));

    [Fact]
    public void SmallDateTimeAndDateTimeUseTheSameShape()
    {
        var moment = new DateTime(2026, 1, 15, 10, 1, 0);

        Assert.Equal("""{"C":"2026-01-15T10:01:00"}""", Encode("smalldatetime", moment));
        Assert.Equal("""{"C":"2026-01-15T10:01:00"}""", Encode("datetime", moment));
    }

    /// <summary>
    /// TimeSpan's FFFFFFF specifier does not drop the point the way DateTime's does; it
    /// writes "10:00:00." on a whole second, which is neither ISO 8601 nor something SQL
    /// Server will read. The encoder switches formats rather than relying on it.
    /// </summary>
    [Theory]
    [InlineData(0L, """{"C":"10:00:00"}""")]
    [InlineData(1234567L, """{"C":"10:00:00.1234567"}""")]
    public void TimeNeverEndsInABareDecimalPoint(long ticks, string expected) =>
        Assert.Equal(expected, Encode("time", new TimeSpan(10, 0, 0).Add(TimeSpan.FromTicks(ticks))));

    [Fact]
    public void TimeOutsideADaysRangeIsRefused()
    {
        var error = Assert.Throws<ArchiveEncodingException>(() => Encode("time", TimeSpan.FromDays(2)));

        Assert.Contains("outside the range", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(2, """{"C":"2026-01-15T10:00:00+02:00"}""")]
    [InlineData(0, """{"C":"2026-01-15T10:00:00+00:00"}""")]
    [InlineData(-5, """{"C":"2026-01-15T10:00:00-05:00"}""")]
    public void DateTimeOffsetAlwaysWritesTheOffsetNumerically(int hours, string expected) =>
        Assert.Equal(expected, Encode("datetimeoffset",
            new DateTimeOffset(2026, 1, 15, 10, 0, 0, TimeSpan.FromHours(hours))));

    [Fact]
    public void GuidIsTheDFormInLowerCase() =>
        Assert.Equal("""{"C":"3f2504e0-4f89-11d3-9a0c-0305e82c3301"}""",
            Encode("uniqueidentifier", Guid.Parse("3F2504E0-4F89-11D3-9A0C-0305E82C3301")));

    [Fact]
    public void BinaryIsBase64() =>
        Assert.Equal("""{"C":"3q2+7w=="}""", Encode("varbinary", new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }));

    [Fact]
    public void EmptyBinaryIsAnEmptyString() =>
        Assert.Equal("""{"C":""}""", Encode("varbinary", Array.Empty<byte>()));

    /// <summary>
    /// The three CLR types are base64 of the server's own serialisation, which is what
    /// lets them be archived at all without Microsoft.SqlServer.Types.
    /// </summary>
    [Theory]
    [InlineData("hierarchyid")]
    [InlineData("geography")]
    [InlineData("geometry")]
    public void ClrTypesAreBase64(string type) =>
        Assert.Equal("""{"C":"W0A="}""", Encode(type, new byte[] { 0x5B, 0x40 }));

    /// <summary>
    /// The driver reports a CLR type three-part and database-qualified. If the encoder
    /// took the whole string, the database's name would decide how a value is encoded.
    /// </summary>
    [Fact]
    public void AClrTypeIsRecognisedThroughItsDatabaseQualifiedName() =>
        Assert.Equal(SqlValueKind.UserDefinedBinary, new ArchiveColumn("C", "Ventas.sys.geography").Kind);

    [Theory]
    [InlineData("char")]
    [InlineData("varchar")]
    [InlineData("nchar")]
    [InlineData("nvarchar")]
    [InlineData("text")]
    [InlineData("ntext")]
    [InlineData("xml")]
    public void EveryCharacterTypeIsAJsonString(string type) =>
        Assert.Equal("""{"C":"x"}""", Encode(type, "x"));

    [Fact]
    public void OnlyWhatJsonRequiresIsEscaped()
    {
        const string value = "a\"b\\c\nd\te<f>g&h\u0001i\u007Fj";

        Assert.Equal("""{"C":"a\"b\\c\nd\te<f>g&h\u0001i\u007fj"}""", Encode("nvarchar", value));
    }

    [Fact]
    public void NonAsciiIsWrittenAsRawUtf8()
    {
        var json = Encode("nvarchar", "Böhm 日本語 \U0001F600");

        Assert.Equal("""{"C":"Böhm 日本語 😀"}""", json);
        Assert.DoesNotContain("\\u", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// U+2028 is legal inside a JSON string and only troubles a JavaScript eval, so it
    /// goes through raw. It is here because a library that decided otherwise would change
    /// every hash of every row containing one.
    /// </summary>
    [Fact]
    public void LineAndParagraphSeparatorsAreNotEscaped() =>
        Assert.Equal("{\"C\":\"a\u2028b\u2029c\"}", Encode("nvarchar", "a\u2028b\u2029c"));

    [Fact]
    public void AColumnNameIsEscapedTheSameWayAValueIs() =>
        Assert.Equal("""{"a\"b":1}""", Encode("int", 1, column: "a\"b"));

    /// <summary>
    /// The substitution every JSON library makes here is silent, and it would be ours.
    /// Refusing is the only answer that does not put a value in the archive that was
    /// never in the database.
    /// </summary>
    [Fact]
    public void AnUnpairedSurrogateIsRefusedRatherThanReplaced()
    {
        var error = Assert.Throws<ArchiveEncodingException>(() => Encode("nvarchar", "x\ud800y", column: "Name"));

        Assert.Equal("Name", error.Column);
        Assert.Contains("unpaired UTF-16 surrogate (U+D800)", error.Message, StringComparison.Ordinal);
        Assert.Contains("U+FFFD", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void APairedSurrogateIsFine() =>
        Assert.Equal("""{"C":"😀"}""", Encode("nvarchar", "😀"));

    [Fact]
    public void SqlVariantIsRefusedAndSaysWhy()
    {
        var error = Assert.Throws<ArchiveEncodingException>(() => new ArchiveColumn("V", "sql_variant"));

        Assert.Contains("sql_variant is not supported in format version 1", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The fallback dbdumper takes here is "other types: string representation". That is
    /// what turns a type nobody has heard of into an archive that parses, verifies
    /// against itself, and restores something that was never there.
    /// </summary>
    [Fact]
    public void AnUnknownTypeIsRefusedRatherThanGuessedAt()
    {
        var error = Assert.Throws<ArchiveEncodingException>(() => new ArchiveColumn("V", "vector"));

        Assert.Contains("'vector' is not one this build knows how to encode", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PropertiesAreWrittenInColumnOrderWithNoWhitespace()
    {
        var columns = new[]
        {
            new ArchiveColumn("Id", "int"),
            new ArchiveColumn("Name", "nvarchar"),
            new ArchiveColumn("Balance", "decimal")
        };

        var buffer = new CanonicalJsonBuffer();
        RowEncoder.Encode(columns, new object?[] { 1L, "Acme", SqlDecimal.Parse("1250.0000") }, buffer);

        Assert.Equal("""{"Id":1,"Name":"Acme","Balance":"1250.0000"}""", Encoding.UTF8.GetString(buffer.WrittenSpan));
    }

    /// <summary>
    /// A frozen line covering every branch of the escaper and every numeric format. If a
    /// future runtime changes how a double or a string is written, this is what says so
    /// before an archive does.
    /// </summary>
    [Fact]
    public void TheCanonicalFormIsFrozen()
    {
        var columns = new[]
        {
            new ArchiveColumn("b", "bit"),
            new ArchiveColumn("i", "bigint"),
            new ArchiveColumn("d", "decimal"),
            new ArchiveColumn("m", "money"),
            new ArchiveColumn("f", "float"),
            new ArchiveColumn("r", "real"),
            new ArchiveColumn("t", "datetime2"),
            new ArchiveColumn("g", "uniqueidentifier"),
            new ArchiveColumn("v", "varbinary"),
            new ArchiveColumn("s", "nvarchar"),
            new ArchiveColumn("n", "int")
        };

        var values = new object?[]
        {
            true,
            9007199254740993L,
            SqlDecimal.Parse("-0.0000000001"),
            -3.5000m,
            0.1d,
            0.1f,
            new DateTime(2026, 1, 15, 10, 0, 0).AddTicks(1234567),
            Guid.Parse("3F2504E0-4F89-11D3-9A0C-0305E82C3301"),
            new byte[] { 0xDE, 0xAD, 0xBE, 0xEF },
            "a\"b\\c\td\ne😀ñ",
            null
        };

        var buffer = new CanonicalJsonBuffer();
        RowEncoder.Encode(columns, values, buffer);

        Assert.Equal(
            """{"b":true,"i":9007199254740993,"d":"-0.0000000001","m":"-3.5000","f":0.1,"r":0.1,"t":"2026-01-15T10:00:00.1234567","g":"3f2504e0-4f89-11d3-9a0c-0305e82c3301","v":"3q2+7w==","s":"a\"b\\c\td\ne😀ñ","n":null}""",
            Encoding.UTF8.GetString(buffer.WrittenSpan));
    }

    /// <summary>
    /// Whatever else it is, it has to be JSON. The .NET reader is used as the referee,
    /// because a hand-written writer that produced something almost-JSON would be found
    /// out years later by a tool that is not this one.
    /// </summary>
    [Fact]
    public void EveryLineParsesAsJson()
    {
        var columns = new[] { new ArchiveColumn("s", "nvarchar") };
        var buffer = new CanonicalJsonBuffer();

        foreach(var value in new[]
                {
                    "", "plain", "a\"b", "a\\b", "\u0000\u001F\u007F", "\t\n\r\b\f",
                    "Böhm", "日本語", "😀", "a\u2028b", new string('x', 5000)
                })
        {
            RowEncoder.Encode(columns, new object?[] { value }, buffer);

            var document = System.Text.Json.JsonDocument.Parse(buffer.WrittenMemory);
            Assert.Equal(value, document.RootElement.GetProperty("s").GetString());
        }
    }
}
