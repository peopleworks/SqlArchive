using SqlArchive.Cli.Commands;

namespace SqlArchive.Core.Tests;

/// <summary>
/// What a command may repeat of a connection string it was given: the database and the
/// server, and nothing that could be the password.
/// </summary>
public sealed class ConnectionTextTests
{
    [Theory]
    [InlineData("Server=SQL1;Database=Ventas;User Id=sa;Password=secret", "Ventas on SQL1")]
    [InlineData("Data Source=SQL1;Initial Catalog=Ventas;Integrated Security=true", "Ventas on SQL1")]
    [InlineData("Server=SQL1;User Id=sa;Password=secret", "SQL1")]
    public void ADatabaseIsNamedByItsNameAndItsServer(string connectionString, string expected)
    {
        var described = ConnectionText.Describe(connectionString, "the source");

        Assert.Equal(expected, described);
        Assert.DoesNotContain("secret", described, StringComparison.Ordinal);
    }

    /// <summary>
    /// A string that does not parse is not echoed back as the next best thing: whatever
    /// made it malformed may sit right beside the password.
    /// </summary>
    [Theory]
    [InlineData("Password=secret;this is not a connection string")]
    [InlineData("Password=secret")]
    [InlineData("")]
    public void AStringThatNamesNoServerFallsBackAndRepeatsNothing(string connectionString)
    {
        Assert.Equal("the source", ConnectionText.Describe(connectionString, "the source"));
    }
}
