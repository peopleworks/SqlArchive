using System.Security.Cryptography;
using System.Text;
using SqlArchive.Core.Format;

namespace SqlArchive.Core.Tests;

/// <summary>
/// The properties the table hash has to have, and the one it must not.
/// </summary>
public sealed class RowHashTests
{
    private static byte[] Line(string text) => Encoding.UTF8.GetBytes(text);

    private static string Combine(params string[] lines)
    {
        var accumulator = new RowHash.Accumulator();

        foreach(var line in lines)
            accumulator.AddRow(Line(line));

        return accumulator.Value;
    }

    [Fact]
    public void ARowHashIsTheSha256OfTheLine()
    {
        var line = Line("""{"Id":1}""");

        Assert.Equal(SHA256.HashData(line), RowHash.OfRow(line));
    }

    [Fact]
    public void AnEmptyTableIsSixtyFourZeros() =>
        Assert.Equal(new string('0', 64), new RowHash.Accumulator().Value);

    [Fact]
    public void TheValueIsLowercaseHex()
    {
        var value = Combine("""{"Id":1}""");

        Assert.Equal(64, value.Length);
        Assert.Equal(value.ToLowerInvariant(), value);
    }

    /// <summary>
    /// The reason the hash is what it is. Export reads a large table in parallel ranges
    /// and verify may read it back in another order; if this failed, both sides would
    /// have to sort, which on a large table is the dominant cost.
    /// </summary>
    [Fact]
    public void TheOrderTheRowsArriveInDoesNotChangeTheAnswer()
    {
        var forwards = Combine("""{"Id":1}""", """{"Id":2}""", """{"Id":3}""", """{"Id":4}""");
        var backwards = Combine("""{"Id":4}""", """{"Id":3}""", """{"Id":2}""", """{"Id":1}""");
        var shuffled = Combine("""{"Id":3}""", """{"Id":1}""", """{"Id":4}""", """{"Id":2}""");

        Assert.Equal(forwards, backwards);
        Assert.Equal(forwards, shuffled);
    }

    /// <summary>
    /// And neither do the range boundaries, which is what lets a table be re-read with a
    /// different partitioning and still add up.
    /// </summary>
    [Fact]
    public void NeitherDoTheRangeBoundaries()
    {
        var whole = new RowHash.Accumulator();
        foreach(var id in Enumerable.Range(1, 10))
            whole.AddRow(Line($$"""{"Id":{{id}}}"""));

        var first = new RowHash.Accumulator();
        var second = new RowHash.Accumulator();
        var third = new RowHash.Accumulator();

        foreach(var id in Enumerable.Range(1, 3))
            first.AddRow(Line($$"""{"Id":{{id}}}"""));

        foreach(var id in Enumerable.Range(4, 1))
            second.AddRow(Line($$"""{"Id":{{id}}}"""));

        foreach(var id in Enumerable.Range(5, 6))
            third.AddRow(Line($$"""{"Id":{{id}}}"""));

        // Folded in an order that is not the order they were read in, on purpose.
        var combined = new RowHash.Accumulator();
        combined.Add(third);
        combined.Add(first);
        combined.Add(second);

        Assert.Equal(whole.Value, combined.Value);
        Assert.Equal(whole.Rows, combined.Rows);
        Assert.Equal(10, combined.Rows);
    }

    [Fact]
    public void OneChangedByteChangesTheHash() =>
        Assert.NotEqual(Combine("""{"Id":1,"N":"a"}"""), Combine("""{"Id":1,"N":"b"}"""));

    /// <summary>
    /// The case a row count cannot see, and the reason the hash exists at all: the same
    /// number of rows with different content in them.
    /// </summary>
    [Fact]
    public void AnUpdateIsVisibleWhereARowCountIsNot()
    {
        var before = new RowHash.Accumulator();
        var after = new RowHash.Accumulator();

        foreach(var id in Enumerable.Range(1, 1000))
        {
            before.AddRow(Line($$"""{"Id":{{id}},"Total":"1.00"}"""));
            after.AddRow(Line($$"""{"Id":{{id}},"Total":"2.00"}"""));
        }

        Assert.Equal(before.Rows, after.Rows);
        Assert.NotEqual(before.Value, after.Value);
    }

    /// <summary>
    /// The degenerate case the work package asks about, and the reason the combining
    /// operation is addition rather than XOR. Under XOR both of these hash to zero and
    /// both have four rows, so neither the hash nor the row count beside it tells them
    /// apart - and a restore that copied one range twice and dropped another is exactly
    /// how you get there.
    /// </summary>
    [Fact]
    public void DuplicateRowsDoNotCancel()
    {
        var one = Combine("""{"Id":1}""", """{"Id":1}""", """{"Id":2}""", """{"Id":2}""");
        var other = Combine("""{"Id":3}""", """{"Id":3}""", """{"Id":4}""", """{"Id":4}""");
        var empty = new RowHash.Accumulator().Value;

        Assert.NotEqual(empty, one);
        Assert.NotEqual(empty, other);
        Assert.NotEqual(one, other);

        // The same rows under XOR, to show what is being avoided rather than to assert
        // that anything here uses it.
        Assert.Equal(Xor("""{"Id":1}""", """{"Id":1}""", """{"Id":2}""", """{"Id":2}"""),
                     Xor("""{"Id":3}""", """{"Id":3}""", """{"Id":4}""", """{"Id":4}"""));
    }

    [Fact]
    public void ATableWithEveryRowDuplicatedIsNotAnEmptyTable()
    {
        var doubled = Combine("""{"Id":1}""", """{"Id":1}""");

        Assert.NotEqual(new string('0', 64), doubled);
    }

    /// <summary>
    /// Two rows and the same two rows again have to differ, or a range copied twice would
    /// be invisible whenever the row count also happened to match.
    /// </summary>
    [Fact]
    public void AddingTheSameRowAgainChangesTheValue()
    {
        var once = new RowHash.Accumulator();
        once.AddRow(Line("""{"Id":1}"""));

        var twice = new RowHash.Accumulator();
        twice.AddRow(Line("""{"Id":1}"""));
        twice.AddRow(Line("""{"Id":1}"""));

        Assert.NotEqual(once.Value, twice.Value);
        Assert.Equal(2, twice.Rows);
    }

    /// <summary>
    /// The carry has to propagate the whole way and then fall off the end, which is what
    /// "modulo 2^256" means. Feeding the accumulator a hash of all 0xFF twice is the
    /// arithmetic that would go wrong if it did not.
    /// </summary>
    [Fact]
    public void TheSumWrapsAtTwoToTheTwoFiftySix()
    {
        var accumulator = new RowHash.Accumulator();
        var ones = new byte[RowHash.Size];
        Array.Fill(ones, (byte)0xFF);

        accumulator.Add(ones);
        accumulator.Add(ones);

        // 2 * (2^256 - 1) mod 2^256 = 2^256 - 2.
        Assert.Equal(new string('f', 63) + "e", accumulator.Value);
    }

    [Fact]
    public void ARowHashHasToBeThirtyTwoBytes()
    {
        var accumulator = new RowHash.Accumulator();

        Assert.Throws<ArgumentException>(() => accumulator.Add(new byte[16]));
    }

    private static string Xor(params string[] lines)
    {
        var total = new byte[RowHash.Size];

        foreach(var line in lines)
        {
            var hash = RowHash.OfRow(Line(line));

            for(var i = 0; i < total.Length; i++)
                total[i] ^= hash[i];
        }

        return Convert.ToHexStringLower(total);
    }
}
