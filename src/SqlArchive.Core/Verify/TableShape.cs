using System.Globalization;
using SqlSchemaDiff.Models;

namespace SqlArchive.Core.Verify;

/// <summary>
/// Says, in words, how two versions of one table are shaped differently.
/// <para>
/// This does not decide <i>whether</i> a table differs - the differ does that, and one
/// authority is the point, so that "verify says the schema matches" means "an import
/// would have nothing to do". This only explains a difference the differ has already
/// found, because "[dbo].[Customer] changed" is not an answer anybody can act on and
/// "the database has a column the archive does not: [Discount]" is.
/// </para>
/// </summary>
internal static class TableShape
{
    /// <summary>
    /// What the database has that the archive does not, and the other way round. Empty
    /// when the two are shaped the same, which happens when the difference the differ
    /// found is one this does not know how to name.
    /// </summary>
    public static IReadOnlyList<string> Describe(TableModel? archive, TableModel? database)
    {
        if(archive is null || database is null)
            return [];

        var words = new List<string>();

        Columns(archive, database, words);
        Counted("key constraint", "key constraints", archive.KeyConstraints.Count, database.KeyConstraints.Count, words);
        Counted("foreign key", "foreign keys", archive.ForeignKeys.Count, database.ForeignKeys.Count, words);
        Counted("check constraint", "check constraints", archive.CheckConstraints.Count, database.CheckConstraints.Count, words);
        Counted("index", "indexes", archive.Indexes.Count, database.Indexes.Count, words);

        if(!string.Equals(archive.TemporalType, database.TemporalType, StringComparison.OrdinalIgnoreCase))
        {
            words.Add(
                $"system versioning: {Temporal(archive.TemporalType)} in the archive, " +
                $"{Temporal(database.TemporalType)} in the database.");
        }

        return words;
    }

    private static void Columns(TableModel archive, TableModel database, List<string> words)
    {
        var byName = database.Columns.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach(var column in archive.Columns)
        {
            seen.Add(column.Name);

            if(!byName.TryGetValue(column.Name, out var other))
            {
                words.Add($"the archive has a column the database does not: [{column.Name}] {Type(column)}.");
                continue;
            }

            if(!string.Equals(Type(column), Type(other), StringComparison.OrdinalIgnoreCase))
                words.Add($"[{column.Name}]: {Type(column)} in the archive, {Type(other)} in the database.");

            if(column.IsNullable != other.IsNullable)
            {
                words.Add(
                    $"[{column.Name}]: {Null(column.IsNullable)} in the archive, " +
                    $"{Null(other.IsNullable)} in the database.");
            }

            if(column.IsIdentity != other.IsIdentity)
            {
                words.Add(column.IsIdentity
                    ? $"[{column.Name}] is an identity in the archive and is not in the database."
                    : $"[{column.Name}] is an identity in the database and is not in the archive.");
            }

            if(column.IsComputed != other.IsComputed)
            {
                words.Add(column.IsComputed
                    ? $"[{column.Name}] is computed in the archive and is not in the database."
                    : $"[{column.Name}] is computed in the database and is not in the archive.");
            }

            // Only the definition, never the name: SQL Server names a default itself
            // with a per-database suffix, so two correct restores of the same archive
            // disagree about the name and agree about everything that matters.
            if(!string.Equals(Normalise(column.DefaultDefinition), Normalise(other.DefaultDefinition), StringComparison.OrdinalIgnoreCase))
            {
                words.Add(
                    $"[{column.Name}] defaults to {Default(column.DefaultDefinition)} in the archive and " +
                    $"{Default(other.DefaultDefinition)} in the database.");
            }
        }

        foreach(var column in database.Columns)
        {
            if(!seen.Contains(column.Name))
                words.Add($"the database has a column the archive does not: [{column.Name}] {Type(column)}.");
        }
    }

    /// <summary>
    /// The count of something both sides have, when the two counts disagree. Names
    /// rather than counts would be worse: SQL Server generates most constraint names and
    /// two correct restores of one archive carry different ones.
    /// </summary>
    private static void Counted(string singular, string plural, int archive, int database, List<string> words)
    {
        if(archive == database)
            return;

        words.Add(
            $"{Plural(archive, singular, plural)} in the archive and " +
            $"{database.ToString(CultureInfo.InvariantCulture)} in the database.");
    }

    private static string Type(ColumnModel column)
    {
        var name = column.IsUserDefinedType && !string.IsNullOrEmpty(column.TypeSchema)
            ? $"{column.TypeSchema}.{column.TypeName}"
            : column.TypeName;

        return column.TypeName.ToLowerInvariant() switch
        {
            "decimal" or "numeric" =>
                $"{name}({column.Precision.ToString(CultureInfo.InvariantCulture)},{column.Scale.ToString(CultureInfo.InvariantCulture)})",

            "datetime2" or "time" or "datetimeoffset" =>
                $"{name}({column.Scale.ToString(CultureInfo.InvariantCulture)})",

            "char" or "varchar" or "binary" or "varbinary" =>
                $"{name}({Length(column.MaxLength, 1)})",

            "nchar" or "nvarchar" =>
                $"{name}({Length(column.MaxLength, 2)})",

            _ => name
        };
    }

    /// <summary><c>sys.columns.max_length</c> is bytes, and -1 is MAX.</summary>
    private static string Length(short maxLength, int bytesPerCharacter) =>
        maxLength < 0 ? "max" : (maxLength / bytesPerCharacter).ToString(CultureInfo.InvariantCulture);

    private static string Null(bool nullable) => nullable ? "NULL" : "NOT NULL";

    private static string Default(string? definition) =>
        string.IsNullOrWhiteSpace(definition) ? "nothing" : definition;

    private static string Temporal(string? temporalType) =>
        string.IsNullOrWhiteSpace(temporalType) ? "off" : temporalType;

    private static string Normalise(string? definition) =>
        string.Concat((definition ?? string.Empty).Where(c => !char.IsWhiteSpace(c)));

    private static string Plural(int count, string singular, string plural) =>
        $"{count.ToString(CultureInfo.InvariantCulture)} {(count == 1 ? singular : plural)}";
}
