using System.Globalization;
using System.Text.Json.Serialization;

namespace SqlArchive.Core.Export;

/// <summary>
/// Everything the export decided before it read a row: which tables, which of them keep
/// only their schema, what each one is filtered by, and how each one is cut into ranges.
/// <para>
/// It is written into the spool and <b>read back rather than recomputed</b> when an
/// export resumes, and that is the point of its existing as a file at all. Range
/// boundaries come from the data's current extent, so recomputing them on a second run
/// would give a table that is half exported under one set of cuts and half under
/// another. The two halves would overlap somewhere and leave a hole somewhere else, the
/// manifest would be assembled from both, and the archive would look complete.
/// </para>
/// </summary>
public sealed class ExportPlan
{
    public List<ExportTablePlan> Tables { get; set; } = [];

    /// <summary>Every unit of work, in the order their spool files are numbered.</summary>
    [JsonIgnore]
    public IEnumerable<ExportUnit> Units => Tables.SelectMany(t => t.Units);
}

/// <summary>One table's share of the plan.</summary>
public sealed class ExportTablePlan
{
    public string Schema { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>True when the schema is archived and the rows deliberately are not.</summary>
    public bool DataSkipped { get; set; }

    /// <summary>The <c>--where</c> this table was matched by, or null.</summary>
    public string? RowFilter { get; set; }

    /// <summary>The column the ranges are cut on, or null for a table read in one pass.</summary>
    public string? PartitionColumn { get; set; }

    /// <summary>Which arithmetic the bounds below are in. Null when there are no bounds.</summary>
    public PartitionKindName? PartitionKind { get; set; }

    /// <summary>Why that column, for the log. Null when the table is not split.</summary>
    public string? PartitionReason { get; set; }

    /// <summary>The columns the archive does not carry, with the reason, for the manifest.</summary>
    public Dictionary<string, string> OmittedColumns { get; set; } = new(StringComparer.Ordinal);

    public List<ExportUnit> Units { get; set; } = [];

    /// <summary>The two-part name, for messages.</summary>
    [JsonIgnore]
    public string Identifier => $"[{Schema}].[{Name}]";
}

/// <summary>
/// One range of one table: the entry it will become, and the bounds that decide which
/// rows go in it.
/// <para>
/// The bounds travel as text in the invariant culture rather than as JSON numbers and
/// dates, so that what the plan says and what the parameter carries cannot drift apart
/// through a parser's idea of a number. They are also then readable by whoever is
/// looking at a spool that would not resume.
/// </para>
/// </summary>
public sealed class ExportUnit
{
    /// <summary>Position in the whole export, which is what names the spool file.</summary>
    public int Ordinal { get; set; }

    /// <summary>The zip entry these rows become.</summary>
    public string EntryName { get; set; } = string.Empty;

    /// <summary>Inclusive lower bound, or null for unbounded. Null on a table's first range.</summary>
    public string? Lower { get; set; }

    /// <summary>Exclusive upper bound, or null for unbounded. Null on a table's last range.</summary>
    public string? Upper { get; set; }

    /// <summary>The spool file this unit's rows are written to, under the working directory.</summary>
    [JsonIgnore]
    public string SpoolName => $"u{Ordinal.ToString("0000", CultureInfo.InvariantCulture)}.jsonl";

    /// <summary>What the unit did, once it has: the rows, their hash, and the bytes it wrote.</summary>
    [JsonIgnore]
    public string DoneName => $"u{Ordinal.ToString("0000", CultureInfo.InvariantCulture)}.done.json";

    /// <summary>The bounds as the values they name, for building the query.</summary>
    public TableRange ToRange(PartitionKindName? kind, int index) => new()
    {
        Index = index,
        Lower = Bounds.Parse(Lower, kind),
        Upper = Bounds.Parse(Upper, kind)
    };
}

/// <summary>
/// The name of the arithmetic a table's bounds are in, written into the plan so that
/// reading it back does not have to consult the schema.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<PartitionKindName>))]
public enum PartitionKindName
{
    Integer,
    Decimal,
    DateTime,
    DateTimeOffset
}

/// <summary>Turns a range bound into text and back, in one place so the two agree.</summary>
public static class Bounds
{
    /// <summary>The invariant text of a bound, or null.</summary>
    public static string? Format(object? bound) => bound switch
    {
        null => null,
        long value => value.ToString(CultureInfo.InvariantCulture),
        decimal value => value.ToString(CultureInfo.InvariantCulture),

        // Round-trip, so a datetime2's seven fractional digits survive being written down
        // and read back. A boundary that lost a digit here would be a different boundary.
        DateTime value => value.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset value => value.ToString("O", CultureInfo.InvariantCulture),
        _ => throw new ExportException($"A range bound cannot be a {bound.GetType().Name}.")
    };

    /// <summary>The value a bound's text names, in the arithmetic the table's key uses.</summary>
    public static object? Parse(string? text, PartitionKindName? kind)
    {
        if(text is null)
            return null;

        if(kind is null)
            throw new ExportException($"The plan carries the range bound '{text}' and does not say what type it is.");

        return kind.Value switch
        {
            PartitionKindName.Integer => long.Parse(text, CultureInfo.InvariantCulture),
            PartitionKindName.Decimal => decimal.Parse(text, NumberStyles.Number, CultureInfo.InvariantCulture),
            PartitionKindName.DateTime => DateTime.Parse(
                text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            _ => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
        };
    }

    /// <summary>The plan's name for the planner's kind.</summary>
    internal static PartitionKindName Name(PartitionKind kind) => kind switch
    {
        SqlArchive.Core.Export.PartitionKind.Integer => PartitionKindName.Integer,
        SqlArchive.Core.Export.PartitionKind.Decimal => PartitionKindName.Decimal,
        SqlArchive.Core.Export.PartitionKind.DateTime => PartitionKindName.DateTime,
        _ => PartitionKindName.DateTimeOffset
    };
}

/// <summary>What one unit produced, kept beside its spool file so a resume can skip it.</summary>
public sealed class ExportUnitResult
{
    /// <summary>Rows written into this unit's entry.</summary>
    public long Rows { get; set; }

    /// <summary>The sum of the SHA-256 of those rows' lines, lowercase hex.</summary>
    public string RowHash { get; set; } = string.Empty;

    /// <summary>The spool file's length, so a truncated one is caught before it is packed.</summary>
    public long Bytes { get; set; }

    /// <summary>
    /// SHA-256 of the spool file, prefixed <c>sha256:</c>. Compared against what the
    /// archive writer computes while packing, so a spool file damaged between two runs is
    /// caught rather than sealed into the archive with a manifest that agrees with it.
    /// </summary>
    public string Sha256 { get; set; } = string.Empty;
}
