using System.Reflection;

namespace SqlArchive.Core.Tests;

/// <summary>
/// SqlArchive carries no engine of its own, and this is what keeps saying so.
/// <para>
/// The whole design rests on composing two published packages rather than copying them.
/// That rule has been expensive to learn in this family: the schema comparer was forked
/// into a consumer once and drifted 1,314 lines away from its origin before anyone
/// noticed. So the composition is asserted rather than assumed - both engines have to be
/// reachable, and they have to arrive as packages.
/// </para>
/// </summary>
public sealed class CompositionTests
{
    [Fact]
    public void BothEnginesAreReachableFromTheCore()
    {
        var schema = Type.GetType(
            "SqlSchemaDiff.Services.SqlServerSchemaExtractor, SqlSchemaDiff.Core");

        var data = Type.GetType(
            "SyncJob.Core.Run.JobRunner, SyncJob.Core");

        Assert.NotNull(schema);
        Assert.NotNull(data);
    }

    /// <summary>
    /// The versions this design was written against. A bump is fine - it is a line in a
    /// csproj - but it should be a decision someone made, not something that happened,
    /// because upgrading the schema reader has already broken a consumer twice. 1.7.0
    /// started rendering temporal and memory-optimized tables faithfully, which is right
    /// for a diff and was wrong for the staging table SyncJob cloned with it. And 1.8.0
    /// removed a method SyncJob.Core 1.0.0 was compiled against: moving this project
    /// onto it, before building anything, is what found it - nine import tests failed
    /// with MissingMethodException inside SyncJob's staging factory. 1.8.1 put it back.
    /// </summary>
    [Theory]
    [InlineData("SqlSchemaDiff.Core", "1.8")]
    [InlineData("SyncJob.Core", "1.0")]
    public void TheEnginesAreTheVersionsThisWasDesignedAgainst(string assemblyName, string expected)
    {
        var assembly = Assembly.Load(assemblyName);
        var version = assembly.GetName().Version;

        Assert.NotNull(version);
        Assert.Equal(expected, $"{version.Major}.{version.Minor}");
    }

    /// <summary>
    /// And they arrive as packages, not as project references to a checkout sitting
    /// beside this one. A project reference would work on the machine that has all three
    /// repositories cloned and nowhere else - and it is exactly how a fork starts.
    /// </summary>
    [Fact]
    public void TheEnginesArriveAsPackagesAndNotFromASiblingCheckout()
    {
        foreach(var name in new[] { "SqlSchemaDiff.Core", "SyncJob.Core" })
        {
            var location = Assembly.Load(name).Location;

            Assert.False(
                string.IsNullOrEmpty(location),
                $"{name} has no location, so where it came from cannot be checked");

            // A package's assembly is copied into the output folder from the NuGet cache;
            // it never sits under a src/ of a repository that is not this one.
            Assert.DoesNotContain(
                $"{Path.DirectorySeparatorChar}src{Path.DirectorySeparatorChar}",
                location,
                StringComparison.OrdinalIgnoreCase);
        }
    }
}
