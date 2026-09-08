using SqlArchive.Core.Export;
using SqlArchive.Core.Format;
using SqlSchemaDiff.Models;

namespace SqlArchive.Core.Tests;

/// <summary>
/// The properties the ranges have to have, asserted without a server.
/// <para>
/// A range that leaves rows out produces an archive that opens, that verifies against
/// itself and that is missing data - the row count beside the hash is computed from the
/// same ranges, so it agrees with itself. The live tests answer "did these rows come out
/// of that table"; these answer the question underneath it, which is whether the ranges
/// can cover anything less than the whole column however badly the boundaries are chosen.
/// </para>
/// </summary>
public sealed class ExportRangeTests
{
    /// <summary>
    /// The one property everything rests on: consecutive ranges share a boundary, and the
    /// two ends are open. Together they say that every value of the column is in exactly
    /// one range - not "if min and max were right", but at all.
    /// </summary>
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(16)]
    public void TheRangesCoverEveryValueAndOverlapNowhere(int count)
    {
        var ranges = RangePlanner.Divide(1L, 1_000_000L, SqlValueKind.Integer, count);

        Assert.Equal(count, ranges.Count);
        Assert.Null(ranges[0].Lower);
        Assert.Null(ranges[^1].Upper);

        for(var i = 1; i < ranges.Count; i++)
        {
            // The same value, not a value computed to be next to it. There is no
            // successor function here to get wrong, so there is no gap to leave.
            Assert.Equal(ranges[i - 1].Upper, ranges[i].Lower);
            Assert.NotNull(ranges[i].Lower);
        }
    }

    /// <summary>
    /// Boundaries taken from an extent that is nothing like the data still cover the data.
    /// This is the guarantee spelled out: a wrongly computed boundary makes the files
    /// lopsided and cannot make a row homeless.
    /// </summary>
    [Fact]
    public void RowsOutsideTheExtentTheBoundariesCameFromStillLandInARange()
    {
        // As if MIN and MAX had said 100..200 while the table really holds -5_000_000_000
        // to 5_000_000_000 - a row inserted after the extent was read, or a filter that
        // hid the outliers from it.
        var ranges = RangePlanner.Divide(100L, 200L, SqlValueKind.Integer, 4);

        foreach(var row in new[] { long.MinValue, -5_000_000_000L, 0L, 150L, 5_000_000_000L, long.MaxValue })
            Assert.Equal(1, ranges.Count(r => Contains(r, row)));
    }

    /// <summary>The widest interval a bigint key can have, which is where an interpolation overflows if it is going to.</summary>
    [Fact]
    public void TheWholeRangeOfABigIntDoesNotOverflowTheArithmetic()
    {
        var ranges = RangePlanner.Divide(long.MinValue, long.MaxValue, SqlValueKind.Integer, 8);

        Assert.Equal(8, ranges.Count);

        foreach(var row in new[] { long.MinValue, -1L, 0L, 1L, long.MaxValue })
            Assert.Equal(1, ranges.Count(r => Contains(r, row)));

        // From two, because the first range has no lower bound at all.
        for(var i = 2; i < ranges.Count; i++)
            Assert.True((long)ranges[i - 1].Lower! < (long)ranges[i].Lower!, "the cuts have to increase");
    }

    /// <summary>
    /// A date key divides the same way, and the boundaries keep the seven fractional
    /// digits a datetime2 can hold - a boundary rounded to the second would be a
    /// different boundary from the one the plan wrote down.
    /// </summary>
    [Fact]
    public void ADateKeyDividesAndKeepsItsPrecision()
    {
        var from = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var to = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified).AddTicks(7);

        var ranges = RangePlanner.Divide(from, to, SqlValueKind.DateTime, 5);

        Assert.Equal(5, ranges.Count);

        foreach(var row in new[] { DateTime.MinValue, from, new DateTime(2023, 6, 1), to, DateTime.MaxValue })
            Assert.Equal(1, ranges.Count(r => Contains(r, row)));

        // Written down and read back is the same instant, ticks and all.
        foreach(var range in ranges.Where(r => r.Lower is not null))
            Assert.Equal(range.Lower, Bounds.Parse(Bounds.Format(range.Lower), PartitionKindName.DateTime));
    }

    /// <summary>
    /// A key narrower than the number of ranges asked for gives back what it can rather
    /// than a row of empty files, and one range when there is nothing to cut at all.
    /// </summary>
    [Fact]
    public void AKeyWithNothingToCutComesBackAsOneRange()
    {
        Assert.True(RangePlanner.Divide(42L, 42L, SqlValueKind.Integer, 8).Single().IsWholeTable);

        // Eight ranges over two adjacent integers: every cut rounds down onto the
        // smallest value, where a boundary would only make an empty first file, so the
        // answer is one range rather than seven empty ones and a full one.
        Assert.True(RangePlanner.Divide(42L, 43L, SqlValueKind.Integer, 8).Single().IsWholeTable);

        // Three integers apart, there is somewhere to cut.
        var several = RangePlanner.Divide(0L, 3L, SqlValueKind.Integer, 3);

        Assert.Equal(3, several.Count);

        foreach(var row in new[] { 0L, 1L, 2L, 3L })
            Assert.Equal(1, several.Count(r => Contains(r, row)));
    }

    /// <summary>The predicate says what the range means, including that the ends are open.</summary>
    [Fact]
    public void ThePredicateIsHalfOpenAndOpenAtTheEnds()
    {
        var ranges = RangePlanner.Divide(0L, 100L, SqlValueKind.Integer, 3);

        Assert.Equal(" WHERE [Id] < @sqlarchive_upper", ranges[0].Predicate("[Id]", null));
        Assert.Equal(
            " WHERE [Id] >= @sqlarchive_lower AND [Id] < @sqlarchive_upper",
            ranges[1].Predicate("[Id]", null));
        Assert.Equal(" WHERE [Id] >= @sqlarchive_lower", ranges[^1].Predicate("[Id]", null));

        // The filter is parenthesised, so an OR inside it cannot reach past the bounds
        // and drag in rows from the neighbouring ranges.
        Assert.Equal(
            " WHERE (A = 1 OR B = 2) AND [Id] >= @sqlarchive_lower AND [Id] < @sqlarchive_upper",
            ranges[1].Predicate("[Id]", "A = 1 OR B = 2"));

        Assert.Equal(string.Empty, TableRange.Whole.Predicate("[Id]", null));
        Assert.Equal(" WHERE (Deleted = 0)", TableRange.Whole.Predicate("[Id]", "Deleted = 0"));
    }

    /// <summary>The clustered key wins, because that is the read that is a seek rather than a scan.</summary>
    [Fact]
    public void TheClusteredKeyIsPreferredOverANonClusteredIndex()
    {
        var table = Table(
            [Column("Id", "int"), Column("Created", "datetime2")],
            keys: [Key("PK_T", "CLUSTERED", "Id")],
            indexes: [Index("IX_Created", "NONCLUSTERED", "Created")]);

        var chosen = RangePlanner.ChooseColumn(table, ArchiveColumns.For(table));

        Assert.NotNull(chosen);
        Assert.Equal("Id", chosen.Column.Name);
        Assert.Contains("PK_T", chosen.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// A nullable column is never chosen, whatever index it leads.
    /// <para>
    /// Every comparison against NULL is unknown, so <c>key &gt;= a</c> and <c>key &lt; a</c>
    /// are both false for a null key and the row is in no range at all. It goes missing
    /// from every range at once, which is exactly why the total still looks like a
    /// plausible number.
    /// </para>
    /// </summary>
    [Fact]
    public void ANullableColumnIsNeverTheOneToSplitBy()
    {
        var table = Table(
            [Column("Id", "int", nullable: true), Column("Seq", "bigint")],
            keys: [],
            indexes: [Index("IX_Id", "CLUSTERED", "Id"), Index("IX_Seq", "NONCLUSTERED", "Seq")]);

        var chosen = RangePlanner.ChooseColumn(table, ArchiveColumns.For(table));

        Assert.NotNull(chosen);
        Assert.Equal("Seq", chosen.Column.Name);
    }

    /// <summary>
    /// A string or a <c>uniqueidentifier</c> key means the table is read in one pass. Their
    /// order belongs to a collation or to the engine, and a boundary placed by our
    /// ordering and compared by theirs puts rows on the wrong side of it.
    /// </summary>
    [Theory]
    [InlineData("nvarchar")]
    [InlineData("uniqueidentifier")]
    [InlineData("float")]
    [InlineData("varbinary")]
    public void AKeyWhoseOrderIsNotOursIsNotSplitBy(string typeName)
    {
        var table = Table([Column("K", typeName)], keys: [Key("PK_T", "CLUSTERED", "K")], indexes: []);

        Assert.Null(RangePlanner.ChooseColumn(table, ArchiveColumns.For(table)));
    }

    /// <summary>An unindexed column is not chosen: a range read on one is a full scan, so four ranges are four scans.</summary>
    [Fact]
    public void AnUnindexedColumnIsNotChosenEvenWhenItsTypeWouldDo()
    {
        var table = Table([Column("Id", "bigint")], keys: [], indexes: []);

        Assert.Null(RangePlanner.ChooseColumn(table, ArchiveColumns.For(table)));
    }

    /// <summary>An included column is carried in the leaf and has no order to seek on, so it is not a key.</summary>
    [Fact]
    public void AnIncludedColumnIsNotALeadingColumn()
    {
        var table = Table(
            [Column("Name", "nvarchar"), Column("Amount", "int")],
            keys: [],
            indexes: [new IndexModel
            {
                Name = "IX_Name",
                TypeDesc = "NONCLUSTERED",
                Columns =
                [
                    new IndexColumnModel { Name = "Name", KeyOrdinal = 1 },
                    new IndexColumnModel { Name = "Amount", KeyOrdinal = 0, IsIncluded = true }
                ]
            }]);

        Assert.Null(RangePlanner.ChooseColumn(table, ArchiveColumns.For(table)));
    }

    /// <summary>
    /// A computed column is not in the archive, so it cannot be what the archive is cut
    /// by either - the range predicate would be about a column the entries do not carry.
    /// </summary>
    [Fact]
    public void AColumnTheArchiveDoesNotCarryIsNotAKeyEither()
    {
        var table = Table(
            [Column("Id", "int"), Computed("Doubled", "int")],
            keys: [],
            indexes: [Index("IX_Doubled", "NONCLUSTERED", "Doubled"), Index("IX_Id", "NONCLUSTERED", "Id")]);

        var chosen = RangePlanner.ChooseColumn(table, ArchiveColumns.For(table));

        Assert.NotNull(chosen);
        Assert.Equal("Id", chosen.Column.Name);
    }

    /// <summary>
    /// The same table plans the same way twice. Resume reads the plan back from the spool
    /// rather than recomputing it, and this is the property that makes the two runs about
    /// the same table.
    /// </summary>
    [Fact]
    public void TheChoiceIsTheSameEveryTime()
    {
        var table = Table(
            [Column("A", "int"), Column("B", "int"), Column("C", "datetime2")],
            keys: [],
            indexes: [Index("IX_C", "NONCLUSTERED", "C"), Index("IX_B", "NONCLUSTERED", "B"), Index("IX_A", "NONCLUSTERED", "A")]);

        var first = RangePlanner.ChooseColumn(table, ArchiveColumns.For(table));
        var again = RangePlanner.ChooseColumn(table, ArchiveColumns.For(table));

        Assert.NotNull(first);
        Assert.Equal(first.Column.Name, again!.Column.Name);

        // An integer before a date, because an evenly spaced cut is likeliest to give
        // evenly sized files on one; and A before B because that is the column order.
        Assert.Equal("A", first.Column.Name);
    }

    private static bool Contains(TableRange range, long value) =>
        (range.Lower is null || value >= (long)range.Lower) &&
        (range.Upper is null || value < (long)range.Upper);

    private static bool Contains(TableRange range, DateTime value) =>
        (range.Lower is null || value >= (DateTime)range.Lower) &&
        (range.Upper is null || value < (DateTime)range.Upper);

    private static TableModel Table(
        List<ColumnModel> columns,
        List<KeyConstraintModel> keys,
        List<IndexModel> indexes) =>
        new() { Schema = "dbo", Name = "T", Columns = columns, KeyConstraints = keys, Indexes = indexes };

    private static ColumnModel Column(string name, string typeName, bool nullable = false) =>
        new() { Name = name, TypeName = typeName, IsNullable = nullable };

    private static ColumnModel Computed(string name, string typeName) =>
        new() { Name = name, TypeName = typeName, IsComputed = true, ComputedDefinition = "([Id]*2)" };

    private static KeyConstraintModel Key(string name, string indexTypeDesc, string column) =>
        new()
        {
            Name = name,
            TypeCode = "PK",
            IndexTypeDesc = indexTypeDesc,
            Columns = [new IndexColumnModel { Name = column, KeyOrdinal = 1 }]
        };

    private static IndexModel Index(string name, string typeDesc, string column) =>
        new()
        {
            Name = name,
            TypeDesc = typeDesc,
            Columns = [new IndexColumnModel { Name = column, KeyOrdinal = 1 }]
        };
}
