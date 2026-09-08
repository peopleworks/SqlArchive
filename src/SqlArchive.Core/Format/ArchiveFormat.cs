using System.Text;

namespace SqlArchive.Core.Format;

/// <summary>
/// The constants of the archive format: the version, the names of the entries in the
/// zip, and the rules for turning a SQL identifier into an entry name.
/// <para>
/// These are normative. <c>FORMAT.md</c> in the repository root says the same things in
/// prose, and any change to either has to change both.
/// </para>
/// </summary>
public static class ArchiveFormat
{
    /// <summary>
    /// The version of the archive shape this build writes, and the highest it can read
    /// as data. A file that declares a higher one still opens far enough to be
    /// inspected and to say what is missing - see <see cref="ManifestSerializer"/>.
    /// </summary>
    public const int CurrentVersion = 1;

    /// <summary>
    /// Written into <see cref="ArchiveManifest.Format"/> so a reader can tell one of our
    /// archives from someone else's. dbdumper's manifest also has a
    /// <c>formatVersion</c> of 1, and without a discriminator the two are told apart
    /// only by which properties happen to deserialize.
    /// </summary>
    public const string FormatName = "sqlarchive";

    /// <summary>The conventional file extension. Not enforced anywhere.</summary>
    public const string FileExtension = ".sqlarchive";

    public const string ManifestEntry = "manifest.json";

    public const string ReadmeEntry = "README.txt";

    public const string SchemaDirectory = "schema/";

    public const string DataDirectory = "data/";

    /// <summary>
    /// The prefix on every hash string in the manifest that is a plain SHA-256 of a
    /// file. <see cref="ArchiveTableEntry.RowHash"/> deliberately carries no prefix: it
    /// is not the SHA-256 of anything, it is the sum of many of them.
    /// </summary>
    public const string HashPrefix = "sha256:";

    /// <summary>
    /// Every JSONL line ends with one of these, on every platform. A writer that let
    /// the environment choose would produce archives whose row hashes differ between
    /// Windows and Linux for the same rows, which is the one thing the hash exists to
    /// rule out. The line terminator is not part of the hashed bytes either way - see
    /// <see cref="RowHash"/> - but it is part of the file, and the file is hashed too.
    /// </summary>
    public const byte LineTerminator = (byte)'\n';

    /// <summary>
    /// UTF-8 with no byte-order mark. A BOM at the head of a JSONL file would be part
    /// of the first line and nothing else, which is exactly the kind of asymmetry that
    /// makes a format hard to read by hand years later.
    /// </summary>
    public static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>The entry name for one phase of the schema script.</summary>
    public static string SchemaEntry(string phaseFileName) =>
        SchemaDirectory + phaseFileName;

    /// <summary>
    /// The entry name for a table's rows: <c>data/dbo.Customer.jsonl</c>, or
    /// <c>data/dbo.Order.0000.jsonl</c> when the table was split into ranges.
    /// </summary>
    /// <param name="schema">The table's schema, unescaped.</param>
    /// <param name="table">The table's name, unescaped.</param>
    /// <param name="part">
    /// The range number, or null for a table written as a single file. Four digits, so
    /// a listing sorts into the order the ranges were read.
    /// </param>
    public static string DataEntry(string schema, string table, int? part = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(schema);
        ArgumentException.ThrowIfNullOrEmpty(table);

        if(part is < 0)
            throw new ArgumentOutOfRangeException(nameof(part), part, "A range number cannot be negative.");

        var name = new StringBuilder(DataDirectory)
            .Append(EscapeIdentifier(schema))
            .Append('.')
            .Append(EscapeIdentifier(table));

        if(part is not null)
            name.Append('.').Append(part.Value.ToString("0000", System.Globalization.CultureInfo.InvariantCulture));

        return name.Append(".jsonl").ToString();
    }

    /// <summary>
    /// Percent-encodes the characters that would make an entry name ambiguous or
    /// unextractable, and leaves everything else alone.
    /// <para>
    /// A SQL identifier may contain almost anything: <c>[My.Table]</c>, <c>[a/b]</c>,
    /// even a newline. The dot is escaped along with the filesystem-hostile characters
    /// because the dot is what separates schema from table from range number in the
    /// name above - without that, <c>[dbo].[My.Table]</c> and <c>[dbo.My].[Table]</c>
    /// would produce the same entry.
    /// </para>
    /// <para>
    /// The encoding is injective, so two different identifiers can never collide. It is
    /// not reversed anywhere: the manifest lists each table's entry names explicitly, so
    /// a reader never has to parse a name back into an identifier.
    /// </para>
    /// </summary>
    /// <remarks>
    /// Two tables whose names differ only in case are distinct in a case-sensitive
    /// collation and produce distinct entry names, which a zip stores happily. Extracting
    /// both onto a case-insensitive filesystem would collide; reading them out of the
    /// archive, which is what SqlArchive does, does not.
    /// </remarks>
    public static string EscapeIdentifier(string identifier)
    {
        ArgumentNullException.ThrowIfNull(identifier);

        var builder = new StringBuilder(identifier.Length);

        foreach(var rune in identifier.EnumerateRunes())
        {
            if(rune.IsBmp && NeedsEscape((char)rune.Value))
            {
                foreach(var b in Utf8.GetBytes(rune.ToString()))
                    builder.Append('%').Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
            }
            else
            {
                builder.Append(rune.ToString());
            }
        }

        // A name that ends in a space or a dot cannot be created on Windows, and the dot
        // is already escaped above, so only the space is left to deal with here.
        if(builder.Length > 0 && builder[^1] == ' ')
        {
            builder.Length -= 1;
            builder.Append("%20");
        }

        return builder.ToString();
    }

    private static bool NeedsEscape(char c) =>
        c < 0x20 || c == 0x7F || c is '%' or '.' or '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|';
}
