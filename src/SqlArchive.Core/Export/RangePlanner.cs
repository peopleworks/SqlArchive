using System.Globalization;
using Microsoft.Data.SqlClient;
using SqlArchive.Core.Format;
using SqlSchemaDiff.Models;
using SqlSchemaDiff.Services;

namespace SqlArchive.Core.Export;

/// <summary>
/// Decides whether a table is read in one pass or in several ranges, which column the
/// ranges are cut on, and where the cuts go.
/// <para>
/// This is the part of an export that can go wrong without looking wrong. A range that
/// leaves rows out produces an archive that opens, verifies against itself, restores, and
/// is missing data; the only thing that would give it away is the row count, and a row
/// count that was computed from the same ranges agrees with itself. So the coverage here
/// is arranged not to depend on the boundaries being right - see
/// <see cref="TableRange"/> - and the boundaries are only ever asked to make the files a
/// sensible size.
/// </para>
/// </summary>
public static class RangePlanner
{
    /// <summary>
    /// The column to split by, or null when there is none and the table is read in one
    /// pass.
    /// </summary>
    /// <remarks>
    /// Three requirements, and each one rules out a way of losing rows or of being slower
    /// than not splitting at all.
    /// <list type="number">
    /// <item>
    /// <b>It is the leading column of an index.</b> A range read on an unindexed column
    /// is a full scan per range, so splitting a table four ways would read it four times
    /// - slower than the one pass it replaced. It also makes the MIN and MAX this planner
    /// asks for two seeks instead of two scans.
    /// </item>
    /// <item>
    /// <b>It is <c>NOT NULL</c>.</b> Every comparison against NULL is unknown, so a
    /// nullable partition column silently drops every row whose key is null - from every
    /// range at once, which is why the total still looks plausible. Refusing to split by
    /// a nullable column removes the whole class rather than adding an <c>IS NULL</c>
    /// range that somebody has to remember to keep.
    /// </item>
    /// <item>
    /// <b>Its type has an ordering that is ours to compute.</b> Integers, decimals and
    /// the date and time types qualify. Strings and <c>uniqueidentifier</c> do not: their
    /// order belongs to a collation or to the engine, and a boundary placed by our
    /// ordering and compared by theirs puts rows on the wrong side of it. See
    /// <see cref="PartitionKinds.Of"/>.
    /// </item>
    /// </list>
    /// <para>
    /// Among the columns that qualify, the ordering is: the best-indexed first, because
    /// that decides whether the read is a seek; then integers before dates before
    /// decimals, because an evenly spaced cut is likeliest to mean evenly sized files on
    /// an integer key; then the table's own column order, so that the same database plans
    /// the same way twice - which resume depends on.
    /// </para>
    /// <para>
    /// <b>There is deliberately no fallback.</b> A table with no such column is read in
    /// one pass. The tempting alternative - numbering the rows with
    /// <c>ROW_NUMBER() OVER (ORDER BY (SELECT NULL))</c> and slicing that - is the trap
    /// this whole file is about: the numbering is not stable between connections, so two
    /// ranges computed from two of them overlap and leave a hole, and the archive looks
    /// complete.
    /// </para>
    /// </remarks>
    public static PartitionColumn? ChooseColumn(TableModel table, IReadOnlyList<ArchiveColumn> archived)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(archived);

        var byName = new Dictionary<string, ArchiveColumn>(StringComparer.OrdinalIgnoreCase);
        foreach(var column in archived)
            byName[column.Name] = column;

        var order = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for(var i = 0; i < table.Columns.Count; i++)
            order[table.Columns[i].Name] = i;

        var nullable = new HashSet<string>(
            table.Columns.Where(c => c.IsNullable).Select(c => c.Name),
            StringComparer.OrdinalIgnoreCase);

        var candidates = new List<PartitionColumn>();

        void Consider(string columnName, int tier, string reason)
        {
            if(nullable.Contains(columnName) || !byName.TryGetValue(columnName, out var column))
                return;

            if(PartitionKinds.Of(column.Kind) is null)
                return;

            candidates.Add(new PartitionColumn(column, tier, reason));
        }

        foreach(var key in table.KeyConstraints)
        {
            var leading = Leading(key.Columns);
            if(leading is not null)
                Consider(leading, SqlRender.IsClustered(key.IndexTypeDesc) ? 0 : 2, $"the leading column of {key.TypeCode} [{key.Name}]");
        }

        foreach(var index in table.Indexes)
        {
            // A columnstore index has no key order to seek on, so it is no help here even
            // though it is an index.
            if(SqlRender.IsColumnstore(index.TypeDesc))
                continue;

            var leading = Leading(index.Columns);
            if(leading is null)
                continue;

            var tier = SqlRender.IsClustered(index.TypeDesc) ? 1 : index.IsUnique ? 2 : 3;
            Consider(leading, tier, $"the leading column of index [{index.Name}]");
        }

        return candidates
            .OrderBy(c => c.Tier)
            .ThenBy(c => Rank(c.Column.Kind))
            .ThenBy(c => order.GetValueOrDefault(c.Column.Name, int.MaxValue))
            .ThenBy(c => c.Column.Name, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    /// <summary>
    /// The ranges for one table: one when it is small, has no partition column or has
    /// been told not to split, and several when it is worth it.
    /// </summary>
    /// <param name="scope">A connection to ask the server about the column's two ends.</param>
    /// <param name="table">The table.</param>
    /// <param name="key">What <see cref="ChooseColumn"/> picked, or null.</param>
    /// <param name="estimatedRows">
    /// The catalog's row estimate. It decides how many files there are and never which
    /// rows go in them, so it is allowed to be stale.
    /// </param>
    /// <param name="options">The export's settings.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public static async Task<IReadOnlyList<TableRange>> PlanAsync(
        ExportReadScope scope,
        TableModel table,
        PartitionColumn? key,
        long estimatedRows,
        ExportOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(options);

        if(key is null || options.Ranges == ExportRanges.Off)
            return [TableRange.Whole];

        if(options.Ranges == ExportRanges.Auto && estimatedRows < options.SplitThreshold)
            return [TableRange.Whole];

        var wanted = Wanted(estimatedRows, options);
        if(wanted < 2)
            return [TableRange.Whole];

        var kind = PartitionKinds.Of(key.Column.Kind)!.Value;
        var (min, max) = await ExtentAsync(scope, table, key, kind, cancellationToken).ConfigureAwait(false);

        // An empty table, a table whose key is all one value, or a decimal too wide to
        // interpolate. One range, and no guessing.
        if(min is null || max is null)
            return [TableRange.Whole];

        return Divide(min, max, key.Column.Kind, wanted);
    }

    /// <summary>
    /// Cuts the interval between two values into contiguous half-open ranges, open at
    /// both ends of the table.
    /// </summary>
    /// <param name="min">The smallest value the column holds.</param>
    /// <param name="max">The largest.</param>
    /// <param name="kind">The column's kind, which decides the arithmetic.</param>
    /// <param name="count">How many ranges are wanted. Fewer come back when the cuts collide.</param>
    /// <remarks>
    /// Separate from <see cref="PlanAsync"/>, and public, because the properties that
    /// matter here are the ones a test should be able to assert without a server: that
    /// consecutive ranges share a boundary, that the first has no floor and the last no
    /// ceiling, and therefore that every value of the column falls in exactly one of them
    /// whatever <paramref name="min"/> and <paramref name="max"/> were.
    /// </remarks>
    public static IReadOnlyList<TableRange> Divide(object min, object max, SqlValueKind kind, int count)
    {
        ArgumentNullException.ThrowIfNull(min);
        ArgumentNullException.ThrowIfNull(max);

        var partition = PartitionKinds.Of(kind)
            ?? throw new ExportException($"A table cannot be split by a {kind} column.");

        var boundaries = count < 2 ? [] : Boundaries(min, max, partition, count);

        if(boundaries.Count == 0)
            return [TableRange.Whole];

        var ranges = new List<TableRange>(boundaries.Count + 1);

        for(var i = 0; i <= boundaries.Count; i++)
        {
            ranges.Add(new TableRange
            {
                Index = i,

                // The first range has no floor and the last has no ceiling, so the union
                // of the predicates is a tautology over the column's whole domain: a row
                // outside [min, max] - inserted after this ran, or excluded from the
                // MIN/MAX by a filter - still belongs to exactly one range.
                Lower = i == 0 ? null : boundaries[i - 1],

                // And this is the same object as the next range's Lower, so the two
                // cannot disagree about where the boundary is.
                Upper = i == boundaries.Count ? null : boundaries[i]
            });
        }

        return ranges;
    }

    /// <summary>How many ranges the row estimate asks for, within the caller's ceiling.</summary>
    private static int Wanted(long estimatedRows, ExportOptions options)
    {
        var perRange = Math.Max(1, options.RowsPerRange);
        var ceiling = Math.Max(1, options.MaxRangesPerTable);
        var count = (int)Math.Clamp((estimatedRows + perRange - 1) / perRange, 1, ceiling);

        // Always means "split whatever the size", which is how a test gets several ranges
        // over a table small enough for it to check every row of.
        return options.Ranges == ExportRanges.Always ? Math.Max(count, Math.Min(2, ceiling)) : count;
    }

    /// <summary>
    /// The two ends of the partition column.
    /// </summary>
    /// <remarks>
    /// Taken without the row filter, on purpose. Restricting the extent to the filtered
    /// rows would make the boundaries tighter and would also make this a scan on a
    /// column the filter is not indexed by; and it buys nothing, because the outer
    /// ranges are unbounded and cover whatever falls outside the extent anyway.
    /// </remarks>
    private static async Task<(object? Min, object? Max)> ExtentAsync(
        ExportReadScope scope,
        TableModel table,
        PartitionColumn key,
        PartitionKind kind,
        CancellationToken cancellationToken)
    {
        var column = PartitionKinds.Bound(kind, SqlRender.Quote(key.Column.Name));

        await using var command = scope.Command(
            $"SELECT MIN({column}), MAX({column}) FROM {SqlRender.Quote(table.Schema, table.Name)};");

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if(!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return (null, null);

        var min = PartitionKinds.Read(reader, 0, kind);
        var max = PartitionKinds.Read(reader, 1, kind);

        return Equals(min, max) ? (null, null) : (min, max);
    }

    /// <summary>
    /// The interior boundaries, evenly spaced between the two ends of the column.
    /// <para>
    /// Even spacing is a guess about where the rows are, and on a key with a long-empty
    /// stretch in the middle it is a poor one - some files come out large and some empty.
    /// That is the whole of what it can cost. It is not, and cannot become, a row in no
    /// file at all, because the ranges built from these are contiguous and open at both
    /// ends of the table.
    /// </para>
    /// </summary>
    private static List<object> Boundaries(object min, object max, PartitionKind kind, int count) => kind switch
    {
        PartitionKind.Integer => Interpolate(min, (long)min, (long)max, count, v => (object)decimal.ToInt64(v)),
        PartitionKind.Decimal => Interpolate(min, (decimal)min, (decimal)max, count, v => (object)v),
        PartitionKind.DateTime => Interpolate(
            min, ((DateTime)min).Ticks, ((DateTime)max).Ticks, count,
            v => (object)new DateTime(decimal.ToInt64(v), ((DateTime)min).Kind)),
        _ => Interpolate(
            min, ((DateTimeOffset)min).UtcTicks, ((DateTimeOffset)max).UtcTicks, count,
            // Comparison of a datetimeoffset is by the instant it names, so a boundary
            // expressed in UTC divides the column the same way whatever offsets the rows
            // happen to carry.
            v => (object)new DateTimeOffset(new DateTime(decimal.ToInt64(v), DateTimeKind.Utc)))
    };

    private static List<object> Interpolate(
        object smallest,
        decimal min,
        decimal max,
        int count,
        Func<decimal, object> build)
    {
        var boundaries = new List<object>(count - 1);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var span = max - min;

        for(var i = 1; i < count; i++)
        {
            var value = build(min + (span * i / count));

            // A cut at the smallest value the column holds gives a leading range with
            // nothing in it, which happens when the key is narrower than the number of
            // ranges asked for and the arithmetic rounds down to it.
            if(value.Equals(smallest))
                continue;

            // Two cuts that land on the same value would give an empty range between
            // them. Harmless but untidy, and dropping one only merges two neighbours,
            // which keeps the ranges contiguous.
            if(seen.Add(Key(value)))
                boundaries.Add(value);
        }

        return boundaries;
    }

    private static string Key(object value) => value switch
    {
        DateTime moment => moment.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset moment => moment.ToString("O", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty
    };

    /// <summary>An integer key cuts most evenly, a date next, a decimal last. Only used to break a tie.</summary>
    private static int Rank(SqlValueKind kind) => kind switch
    {
        SqlValueKind.Integer => 0,
        SqlValueKind.Date or SqlValueKind.DateTime or SqlValueKind.DateTimeOffset => 1,
        _ => 2
    };

    /// <summary>The first key column of an index, ignoring the ones that are only carried along in the leaf.</summary>
    private static string? Leading(IEnumerable<IndexColumnModel> columns) =>
        columns
            .Where(c => !c.IsIncluded && c.KeyOrdinal > 0)
            .OrderBy(c => c.KeyOrdinal)
            .FirstOrDefault()?.Name;
}
