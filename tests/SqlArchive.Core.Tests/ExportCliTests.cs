using SqlArchive.Cli.Commands;
using SqlArchive.Core.Export;

namespace SqlArchive.Core.Tests;

/// <summary>
/// The wiring of <c>export</c>, tested on its own rather than end to end: the
/// <c>--where</c> parser and the mapping from <see cref="ExportCommand.Settings"/> onto
/// <see cref="ExportOptions"/>. No server anywhere in this file - the engine's own
/// behaviour is <c>ExportLiveTests</c>'s to prove, and the CLI's job, checked here, is
/// only that what an operator typed reaches it unchanged.
/// </summary>
public sealed class ExportCliTests
{
    // ---------------------------------------------------------------- --where parsing

    [Fact]
    public void AWhereClauseSplitsOnTableAndPredicate()
    {
        Assert.True(ExportCommand.TryParseWhere("dbo.Order=Total>0", out var table, out var predicate, out var error));

        Assert.Equal("dbo.Order", table);
        Assert.Equal("Total>0", predicate);
        Assert.Null(error);
    }

    /// <summary>
    /// Most predicates contain their own '=' - an equality test is the most common
    /// predicate there is - so only the first '=' can be the separator between the table
    /// and the predicate, or a filter like this one would be cut in half.
    /// </summary>
    [Fact]
    public void APredicateContainingItsOwnEqualsSignIsKeptWhole()
    {
        Assert.True(ExportCommand.TryParseWhere("dbo.Order=Status='Active'", out var table, out var predicate, out _));

        Assert.Equal("dbo.Order", table);
        Assert.Equal("Status='Active'", predicate);
    }

    [Fact]
    public void AWhereClauseCanHaveMultipleEqualsSignsInThePredicate()
    {
        Assert.True(ExportCommand.TryParseWhere("dbo.T=A=1 AND B=2", out var table, out var predicate, out _));

        Assert.Equal("dbo.T", table);
        Assert.Equal("A=1 AND B=2", predicate);
    }

    [Theory]
    [InlineData("dbo.OrderTotal>0")]
    [InlineData("")]
    public void AWhereClauseWithNoEqualsSignIsRejected(string spec)
    {
        Assert.False(ExportCommand.TryParseWhere(spec, out _, out _, out var error));
        Assert.Contains("TABLE=PREDICATE", error, StringComparison.Ordinal);
    }

    [Fact]
    public void AWhereClauseThatStartsWithEqualsHasNoTableAndIsRejected()
    {
        Assert.False(ExportCommand.TryParseWhere("=Total>0", out _, out _, out var error));
        Assert.Contains("TABLE=PREDICATE", error, StringComparison.Ordinal);
    }

    [Fact]
    public void AWhereClauseWithNoPredicateAfterTheEqualsIsRejected()
    {
        Assert.False(ExportCommand.TryParseWhere("dbo.Order=", out _, out _, out var error));
        Assert.Contains("no predicate", error, StringComparison.Ordinal);
    }

    [Fact]
    public void WhitespaceAroundTheTableAndThePredicateIsTrimmed()
    {
        Assert.True(ExportCommand.TryParseWhere(" dbo.Order = Total > 0 ", out var table, out var predicate, out _));

        Assert.Equal("dbo.Order", table);
        Assert.Equal("Total > 0", predicate);
    }

    // ---------------------------------------------------------------- Settings.Validate

    [Fact]
    public void ValidateRejectsAMalformedWhereClause()
    {
        var settings = new ExportCommand.Settings
        {
            Source = "Server=x",
            Out = "z.sqlarchive",
            Where = ["not-a-clause"]
        };

        var result = settings.Validate();

        Assert.False(result.Successful);
        Assert.Contains("TABLE=PREDICATE", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateAcceptsAWellFormedWhereClause()
    {
        var settings = new ExportCommand.Settings
        {
            Source = "Server=x",
            Out = "z.sqlarchive",
            Where = ["dbo.Order=Status='Active'"]
        };

        Assert.True(settings.Validate().Successful);
    }

    // ---------------------------------------------------------------- Settings -> ExportOptions

    [Fact]
    public void EveryFilterAndPathOptionReachesExportOptions()
    {
        var settings = new ExportCommand.Settings
        {
            Source = "Server=SQL2022;Database=Ventas;Integrated Security=true",
            Out = "Ventas.sqlarchive",
            Tables = ["dbo.*", "sales.Order"],
            Exclude = ["dbo.AuditLog"],
            Where = ["dbo.Order=Total>0", "dbo.Customer=Status='Active'"],
            Spool = @"C:\spool\ventas",
            Resume = true,
            Consistent = true
        };

        var options = ExportCommand.ToExportOptions(settings);

        Assert.Equal(settings.Source, options.ConnectionString);
        Assert.Equal(settings.Tables, options.IncludeTables);
        Assert.Equal(settings.Exclude, options.ExcludeTables);
        Assert.Equal(
            [new KeyValuePair<string, string>("dbo.Order", "Total>0"), new KeyValuePair<string, string>("dbo.Customer", "Status='Active'")],
            options.RowFilters);
        Assert.True(options.Consistent);
        Assert.Equal(settings.Spool, options.WorkingDirectory);
        Assert.True(options.Resumable);
        Assert.Empty(options.ExcludeData);
    }

    /// <summary>
    /// The one flag that is not a straight rename: --schema-only becomes a glob that
    /// matches every table, because <see cref="ExportOptions.ExcludeData"/> is a list of
    /// patterns and not a switch.
    /// </summary>
    [Fact]
    public void SchemaOnlyBecomesAWildcardExcludeDataPattern()
    {
        var settings = new ExportCommand.Settings
        {
            Source = "Server=x",
            Out = "z.sqlarchive",
            SchemaOnly = true
        };

        var options = ExportCommand.ToExportOptions(settings);

        Assert.Equal(["*"], options.ExcludeData);
    }

    [Fact]
    public void WithoutSchemaOnlyNoTableHasItsDataExcluded()
    {
        var settings = new ExportCommand.Settings { Source = "Server=x", Out = "z.sqlarchive" };

        Assert.Empty(ExportCommand.ToExportOptions(settings).ExcludeData);
    }

    /// <summary>
    /// --maxdop maps straight onto <see cref="ExportOptions.Parallelism"/>, which the
    /// engine counts in units - table ranges - and not in tables. Translating "N tables
    /// at a time" into a table count here would be exactly the mistake that stops a
    /// large, split table from using the parallelism --range-size gives it: this is
    /// checked by asserting the raw value lands unchanged.
    /// </summary>
    [Fact]
    public void MaxDopMapsDirectlyOntoParallelismInUnits()
    {
        var settings = new ExportCommand.Settings { Source = "Server=x", Out = "z.sqlarchive", MaxDop = 3 };

        Assert.Equal(3, ExportCommand.ToExportOptions(settings).Parallelism);
    }

    [Fact]
    public void WithNoMaxDopParallelismKeepsTheEnginesOwnDefault()
    {
        var settings = new ExportCommand.Settings { Source = "Server=x", Out = "z.sqlarchive" };
        var untouched = new ExportOptions { ConnectionString = "Server=x" };

        Assert.Equal(untouched.Parallelism, ExportCommand.ToExportOptions(settings).Parallelism);
    }

    [Fact]
    public void RangeSizeMapsOntoRowsPerRange()
    {
        var settings = new ExportCommand.Settings { Source = "Server=x", Out = "z.sqlarchive", RangeSize = 500 };

        Assert.Equal(500, ExportCommand.ToExportOptions(settings).RowsPerRange);
    }

    [Fact]
    public void WithNoRangeSizeRowsPerRangeKeepsTheEnginesOwnDefault()
    {
        var settings = new ExportCommand.Settings { Source = "Server=x", Out = "z.sqlarchive" };
        var untouched = new ExportOptions { ConnectionString = "Server=x" };

        Assert.Equal(untouched.RowsPerRange, ExportCommand.ToExportOptions(settings).RowsPerRange);
    }

    [Fact]
    public void ConsistentDefaultsToFalse()
    {
        var settings = new ExportCommand.Settings { Source = "Server=x", Out = "z.sqlarchive" };

        Assert.False(ExportCommand.ToExportOptions(settings).Consistent);
    }
}
