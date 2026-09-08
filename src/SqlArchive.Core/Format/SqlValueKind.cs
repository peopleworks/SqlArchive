namespace SqlArchive.Core.Format;

/// <summary>
/// How a column's values are written into a JSONL line. One member per row of the
/// encoding table in <c>FORMAT.md</c>.
/// <para>
/// This is a closed set on purpose. A SQL type this build does not know about is
/// refused rather than written with a best-effort <c>ToString</c>: an unrecognised type
/// encoded by guesswork produces an archive that parses, verifies against itself, and
/// restores something that is not what was there.
/// </para>
/// </summary>
public enum SqlValueKind
{
    /// <summary><c>bit</c>, as a JSON boolean.</summary>
    Boolean,

    /// <summary><c>tinyint</c>, <c>smallint</c>, <c>int</c>, <c>bigint</c>, as a JSON integer.</summary>
    Integer,

    /// <summary><c>decimal</c> and <c>numeric</c>, as a JSON string carrying all 38 possible digits.</summary>
    Decimal,

    /// <summary><c>money</c> and <c>smallmoney</c>, as a JSON string with the four decimals both types have.</summary>
    Money,

    /// <summary><c>float</c>, as a JSON number, shortest round-trippable.</summary>
    Double,

    /// <summary><c>real</c>, as a JSON number, shortest round-trippable at single precision.</summary>
    Single,

    /// <summary><c>date</c>, as <c>yyyy-MM-dd</c>.</summary>
    Date,

    /// <summary><c>time</c>, as <c>HH:mm:ss[.fffffff]</c>.</summary>
    Time,

    /// <summary><c>datetime</c>, <c>smalldatetime</c> and <c>datetime2</c>, as ISO 8601 with a <c>T</c>.</summary>
    DateTime,

    /// <summary><c>datetimeoffset</c>, as ISO 8601 with a <c>T</c> and an offset.</summary>
    DateTimeOffset,

    /// <summary>Every character type, and <c>xml</c>, as a JSON string.</summary>
    String,

    /// <summary><c>uniqueidentifier</c>, as the lowercase 8-4-4-4-12 form.</summary>
    Guid,

    /// <summary><c>binary</c>, <c>varbinary</c>, <c>image</c>, <c>timestamp</c>, as base64.</summary>
    Binary,

    /// <summary>
    /// <c>hierarchyid</c>, <c>geography</c> and <c>geometry</c>, as base64 of the raw
    /// CLR serialisation the server sends. These are the only types whose .NET value
    /// cannot be materialised without <c>Microsoft.SqlServer.Types</c> - and do not need
    /// to be, because the bytes go straight back in through a <c>varbinary</c> parameter.
    /// </summary>
    UserDefinedBinary
}
