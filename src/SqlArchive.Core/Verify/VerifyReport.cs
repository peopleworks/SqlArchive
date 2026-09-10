using System.Text.Json.Serialization;

namespace SqlArchive.Core.Verify;

/// <summary>
/// The verdict: what was compared, what matched, and for everything that did not, what
/// differs.
/// <para>
/// A verify that ran and found differences is not a failed verify. The report says so
/// in <see cref="HasDifferences"/> rather than by throwing, because "the comparison
/// found drift" and "the comparison could not be made" are two different answers and a
/// script has to be able to tell them apart.
/// </para>
/// </summary>
public sealed class VerifyReport
{
    /// <summary>The archive that was verified, as the caller named it.</summary>
    public string Archive { get; init; } = string.Empty;

    /// <summary>
    /// The database it was compared against, or null when nothing but the archive was
    /// read. Taken from the server rather than from the connection string, so that it
    /// says which database actually answered.
    /// </summary>
    public string? Database { get; init; }

    public DateTimeOffset CheckedAt { get; init; }

    public TimeSpan Elapsed { get; init; }

    /// <summary>Whether the archive still is what its manifest says it is.</summary>
    public IntegrityVerdict Integrity { get; init; } = new();

    /// <summary>
    /// The schema comparison, or null when no database was named or the manifest carries
    /// no schema snapshot to compare.
    /// </summary>
    public SchemaVerdict? Schema { get; init; }

    /// <summary>
    /// One verdict per table, empty when no database was named. A table that is only in
    /// the database gets one too - being extra is an answer, and a list built solely
    /// from the manifest could never give it.
    /// </summary>
    public IReadOnlyList<TableVerdict> Tables { get; init; } = [];

    /// <summary>
    /// Everything the operator should know that is not a difference: what was left out,
    /// what could not be compared and why, what was deliberately not treated as drift.
    /// </summary>
    public IReadOnlyList<string> Notices { get; init; } = [];

    /// <summary>Tables whose data was compared and matched, hash and count both.</summary>
    public int Matching => Tables.Count(t => t.Outcome == TableOutcome.Matches);

    /// <summary>Tables that differ, of any kind.</summary>
    public int Differing => Tables.Count(t => t.Differs);

    /// <summary>
    /// Tables the manifest cannot answer for: their data was deliberately not archived,
    /// or the archive carries no hash for them. Not differences, and not matches either.
    /// </summary>
    public int Unverifiable => Tables.Count(t => t.Outcome == TableOutcome.NotVerifiable);

    /// <summary>
    /// True when anything was found that a person would want to know about: a corrupt or
    /// missing entry, a schema that does not line up, or a table that does not match.
    /// <para>
    /// An entry the manifest could not answer for is deliberately not one of them. It is
    /// a limit of the archive, stated in the report, and not evidence that anything
    /// changed.
    /// </para>
    /// </summary>
    public bool HasDifferences =>
        !Integrity.Matches ||
        Schema is { Matches: false } ||
        Tables.Any(t => t.Differs);
}

/// <summary>
/// Whether the bytes in the archive are the bytes the manifest describes, and whether
/// the manifest describes the bytes that are there.
/// </summary>
/// <remarks>
/// Both directions, because a manifest that is merely self-consistent proves nothing.
/// An entry declared and absent is a truncated archive; an entry present and undeclared
/// is an archive whose manifest does not cover it, and <c>FORMAT.md</c> says the two
/// halves of the manifest cover every entry exactly once.
/// </remarks>
public sealed class IntegrityVerdict
{
    public IReadOnlyList<EntryVerdict> Entries { get; init; } = [];

    /// <summary>
    /// False when an undeclared entry is only reported and not counted against the
    /// archive: a manifest from a later format version is allowed to describe entries
    /// this build has never heard of.
    /// </summary>
    public bool UndeclaredCounts { get; init; } = true;

    public int Intact => Entries.Count(e => e.State == EntryState.Intact);

    public int Corrupt => Entries.Count(e => e.State == EntryState.Corrupt);

    public int Missing => Entries.Count(e => e.State == EntryState.Missing);

    public int Undeclared => Entries.Count(e => e.State == EntryState.Undeclared);

    /// <summary>Entries the manifest names but records no hash for, so their bytes cannot be checked.</summary>
    public int Unhashed => Entries.Count(e => e.State == EntryState.NotHashed);

    /// <summary>Entries deliberately left unread, which is what <c>--schema-only</c> does to the data.</summary>
    public int NotChecked => Entries.Count(e => e.State == EntryState.NotChecked);

    public bool Matches => Corrupt == 0 && Missing == 0 && (!UndeclaredCounts || Undeclared == 0);
}

/// <summary>What became of one entry when it was checked against the manifest.</summary>
/// <param name="Entry">The entry name, as the zip holds it.</param>
/// <param name="State">The verdict.</param>
/// <param name="Declared">What the manifest says it should hash to, or null.</param>
/// <param name="Actual">What it does hash to, or null when it was not read.</param>
public sealed record EntryVerdict(string Entry, EntryState State, string? Declared, string? Actual);

public enum EntryState
{
    /// <summary>Present, and its bytes hash to what the manifest declares.</summary>
    Intact,

    /// <summary>Present, and its bytes hash to something else.</summary>
    Corrupt,

    /// <summary>The manifest declares it and the archive does not hold it.</summary>
    Missing,

    /// <summary>The archive holds it and the manifest does not account for it.</summary>
    Undeclared,

    /// <summary>Named by the manifest with no hash beside it - a dbdumper archive is like this throughout.</summary>
    NotHashed,

    /// <summary>Not read, because this run was not asked to read it.</summary>
    NotChecked
}

/// <summary>
/// The schema of the archive against the schema of the database, through the same differ
/// a restore-as-migration would use.
/// <para>
/// One authority on purpose. "Verify says the schema matches" has to mean "an import
/// would have nothing to do", and it can only mean that if both answers come from the
/// same comparison.
/// </para>
/// </summary>
public sealed class SchemaVerdict
{
    /// <summary>Objects the archive has and the database does not.</summary>
    public IReadOnlyList<string> OnlyInArchive { get; init; } = [];

    /// <summary>Objects the database has and the archive does not.</summary>
    public IReadOnlyList<string> OnlyInDatabase { get; init; } = [];

    /// <summary>Objects both have, differently.</summary>
    public IReadOnlyList<string> Differing { get; init; } = [];

    /// <summary>
    /// What the comparison deliberately did not look at, and why. A verify that cried
    /// drift over something a clean round trip cannot avoid would be worthless, so
    /// everything left out is written down here rather than left to be discovered.
    /// </summary>
    public IReadOnlyList<string> Ignored { get; init; } = [];

    public bool Matches => OnlyInArchive.Count == 0 && OnlyInDatabase.Count == 0 && Differing.Count == 0;
}

/// <summary>
/// One table's verdict. The headline is <see cref="Outcome"/>; everything found is in
/// <see cref="Differences"/>, because a table can be wrong in more than one way at once
/// and reporting only the first would hide the rest.
/// </summary>
public sealed class TableVerdict
{
    public string Schema { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public TableOutcome Outcome { get; init; }

    /// <summary>Rows the manifest declares, or null when it does not say.</summary>
    public long? ArchiveRows { get; init; }

    /// <summary>Rows the database holds, or null when it was not read.</summary>
    public long? DatabaseRows { get; init; }

    public string? ArchiveHash { get; init; }

    public string? DatabaseHash { get; init; }

    /// <summary>The <c>--where</c> the table was archived with, if any. Both sides were read through it.</summary>
    public string? RowFilter { get; init; }

    /// <summary>True when the archive carries this table's schema and deliberately not its rows.</summary>
    public bool DataSkipped { get; init; }

    /// <summary>
    /// True when the content hashes were actually compared. False says the count is all
    /// that was checked, which is worth knowing before trusting a match.
    /// </summary>
    public bool ContentCompared { get; init; }

    /// <summary>What differs, in sentences. Empty when nothing does.</summary>
    public IReadOnlyList<string> Differences { get; init; } = [];

    /// <summary>Why this table could not be compared as fully as the others, or null.</summary>
    public string? Limitation { get; init; }

    public string Identifier => $"[{Schema}].[{Name}]";

    /// <summary>True for an outcome that is a difference rather than an absence of one.</summary>
    [JsonIgnore]
    public bool Differs => Outcome is not (TableOutcome.Matches or TableOutcome.NotCompared or TableOutcome.NotVerifiable);
}

/// <summary>
/// What a table's comparison came to. Ordered by how much it matters: a table that is
/// missing is a different answer from one whose rows moved, and the count matching while
/// the content does not is the answer a row count cannot reach.
/// </summary>
public enum TableOutcome
{
    /// <summary>Same schema, same rows, same content.</summary>
    Matches,

    /// <summary>Not looked at: left out by a glob, or <c>--schema-only</c>.</summary>
    NotCompared,

    /// <summary>
    /// The manifest cannot answer for it: the rows were deliberately not archived, or
    /// the archive carries no hash for them. Not a difference and not a match.
    /// </summary>
    NotVerifiable,

    /// <summary>The archive has the table and the database does not.</summary>
    MissingFromDatabase,

    /// <summary>The database has the table and the archive does not.</summary>
    OnlyInDatabase,

    /// <summary>Both have it, shaped differently.</summary>
    SchemaDiffers,

    /// <summary>Both have it, with a different number of rows.</summary>
    RowCountDiffers,

    /// <summary>
    /// The same number of rows, holding different values. The case a row count cannot
    /// see, and the one this tool exists for: a thousand modified rows are still a
    /// thousand rows.
    /// </summary>
    ContentDiffers,

    /// <summary>The table is there and could not be read. The reason is in the differences.</summary>
    Unreadable
}
