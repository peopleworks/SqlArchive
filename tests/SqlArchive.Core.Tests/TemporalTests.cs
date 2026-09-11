using SqlArchive.Core.Format;
using SqlArchive.Core.Import;
using SqlSchemaDiff.Models;
using SqlSchemaDiff.Services;

namespace SqlArchive.Core.Tests;

/// <summary>
/// The decisions about system-versioned tables that need no server: which table is whose
/// history, what the migration's diff is shown, which finalize statement a migration must
/// not run twice, how versioning is put back, and what a refusal means.
/// <para>
/// What SQL Server actually does with a restored timeline is in
/// <c>TemporalLiveTests</c>. Nothing here pretends to know it.
/// </para>
/// </summary>
public sealed class TemporalTests
{
    // ---------------------------------------------------------------- the parent link

    /// <summary>
    /// The snapshot records the link on the parent, and that is where it is read. A history
    /// table in the parent's own schema is recorded with that schema.
    /// </summary>
    [Fact]
    public void AHistoryTableIsNamedByItsParent()
    {
        Assert.Equal(("history", "PrecioHistory"), HistoryLink.HistoryOf(Versioned("dbo", "Precio", "history", "PrecioHistory").Table!));
        Assert.Equal(("dbo", "PrecioHistory"), HistoryLink.HistoryOf(Versioned("dbo", "Precio", null, "PrecioHistory").Table!));
        Assert.Null(HistoryLink.HistoryOf(Plain("dbo", "Region").Table!));
    }

    [Fact]
    public void EachHistoryTableIsKeyedToItsParent()
    {
        var parents = HistoryLink.Parents(Snapshot(
            Versioned("dbo", "Precio", "history", "PrecioHistory"),
            Plain("history", "PrecioHistory"),
            Plain("dbo", "Region")));

        Assert.Equal(["history.PrecioHistory"], parents.Keys.ToArray());
        Assert.Equal("Precio", parents["HISTORY.precioHISTORY"].Name);
    }

    /// <summary>
    /// What a migration's diff is shown: the archive without its history tables, because the
    /// destination's extract never has one, and a diff that saw the difference would
    /// propose a CREATE that collides with the table SQL Server made from the clause.
    /// Nothing else is taken - not the parent, and not an ordinary table.
    /// </summary>
    [Fact]
    public void TheDiffIsShownTheArchiveWithoutItsHistoryTables()
    {
        var snapshot = Snapshot(
            Versioned("dbo", "Precio", "dbo", "PrecioHistory"),
            Plain("dbo", "PrecioHistory"),
            Plain("dbo", "Region"));

        var diffed = HistoryLink.Without(snapshot);

        Assert.Equal(["Precio", "Region"], diffed.Objects.Select(o => o.Name).ToArray());
        Assert.Equal(3, snapshot.Objects.Count);

        var ordinary = Snapshot(Plain("dbo", "Region"));
        Assert.Same(ordinary, HistoryLink.Without(ordinary));
    }

    /// <summary>
    /// The entries do not repeat the link, so the README says it - the one reader who
    /// cannot run <c>HistoryLink</c> is the person with the archive and no tool.
    /// </summary>
    [Fact]
    public void TheReadmeSaysWhoseHistoryATableIs()
    {
        var manifest = new ArchiveManifest
        {
            Schema = Snapshot(Versioned("dbo", "Precio", "dbo", "PrecioHistory"), Plain("dbo", "PrecioHistory")),
            Tables =
            [
                new ArchiveTableEntry { Schema = "dbo", Name = "Precio", RowCount = 3, RowHash = new string('0', 64) },
                new ArchiveTableEntry { Schema = "dbo", Name = "PrecioHistory", RowCount = 2, RowHash = new string('0', 64) }
            ]
        };

        var readme = ArchiveReadme.Compose(manifest);

        Assert.Contains("the history table of [dbo].[Precio]", readme, StringComparison.Ordinal);
        Assert.Equal(1, Occurrences(readme, "the history table of"));
    }

    // ---------------------------------------------------------------- the finalize filter

    /// <summary>
    /// The statement the filter looks for is the engine's own, as the exporter wrote it:
    /// built here by <c>SqlRender.BuildPeriodAdd</c> rather than typed, so a change to its
    /// shape turns this red instead of turning a migration's finalize into error 13597.
    /// </summary>
    [Fact]
    public void TheEnginesPeriodStatementIsRecognisedWithItsTable()
    {
        var table = WithPeriod("dbo", "Precio", hidden: true);
        var sql = SqlRender.BuildPeriodAdd(table)!;

        Assert.Contains("ADD HIDDEN", sql, StringComparison.Ordinal);
        Assert.Equal("[dbo].[Precio]", SchemaScript.PeriodAddedBy(sql));
    }

    [Fact]
    public void ABracketInTheNameIsPartOfTheName()
    {
        var sql = SqlRender.BuildPeriodAdd(WithPeriod("sa]les", "Pre]cio", hidden: false))!;

        Assert.Equal("[sa]]les].[Pre]]cio]", SchemaScript.PeriodAddedBy(sql));
    }

    /// <summary>The other statements of the finalize phase are idempotent and are left to run.</summary>
    [Fact]
    public void NothingElseInTheFinalizePhaseIsTakenForIt()
    {
        var versioned = WithPeriod("dbo", "Precio", hidden: false);
        versioned.TemporalType = "SYSTEM_VERSIONED_TEMPORAL_TABLE";
        versioned.HistoryTableSchema = "dbo";
        versioned.HistoryTableName = "PrecioHistory";

        Assert.Null(SchemaScript.PeriodAddedBy(SqlRender.BuildSystemVersioningOn(versioned)));
        Assert.Null(SchemaScript.PeriodAddedBy("ALTER SEQUENCE [dbo].[Ticket] RESTART WITH 4;"));
        Assert.Null(SchemaScript.PeriodAddedBy("-- Period for system time on [dbo].[Precio]"));
        Assert.Null(SchemaScript.PeriodAddedBy("ALTER TABLE [dbo].[Precio] DROP PERIOD FOR SYSTEM_TIME;"));
    }

    // ---------------------------------------------------------------- versioning put back

    /// <summary>
    /// Versioning goes back on naming the history it had and with the consistency check,
    /// which is the check that refuses a history that would not answer AS OF coherently.
    /// </summary>
    [Fact]
    public void VersioningGoesBackOnAsItWasFound()
    {
        Assert.Equal(
            "ALTER TABLE [dbo].[Precio] SET (SYSTEM_VERSIONING = ON (HISTORY_TABLE = [hist].[PrecioHistory], DATA_CONSISTENCY_CHECK = ON));",
            Temporal(retention: -1, unit: "INFINITE").VersioningOn);
    }

    /// <summary>
    /// A retention the destination had is stated again: a SET without it leaves INFINITE,
    /// and a restore that silently lifted somebody's retention policy would be a change
    /// nobody asked for.
    /// </summary>
    [Fact]
    public void AFiniteRetentionIsKept() =>
        Assert.EndsWith(
            "DATA_CONSISTENCY_CHECK = ON, HISTORY_RETENTION_PERIOD = 6 MONTH));",
            Temporal(retention: 6, unit: "MONTH").VersioningOn,
            StringComparison.Ordinal);

    /// <summary>A server that predates retention reports none, and none is stated.</summary>
    [Fact]
    public void NoRetentionIsStatedWhereTheServerHasNone() =>
        Assert.DoesNotContain(
            "RETENTION",
            Temporal(retention: null, unit: null).VersioningOn,
            StringComparison.Ordinal);

    // ---------------------------------------------------------------- refusals

    /// <summary>
    /// Each refusal SQL Server can make of a restored timeline says what it means here,
    /// and the two that are about a clock say that nothing in the archive is wrong.
    /// </summary>
    [Theory]
    [InlineData(13542, "clock")]
    [InlineData(13543, "clock")]
    [InlineData(13573, "overlap")]
    [InlineData(13575, "largest value")]
    [InlineData(13525, "type")]
    [InlineData(12331, "memory-optimized")]
    public void ATemporalRefusalIsExplained(int number, string expected)
    {
        var why = TemporalRefusal.Explain(number);

        Assert.NotNull(why);
        Assert.Contains(expected, why, StringComparison.Ordinal);
    }

    [Fact]
    public void AnyOtherErrorIsLeftToSpeakForItself()
    {
        Assert.Null(TemporalRefusal.Explain(547));
        Assert.Null(TemporalRefusal.Explain([547, 2627]));
        Assert.NotNull(TemporalRefusal.Explain([547, 13542]));
    }

    // ---------------------------------------------------------------- helpers

    private static DestinationTemporalTable Temporal(int? retention, string? unit) =>
        new("dbo", "Precio", "ValidFrom", false, "ValidTo", false, true, "hist", "PrecioHistory", retention, unit);

    private static TableModel WithPeriod(string schema, string name, bool hidden) => new()
    {
        Schema = schema,
        Name = name,
        PeriodStartColumn = "ValidFrom",
        PeriodEndColumn = "ValidTo",
        Columns =
        [
            new ColumnModel { Name = "Id", TypeName = "int" },
            new ColumnModel { Name = "ValidFrom", TypeName = "datetime2", Scale = 7, GeneratedAlwaysType = 1, IsHidden = hidden },
            new ColumnModel { Name = "ValidTo", TypeName = "datetime2", Scale = 7, GeneratedAlwaysType = 2, IsHidden = hidden }
        ]
    };

    private static DbSchemaObject Versioned(string schema, string name, string? historySchema, string historyName)
    {
        var entry = Plain(schema, name);

        entry.Table!.TemporalType = "SYSTEM_VERSIONED_TEMPORAL_TABLE";
        entry.Table.HistoryTableSchema = historySchema;
        entry.Table.HistoryTableName = historyName;

        return entry;
    }

    private static DbSchemaObject Plain(string schema, string name) => new()
    {
        Type = DbObjectType.Table,
        Schema = schema,
        Name = name,
        Table = new TableModel
        {
            Schema = schema,
            Name = name,
            Columns = [new ColumnModel { Name = "Id", TypeName = "int" }]
        }
    };

    private static DatabaseSnapshot Snapshot(params DbSchemaObject[] objects) =>
        new() { DatabaseName = "Ventas", Objects = [.. objects] };

    private static int Occurrences(string text, string value)
    {
        var count = 0;

        for(var at = text.IndexOf(value, StringComparison.Ordinal); at >= 0; at = text.IndexOf(value, at + value.Length, StringComparison.Ordinal))
            count++;

        return count;
    }
}
