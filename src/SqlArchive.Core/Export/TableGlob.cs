using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace SqlArchive.Core.Export;

/// <summary>
/// Matches a table against the patterns an operator typed.
/// <para>
/// Two shapes, and which one is meant is decided by the pattern rather than by a flag:
/// a pattern with a dot in it is matched against <c>schema.name</c>, and one without is
/// matched against the bare table name. So <c>Customer</c> finds <c>[dbo].[Customer]</c>
/// without anyone having to write <c>dbo.</c>, and <c>sales.*</c> takes a whole schema.
/// </para>
/// </summary>
public static class TableGlob
{
    private static readonly ConcurrentDictionary<string, Regex> Compiled = new(StringComparer.Ordinal);

    /// <summary>True when the pattern names this table.</summary>
    public static bool Matches(string pattern, string schema, string name)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(name);

        var regex = Compiled.GetOrAdd(pattern, Build);

        return pattern.Contains('.', StringComparison.Ordinal)
            ? regex.IsMatch($"{schema}.{name}")
            : regex.IsMatch(name);
    }

    /// <summary>True when any of the patterns names this table. An empty list matches nothing.</summary>
    public static bool MatchesAny(IEnumerable<string> patterns, string schema, string name)
    {
        ArgumentNullException.ThrowIfNull(patterns);

        foreach(var pattern in patterns)
        {
            if(Matches(pattern, schema, name))
                return true;
        }

        return false;
    }

    /// <summary>
    /// The first value whose pattern names this table, or null. First rather than most
    /// specific: "most specific" has no definition that survives two patterns of the same
    /// length, and an order the caller wrote down is one they can reason about.
    /// </summary>
    public static string? FirstMatch(IEnumerable<KeyValuePair<string, string>> patterns, string schema, string name)
    {
        ArgumentNullException.ThrowIfNull(patterns);

        foreach(var (pattern, value) in patterns)
        {
            if(Matches(pattern, schema, name))
                return value;
        }

        return null;
    }

    /// <summary>
    /// A glob as a regex. Case-insensitive, because SQL Server's default collation is and
    /// somebody typing a table name is not thinking about collation.
    /// </summary>
    private static Regex Build(string pattern)
    {
        var expression = Regex.Escape(pattern)
            .Replace("\\*", ".*", StringComparison.Ordinal)
            .Replace("\\?", ".", StringComparison.Ordinal);

        return new Regex(
            $"^{expression}$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
