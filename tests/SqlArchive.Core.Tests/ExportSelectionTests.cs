using SqlArchive.Core.Export;
using SqlSchemaDiff.Models;

namespace SqlArchive.Core.Tests;

/// <summary>
/// Which tables an export takes, and what the fingerprint refuses to resume.
/// </summary>
public sealed class ExportSelectionTests
{
    [Theory]
    [InlineData("Customer", "dbo", "Customer", true)]
    [InlineData("customer", "dbo", "Customer", true)]
    [InlineData("Cust*", "dbo", "Customer", true)]
    [InlineData("Cust?mer", "dbo", "Customer", true)]
    [InlineData("Cust?", "dbo", "Customer", false)]
    [InlineData("dbo.Customer", "dbo", "Customer", true)]
    [InlineData("sales.*", "sales", "Order", true)]
    [InlineData("sales.*", "dbo", "Order", false)]
    [InlineData("*.Order", "sales", "Order", true)]
    public void APatternMatchesTheWayItLooksLikeItShould(string pattern, string schema, string name, bool expected) =>
        Assert.Equal(expected, TableGlob.Matches(pattern, schema, name));

    /// <summary>
    /// A pattern with no dot is about the table's own name, so <c>Customer</c> finds
    /// <c>[dbo].[Customer]</c> without anyone having to know the schema - and does not
    /// accidentally match a schema called Customer either.
    /// </summary>
    [Fact]
    public void ABarePatternIsAboutTheTableName()
    {
        Assert.True(TableGlob.Matches("Order", "sales", "Order"));
        Assert.False(TableGlob.Matches("sales", "sales", "Order"));
    }

    /// <summary>The first matching filter wins, so a specific pattern put first beats the general one after it.</summary>
    [Fact]
    public void TheFirstMatchingRowFilterIsTheOneUsed()
    {
        List<KeyValuePair<string, string>> filters =
        [
            new("dbo.Order", "Status = 'open'"),
            new("*", "1 = 1")
        ];

        Assert.Equal("Status = 'open'", TableGlob.FirstMatch(filters, "dbo", "Order"));
        Assert.Equal("1 = 1", TableGlob.FirstMatch(filters, "dbo", "Customer"));
        Assert.Null(TableGlob.FirstMatch([], "dbo", "Customer"));
    }

    /// <summary>
    /// A table left out of the archive takes with it the foreign keys that point at it,
    /// because the foreign-key phase would not run otherwise and an archive whose schema
    /// will not execute is not an archive.
    /// </summary>
    [Fact]
    public void ExcludingATableDropsTheForeignKeysThatPointAtIt()
    {
        var snapshot = Snapshot(
            Table("dbo", "Customer"),
            WithForeignKey(Table("dbo", "Order"), "FK_Order_Customer", "dbo", "Customer"));

        var notices = new List<string>();
        var restricted = SnapshotSelection.Restrict(snapshot, (_, name) => name != "Customer", null, notices);

        var order = SnapshotSelection.Tables(restricted).Single();

        Assert.Equal("Order", order.Name);
        Assert.Empty(order.ForeignKeys);
        Assert.Contains(notices, n => n.Contains("FK_Order_Customer", StringComparison.Ordinal));

        // And the snapshot the caller handed in is untouched, because Clone is shallow and
        // editing the list in place would have reached back into it.
        Assert.Single(SnapshotSelection.Tables(snapshot).Single(t => t.Name == "Order").ForeignKeys);
    }

    /// <summary>A trigger cannot outlive its table, so it goes with it and the operator is told.</summary>
    [Fact]
    public void ExcludingATableDropsItsTriggers()
    {
        var snapshot = new DatabaseSnapshot
        {
            DatabaseName = "Ventas",
            Objects =
            [
                Table("dbo", "Order"),
                new DbSchemaObject
                {
                    Type = DbObjectType.Trigger,
                    Schema = "dbo",
                    Name = "TR_Order",
                    Trigger = new TriggerModel { ParentSchema = "dbo", ParentName = "Order" }
                }
            ]
        };

        var notices = new List<string>();
        var restricted = SnapshotSelection.Restrict(snapshot, (_, _) => false, null, notices);

        Assert.Empty(restricted.Objects);
        Assert.Contains(notices, n => n.Contains("TR_Order", StringComparison.Ordinal));
    }

    /// <summary>
    /// A system-versioned table and its history are one object to the server: the finalize
    /// phase names the history table when it turns versioning back on, so taking one
    /// without the other gives an archive whose schema stops half way through.
    /// </summary>
    [Fact]
    public void AHistoryTableComesAlongWithItsVersionedTable()
    {
        var versioned = Table("dbo", "Employee");
        versioned.Table!.TemporalType = "SYSTEM_VERSIONED_TEMPORAL_TABLE";
        versioned.Table.HistoryTableSchema = "history";
        versioned.Table.HistoryTableName = "EmployeeHistory";

        var snapshot = Snapshot(versioned, Table("history", "EmployeeHistory"));

        var notices = new List<string>();
        var restricted = SnapshotSelection.Restrict(snapshot, (schema, _) => schema == "dbo", null, notices);

        Assert.Equal(2, SnapshotSelection.Tables(restricted).Count());
        Assert.Contains(notices, n => n.Contains("EmployeeHistory", StringComparison.Ordinal));
    }

    /// <summary>
    /// The database name recorded is the one the rows came from, not the throwaway
    /// snapshot they were read through - the name is what a later diff matches on, and
    /// the snapshot will not exist in an hour.
    /// </summary>
    [Fact]
    public void TheRecordedDatabaseNameIsTheSourceAndNotTheSnapshotOfIt()
    {
        var snapshot = new DatabaseSnapshot { DatabaseName = "Ventas_sqlarchive_1a2b3c4d" };

        Assert.Equal("Ventas", SnapshotSelection.Restrict(snapshot, (_, _) => true, "Ventas", []).DatabaseName);
    }

    /// <summary>
    /// Resuming with a different filter has to be an error. Half a table filtered one way
    /// and half another is an archive nothing in the format can describe, and the manifest
    /// would report it as whole.
    /// </summary>
    [Fact]
    public void AFingerprintRefusesASpoolMadeWithOtherFilters()
    {
        var first = ExportFingerprint.Of(Options(filter: "Status = 'open'"), "0.1.0");
        var second = ExportFingerprint.Of(Options(filter: "Status = 'closed'"), "0.1.0");

        Assert.NotNull(first.Difference(second));
        Assert.Contains("filters", first.Difference(second)!, StringComparison.Ordinal);
        Assert.Null(first.Difference(ExportFingerprint.Of(Options(filter: "Status = 'open'"), "0.1.0")));
    }

    /// <summary>Pointing at a different database is a different export, and it says which two.</summary>
    [Fact]
    public void AFingerprintRefusesASpoolMadeAgainstAnotherDatabase()
    {
        var first = ExportFingerprint.Of(Options(database: "Ventas"), "0.1.0");
        var second = ExportFingerprint.Of(Options(database: "Compras"), "0.1.0");

        var difference = first.Difference(second);

        Assert.NotNull(difference);
        Assert.Contains("Ventas", difference, StringComparison.Ordinal);
        Assert.Contains("Compras", difference, StringComparison.Ordinal);
    }

    /// <summary>
    /// And a different build, which the work package did not ask for. The encoding belongs
    /// to the build, so two of them writing halves of one table would put two definitions
    /// of equality in one archive and the row hash would be right about neither.
    /// </summary>
    [Fact]
    public void AFingerprintRefusesASpoolMadeByAnotherBuild()
    {
        var difference = ExportFingerprint.Of(Options(), "0.1.0").Difference(ExportFingerprint.Of(Options(), "0.2.0"));

        Assert.NotNull(difference);
        Assert.Contains("0.2.0", difference, StringComparison.Ordinal);
    }

    /// <summary>A rotated password still points at the same database, so the spool is still this export's.</summary>
    [Fact]
    public void AFingerprintIsAboutTheDatabaseAndNotThePassword()
    {
        var first = new ExportOptions
        {
            ConnectionString = "Server=sql1;Database=Ventas;User ID=app;Password=one"
        };

        var second = new ExportOptions
        {
            ConnectionString = "Server=sql1;Database=Ventas;User ID=app;Password=two;Application Name=other"
        };

        Assert.Null(ExportFingerprint.Of(first, "0.1.0").Difference(ExportFingerprint.Of(second, "0.1.0")));
    }

    /// <summary>
    /// The working directory is deleted whole when a run is not resumable, and a caller
    /// can name it. A directory holding something an export did not put there is refused
    /// rather than emptied.
    /// </summary>
    [Fact]
    public void ADirectoryThatIsNotAnExportsIsNotDeleted()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"sqlarchive-guard-{Guid.NewGuid():N}");
        var precious = Path.Combine(directory, "somebody-elses-file.txt");

        Directory.CreateDirectory(directory);
        File.WriteAllText(precious, "not the export's");

        try
        {
            var refused = Assert.Throws<ExportException>(
                () => ExportSpool.Open(directory, ExportFingerprint.Of(Options(), "0.1.0"), resumable: false));

            Assert.Contains("would have deleted", refused.Message, StringComparison.Ordinal);
            Assert.True(File.Exists(precious));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static ExportOptions Options(string database = "Ventas", string? filter = null) => new()
    {
        ConnectionString = $"Server=sql1;Database={database};Integrated Security=true",
        RowFilters = filter is null ? [] : [new KeyValuePair<string, string>("*", filter)]
    };

    private static DatabaseSnapshot Snapshot(params DbSchemaObject[] objects) =>
        new() { DatabaseName = "Ventas", Objects = [.. objects] };

    private static DbSchemaObject Table(string schema, string name) => new()
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

    private static DbSchemaObject WithForeignKey(DbSchemaObject entry, string name, string schema, string table)
    {
        entry.Table!.ForeignKeys.Add(new ForeignKeyModel
        {
            Name = name,
            ReferencedSchema = schema,
            ReferencedTable = table,
            Columns = [new ForeignKeyColumnModel { ParentColumn = "Id", ReferencedColumn = "Id" }]
        });

        return entry;
    }
}
