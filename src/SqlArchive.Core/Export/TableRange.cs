using System.Data;
using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.Data.SqlClient;
using SqlArchive.Core.Format;

namespace SqlArchive.Core.Export;

/// <summary>
/// One slice of a table, as a half-open interval on the partition column:
/// <c>lower &lt;= key &lt; upper</c>, with a missing bound meaning unbounded.
/// <para>
/// <b>The ranges of a table are contiguous by construction rather than by arithmetic.</b>
/// Range <i>i</i>'s <see cref="Upper"/> is the very same value as range <i>i+1</i>'s
/// <see cref="Lower"/> - not its successor, not its successor computed for this
/// particular type - so there is nowhere for a value to fall between two ranges. The
/// first range has no lower bound and the last has no upper bound, so the union of the
/// predicates is true for every value the column can hold. Both of those together are
/// what make a badly chosen boundary a performance problem instead of a correctness one:
/// the worst a wrong boundary can do is make one file large and another empty.
/// </para>
/// <para>
/// The bounds are bound as parameters typed from the column, never formatted into the
/// SQL. That is not only about injection. A boundary written as text is rounded by
/// whatever formats it, and a boundary rounded differently on the two sides of it either
/// loses rows or duplicates them; one parameter value used by both sides cannot be
/// rounded two ways.
/// </para>
/// </summary>
public sealed class TableRange
{
    /// <summary>The zero-based position of this range in its table, which is also its entry's number.</summary>
    public int Index { get; init; }

    /// <summary>Inclusive lower bound, or null for unbounded. Always null on the first range.</summary>
    public object? Lower { get; init; }

    /// <summary>Exclusive upper bound, or null for unbounded. Always null on the last range.</summary>
    public object? Upper { get; init; }

    /// <summary>True for the single range of a table that was not split.</summary>
    [JsonIgnore]
    public bool IsWholeTable => Lower is null && Upper is null;

    /// <summary>The one range of a table read in a single pass.</summary>
    public static TableRange Whole { get; } = new() { Index = 0 };

    /// <summary>
    /// The <c>WHERE</c> this range needs, or an empty string. The row filter, when there
    /// is one, is parenthesised so that an <c>OR</c> inside it cannot swallow the bounds.
    /// </summary>
    public string Predicate(string quotedColumn, string? rowFilter)
    {
        var terms = new List<string>(3);

        if(!string.IsNullOrWhiteSpace(rowFilter))
            terms.Add($"({rowFilter})");

        if(Lower is not null)
            terms.Add($"{quotedColumn} >= @sqlarchive_lower");

        if(Upper is not null)
            terms.Add($"{quotedColumn} < @sqlarchive_upper");

        return terms.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", terms);
    }

    /// <summary>Binds whichever bounds this range has, with the column's own type.</summary>
    public void Bind(SqlCommand command, ArchiveColumn? column)
    {
        ArgumentNullException.ThrowIfNull(command);

        if(Lower is not null)
            command.Parameters.Add(Parameter("@sqlarchive_lower", Lower, column));

        if(Upper is not null)
            command.Parameters.Add(Parameter("@sqlarchive_upper", Upper, column));
    }

    /// <summary>For a message and for the plan file: <c>[1000, 2000)</c>.</summary>
    public override string ToString() =>
        $"[{Describe(Lower)}, {Describe(Upper)})";

    /// <summary>
    /// The bound as a parameter of the column's own type.
    /// </summary>
    /// <remarks>
    /// An integer bound is computed as a <see cref="long"/> whatever the column's width
    /// and is narrowed back here, so that a <c>tinyint</c> column is compared against a
    /// <c>tinyint</c>. Not tidiness: comparing a narrow column against a wider parameter
    /// makes the optimiser reason about a conversion before it can seek, and the whole
    /// reason for choosing an indexed column was to get the seek. The narrowing cannot
    /// overflow, because every bound lies between the column's own MIN and MAX.
    /// </remarks>
    private static SqlParameter Parameter(string name, object value, ArchiveColumn? column)
    {
        if(column is null)
            return new SqlParameter(name, value);

        var type = RowDecoder.ParameterType(column);

        var parameter = new SqlParameter(name, type)
        {
            Value = type switch
            {
                SqlDbType.TinyInt => (byte)(long)value,
                SqlDbType.SmallInt => (short)(long)value,
                SqlDbType.Int => (int)(long)value,
                _ => value
            }
        };

        if(column.Kind == SqlValueKind.Decimal)
        {
            // Without these the driver infers precision and scale from the value, and two
            // boundaries inferred separately would not be comparable to each other.
            parameter.Precision = column.Precision;
            parameter.Scale = column.Scale;
        }

        return parameter;
    }

    private static string Describe(object? bound) => bound switch
    {
        null => "-",
        DateTime moment => moment.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset moment => moment.ToString("O", CultureInfo.InvariantCulture),
        IFormattable value => value.ToString(null, CultureInfo.InvariantCulture),
        _ => bound.ToString() ?? "-"
    };
}

/// <summary>
/// The column a table is split by, and why it was picked.
/// </summary>
/// <param name="Column">The column, as the codec sees it.</param>
/// <param name="Tier">
/// How well indexed it is: 0 a clustered key, 1 a clustered index, 2 a unique
/// nonclustered index, 3 any nonclustered index. Only used to order the candidates.
/// </param>
/// <param name="Reason">A sentence for the log, saying which index made this the choice.</param>
public sealed record PartitionColumn(ArchiveColumn Column, int Tier, string Reason);

/// <summary>The types a table may be split by, and the shape of their arithmetic.</summary>
internal enum PartitionKind
{
    Integer,
    Decimal,
    DateTime,
    DateTimeOffset
}

/// <summary>Handy conversions the planner needs and nobody else does.</summary>
internal static class PartitionKinds
{
    /// <summary>
    /// Which of the four shapes a column has, or null for a column a table must not be
    /// split by.
    /// </summary>
    /// <remarks>
    /// The exclusions are the interesting part.
    /// <para>
    /// <b>Character columns and <c>uniqueidentifier</c> are out</b> because their ordering
    /// is not ours. A string's order comes from the collation, and a boundary compared
    /// under one collation against an index built under another puts rows on the wrong
    /// side of it - which, unlike a badly placed boundary, does lose rows. A
    /// <c>uniqueidentifier</c> is worse: SQL Server's ordering of it is neither
    /// lexicographic nor byte order.
    /// </para>
    /// <para>
    /// <b><c>float</c> and <c>real</c> are out</b> because a boundary that is not
    /// representable is a boundary that moves, and <b><c>time</c> is out</b> because a
    /// time of day wraps and is nobody's key.
    /// </para>
    /// </remarks>
    public static PartitionKind? Of(SqlValueKind kind) => kind switch
    {
        SqlValueKind.Integer => PartitionKind.Integer,
        SqlValueKind.Decimal => PartitionKind.Decimal,
        SqlValueKind.Date or SqlValueKind.DateTime => PartitionKind.DateTime,
        SqlValueKind.DateTimeOffset => PartitionKind.DateTimeOffset,
        _ => null
    };

    /// <summary>
    /// How to ask the server for the two ends of the column.
    /// </summary>
    /// <remarks>
    /// Integers are widened to <c>bigint</c> in the query rather than by the reader,
    /// because <c>MIN</c> of a <c>smallint</c> comes back a <c>smallint</c> and a typed
    /// accessor for the wrong width throws. The arithmetic is done in one width for all
    /// four so there is one interpolation to get right instead of four.
    /// </remarks>
    public static string Bound(PartitionKind kind, string quotedColumn) => kind == PartitionKind.Integer
        ? $"CONVERT(bigint, {quotedColumn})"
        : quotedColumn;

    /// <summary>
    /// Reads a bound out of the MIN/MAX row as the type its arithmetic is done in, or
    /// null when the value is one this build will not split by - a <c>decimal</c> too
    /// wide for .NET's, which is a column the planner leaves alone rather than a column
    /// it guesses at.
    /// </summary>
    public static object? Read(SqlDataReader reader, int ordinal, PartitionKind kind)
    {
        ArgumentNullException.ThrowIfNull(reader);

        if(reader.IsDBNull(ordinal))
            return null;

        switch(kind)
        {
            case PartitionKind.Integer:
                return reader.GetInt64(ordinal);

            case PartitionKind.Decimal:
                var wide = reader.GetFieldValue<System.Data.SqlTypes.SqlDecimal>(ordinal);

                // A decimal(38,10) at its limit does not fit in .NET's 28 digits, and
                // interpolating something we cannot hold is exactly how a boundary ends
                // up somewhere it was not meant to be. Not splitting is always allowed.
                try
                {
                    return wide.Value;
                }
                catch(OverflowException)
                {
                    return null;
                }

            case PartitionKind.DateTime:
                return reader.GetDateTime(ordinal);

            default:
                return reader.GetDateTimeOffset(ordinal);
        }
    }
}
