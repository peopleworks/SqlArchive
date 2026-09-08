using System.Globalization;
using System.Text;

namespace SqlArchive.Core.Format;

/// <summary>
/// Writes the <c>README.txt</c> that goes in every archive.
/// <para>
/// This is part of the format, not decoration. The case the whole design turns on is the
/// one where the tool is gone and the archive is not, and in that case the only thing
/// standing between a person and a directory of JSON is a page that says what the files
/// are, what order the SQL runs in, and how to read a value out of a line. It is written
/// in the archive rather than kept in a repository because a repository is one more thing
/// that has to still exist.
/// </para>
/// </summary>
public static class ArchiveReadme
{
    public static string Compose(ArchiveManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var text = new StringBuilder();

        text.AppendLine("SqlArchive")
            .AppendLine("==========")
            .AppendLine()
            .AppendLine(Invariant($"Database   : {manifest.Source.Database}"))
            .AppendLine(Invariant($"Server     : {manifest.Source.Server}"))
            .AppendLine(Invariant($"Taken      : {manifest.CreatedAt:yyyy-MM-dd HH:mm:ss} UTC"))
            .AppendLine(Invariant($"Written by : {manifest.Tool.Name} {manifest.Tool.Version}"))
            .AppendLine(Invariant($"Format     : {ArchiveFormat.FormatName} version {manifest.FormatVersion}"))
            .AppendLine(Invariant($"Rows read  : {Describe(manifest.Consistency)}"))
            .AppendLine();

        if(manifest.Source.Collation is { Length: > 0 })
            text.AppendLine(Invariant($"Collation  : {manifest.Source.Collation}"));

        if(manifest.Source.ProductVersion is { Length: > 0 })
            text.AppendLine(Invariant($"SQL Server : {manifest.Source.ProductVersion} {manifest.Source.Edition}"));

        text.AppendLine()
            .AppendLine("What is in here")
            .AppendLine("---------------")
            .AppendLine()
            .AppendLine("  manifest.json   The schema of the whole database, plus per table the row count and")
            .AppendLine("                  the content hash. Everything else in the archive is described here.")
            .AppendLine("  schema/*.sql    The schema, as SQL you can run by hand. Run them in the order of")
            .AppendLine("                  their numeric prefixes: schemas and types before tables, tables")
            .AppendLine("                  before rows, indexes and foreign keys after them.")
            .AppendLine("  data/*.jsonl    One table's rows. One JSON object per line, its properties in the")
            .AppendLine("                  table's column order. A table read in ranges has several files,")
            .AppendLine("                  numbered; the manifest lists them.")
            .AppendLine()
            .AppendLine("Reading a value")
            .AppendLine("---------------")
            .AppendLine()
            .AppendLine("  null                 SQL NULL, which is not the empty string.")
            .AppendLine("  true / false         bit.")
            .AppendLine("  a number             tinyint, smallint, int, bigint, float, real.")
            .AppendLine("  \"1.0000\"             decimal, numeric, money and smallmoney are strings, so that")
            .AppendLine("                       38 digits of precision survive a JSON parser that would")
            .AppendLine("                       otherwise put them through a double. The decimals shown are")
            .AppendLine("                       the ones the column declares.")
            .AppendLine("  \"2026-01-15\"         date.")
            .AppendLine("  \"10:00:00.1234567\"   time. The fraction appears only where there is one.")
            .AppendLine("  \"2026-01-15T10:00:00.003\"")
            .AppendLine("                       datetime, smalldatetime, datetime2. ISO 8601 with a T, which")
            .AppendLine("                       every language setting reads the same way.")
            .AppendLine("  \"2026-01-15T10:00:00+02:00\"")
            .AppendLine("                       datetimeoffset.")
            .AppendLine("  \"3f2504e0-4f89-11d3-9a0c-0305e82c3301\"")
            .AppendLine("                       uniqueidentifier, lower case.")
            .AppendLine("  \"3q2+7w==\"           binary, varbinary, image, and hierarchyid, geography and")
            .AppendLine("                       geometry, all base64.")
            .AppendLine("  \"text\"               char, varchar, nchar, nvarchar, text, ntext, xml. UTF-8, with")
            .AppendLine("                       only the escapes JSON requires.")
            .AppendLine()
            .AppendLine("Columns a table has and this archive does not carry: computed columns, rowversion,")
            .AppendLine("and the GENERATED ALWAYS columns of a system-versioned table. SQL Server assigns all")
            .AppendLine("three itself and refuses to be told what they are, so carrying them would produce a")
            .AppendLine("restore that fails on its first row. Each table below lists its own.")
            .AppendLine()
            .AppendLine("Tables")
            .AppendLine("------")
            .AppendLine();

        foreach(var table in manifest.Tables.OrderBy(t => t.Schema, StringComparer.Ordinal).ThenBy(t => t.Name, StringComparer.Ordinal))
        {
            text.AppendLine(Invariant($"  {table.Identifier}"));
            text.AppendLine(table.DataSkipped
                ? "      schema only - the rows were deliberately not exported"
                : Invariant($"      {table.RowCount} rows, hash {table.RowHash}"));

            if(table.RowFilter is { Length: > 0 })
                text.AppendLine(Invariant($"      filtered: {table.RowFilter}"));

            foreach(var file in table.DataFiles)
                text.AppendLine(Invariant($"      {file}"));

            foreach(var omitted in table.OmittedColumns)
                text.AppendLine(Invariant($"      column {omitted.Key} not carried ({omitted.Value})"));
        }

        text.AppendLine()
            .AppendLine("The hash")
            .AppendLine("--------")
            .AppendLine()
            .AppendLine("A table's hash is the sum, modulo 2^256, of the SHA-256 of each of its lines - the")
            .AppendLine("line exactly as it is in the file, without its newline. Adding them up, rather than")
            .AppendLine("hashing them one after another, is what makes it independent of the order the rows")
            .AppendLine("were read in, so a table read in parallel ranges, or read back in a different")
            .AppendLine("order, still comes to the same value. Adding rather than XOR-ing is what stops two")
            .AppendLine("identical rows cancelling each other out.")
            .AppendLine()
            .AppendLine("It says that the content is the same; it does not say which row changed. The row")
            .AppendLine("count beside it is part of the check, not decoration.")
            .AppendLine()
            .AppendLine("The file hashes in the manifest are plain SHA-256 of each entry, prefixed sha256:.")
            .AppendLine("Between the two, an archive can be checked for damage without a database anywhere")
            .AppendLine("near it.")
            .AppendLine();

        return text.ToString();
    }

    private static string Describe(ArchiveConsistency consistency) => consistency switch
    {
        ArchiveConsistency.Snapshot => "from a database snapshot - every table at the same instant",
        ArchiveConsistency.SnapshotIsolation => "through one connection under SNAPSHOT isolation - every table at the same instant",
        // Not "each table is consistent in itself". That was written before ranges
        // existed and it was never quite true even then: under READ COMMITTED a long
        // scan can see rows committed after it started. Reading a large table in
        // parallel ranges widens a window that was already open, and an archive that
        // overstates its own consistency is worse than one that admits it, because the
        // overstatement is what someone relies on years later.
        _ => "table by table, and a large table range by range - rows within one table may be moments apart, and two tables more so"
    };

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
