using Microsoft.Data.SqlClient;

namespace SqlArchive.Cli.Commands;

/// <summary>
/// How a command names the database it was pointed at, in anything it prints.
/// <para>
/// Never by the connection string. It carries the password, and what a command prints is
/// read over shoulders, captured whole by CI logs and pasted into issues. The database and
/// the server are what an operator needs to recognise which run failed, and they are all
/// of the string that is safe to repeat.
/// </para>
/// </summary>
internal static class ConnectionText
{
    /// <summary>
    /// "Ventas on SQL1", the server alone when the string names no database, or
    /// <paramref name="fallback"/> when it cannot be parsed at all.
    /// </summary>
    public static string Describe(string connectionString, string fallback)
    {
        try
        {
            var builder = new SqlConnectionStringBuilder(connectionString);

            if(string.IsNullOrWhiteSpace(builder.DataSource))
                return fallback;

            return string.IsNullOrWhiteSpace(builder.InitialCatalog)
                ? builder.DataSource
                : $"{builder.InitialCatalog} on {builder.DataSource}";
        }
        catch(ArgumentException)
        {
            return fallback;
        }
    }
}
