using SqlArchive.Core.Format;
using SqlSchemaDiff.Models;

namespace SqlArchive.Core.Tests;

/// <summary>
/// Reading dbdumper's manifest as one side of a snapshot, so verify and
/// restore-as-migration work on an archive somebody else made.
/// <para>
/// The JSON below is dbdumper's shape as its <c>internal/model</c> package declares it,
/// with the property names it writes.
/// </para>
/// </summary>
public sealed class FormatDbDumperTests
{
    private const string Manifest = """
        {
          "formatVersion": 1,
          "tool": "dbdumper 0.9.0",
          "createdAt": "2026-08-27T09:14:22Z",
          "source": {
            "server": "SQL2019",
            "database": "Ventas",
            "serverVersion": "15.0.4335.1",
            "edition": "Standard Edition",
            "collation": "SQL_Latin1_General_CP1_CI_AS"
          },
          "database": {
            "collation": "SQL_Latin1_General_CP1_CI_AS",
            "schemas": [ { "name": "dbo" }, { "name": "sales", "owner": "dbo" } ],
            "userTypes": [
              { "schema": "dbo", "name": "Dinero", "isTableType": false, "baseType": "decimal", "maxLength": 9, "precision": 19, "scale": 4 },
              { "schema": "dbo", "name": "IdList", "isTableType": true,
                "columns": [ { "name": "Id", "ordinal": 1, "typeName": "int", "maxLength": 4 } ] }
            ],
            "sequences": [
              { "schema": "dbo", "name": "OrderNo", "dataType": "bigint", "startValue": "1", "increment": "1",
                "minValue": "1", "maxValue": "9223372036854775807", "isCached": true, "cacheSize": 50, "currentValue": "4711" }
            ],
            "tables": [
              {
                "schema": "sales",
                "name": "Order",
                "columns": [
                  { "name": "Id", "ordinal": 1, "typeName": "int", "maxLength": 4, "isIdentity": true, "identitySeed": "1", "identityIncrement": "1" },
                  { "name": "Total", "ordinal": 2, "typeSchema": "dbo", "typeName": "Dinero", "baseTypeName": "decimal", "maxLength": 9, "precision": 19, "scale": 4 },
                  { "name": "Notes", "ordinal": 3, "typeName": "nvarchar", "maxLength": -1, "isNullable": true, "collation": "SQL_Latin1_General_CP1_CI_AS" },
                  { "name": "Line", "ordinal": 4, "typeName": "int", "isComputed": true, "computedDefinition": "([Id]*(2))" }
                ],
                "primaryKey": { "name": "PK_Order", "type": "CLUSTERED", "isPrimaryKey": true, "isUnique": true,
                                "columns": [ { "name": "Id", "keyOrdinal": 1 } ] },
                "uniqueConstraints": [ { "name": "UQ_Order", "type": "NONCLUSTERED", "isUniqueConstraint": true, "isUnique": true,
                                         "columns": [ { "name": "Notes", "keyOrdinal": 1 } ] } ],
                "indexes": [ { "name": "IX_Order_Total", "type": "NONCLUSTERED", "fillFactor": 80,
                               "columns": [ { "name": "Total", "keyOrdinal": 1, "isDescending": true },
                                            { "name": "Notes", "isIncluded": true } ] } ],
                "foreignKeys": [ { "name": "FK_Order_Customer", "columns": ["Id"], "referencedSchema": "dbo",
                                   "referencedTable": "Customer", "referencedColumns": ["Id"], "onDelete": "CASCADE" } ],
                "checkConstraints": [ { "name": "CK_Order_Total", "definition": "([Total]>(0))", "isNotTrusted": true } ],
                "dataColumns": ["Id", "Total", "Notes"],
                "dataFiles": ["data/sales.Order.part000.jsonl", "data/sales.Order.part001.jsonl"],
                "rowCount": 4711,
                "rowFilter": "Total > 0"
              },
              {
                "schema": "dbo",
                "name": "Customer",
                "columns": [ { "name": "Id", "ordinal": 1, "typeName": "int", "maxLength": 4 } ],
                "dataFile": "data/dbo.Customer.jsonl",
                "rowCount": 12
              }
            ],
            "modules": [
              { "schema": "dbo", "name": "V", "kind": "view", "definition": "CREATE VIEW dbo.V AS SELECT 1 AS x",
                "ansiNulls": true, "quotedIdentifier": true },
              { "schema": "dbo", "name": "TR", "kind": "trigger", "definition": "CREATE TRIGGER ...",
                "parentSchema": "dbo", "parentName": "Customer", "isInsteadOfTrigger": true, "isDisabled": true,
                "ansiNulls": true, "quotedIdentifier": true }
            ]
          }
        }
        """;

    [Fact]
    public void ItParses()
    {
        var manifest = DbDumperManifestReader.Parse(Manifest);

        Assert.Equal(1, manifest.FormatVersion);
        Assert.Equal("dbdumper 0.9.0", manifest.Tool);
        Assert.Equal("Ventas", manifest.Source!.Database);
        Assert.Equal(2, manifest.Database!.Tables.Count);
    }

    [Fact]
    public void TablesBecomeSnapshotObjects()
    {
        var snapshot = DbDumperManifestReader.ToSnapshot(DbDumperManifestReader.Parse(Manifest));
        var order = snapshot.Objects.Single(o => o.Type == DbObjectType.Table && o.Name == "Order");

        Assert.Equal("sales", order.Schema);
        Assert.Equal(4, order.Table!.Columns.Count);
        Assert.True(order.Table.Columns[0].IsIdentity);
        Assert.True(order.Table.Columns[3].IsComputed);
        Assert.Equal(-1, order.Table.Columns[2].MaxLength);
    }

    /// <summary>
    /// dbdumper keeps the index behind a PRIMARY KEY or UNIQUE constraint apart from a
    /// standalone one, and so does SqlSchemaDiff, because the two are scripted
    /// differently. Putting all three in one list would produce a diff that wanted to
    /// drop a constraint and create an index in its place.
    /// </summary>
    [Fact]
    public void KeysAndIndexesLandInTheRightLists()
    {
        var snapshot = DbDumperManifestReader.ToSnapshot(DbDumperManifestReader.Parse(Manifest));
        var order = snapshot.Objects.Single(o => o.Name == "Order").Table!;

        Assert.Equal(["PK", "UQ"], order.KeyConstraints.Select(k => k.TypeCode).ToArray());
        Assert.Equal("PK_Order", order.KeyConstraints[0].Name);
        Assert.Equal("CLUSTERED", order.KeyConstraints[0].IndexTypeDesc);

        Assert.Single(order.Indexes);
        Assert.Equal("IX_Order_Total", order.Indexes[0].Name);
        Assert.Equal(80, order.Indexes[0].FillFactor);
        Assert.True(order.Indexes[0].Columns[0].IsDescending);
        Assert.True(order.Indexes[0].Columns[1].IsIncluded);
    }

    [Fact]
    public void ForeignKeyColumnsArePairedInOrder()
    {
        var snapshot = DbDumperManifestReader.ToSnapshot(DbDumperManifestReader.Parse(Manifest));
        var key = snapshot.Objects.Single(o => o.Name == "Order").Table!.ForeignKeys.Single();

        Assert.Equal("FK_Order_Customer", key.Name);
        Assert.Equal("CASCADE", key.DeleteActionDesc);
        Assert.Equal("NO_ACTION", key.UpdateActionDesc);
        Assert.Equal("Id", key.Columns[0].ParentColumn);
        Assert.Equal("Id", key.Columns[0].ReferencedColumn);
    }

    [Fact]
    public void CheckConstraintsKeepTheirNotTrustedFlag()
    {
        var snapshot = DbDumperManifestReader.ToSnapshot(DbDumperManifestReader.Parse(Manifest));
        var check = snapshot.Objects.Single(o => o.Name == "Order").Table!.CheckConstraints.Single();

        Assert.Equal("([Total]>(0))", check.Definition);
        Assert.True(check.IsNotTrusted);
    }

    [Fact]
    public void ModulesBecomeTheObjectTypesTheyAre()
    {
        var snapshot = DbDumperManifestReader.ToSnapshot(DbDumperManifestReader.Parse(Manifest));

        Assert.Equal(DbObjectType.View, snapshot.Objects.Single(o => o.Name == "V").Type);

        var trigger = snapshot.Objects.Single(o => o.Name == "TR");

        Assert.Equal(DbObjectType.Trigger, trigger.Type);
        Assert.Equal("Customer", trigger.Trigger!.ParentName);
        Assert.True(trigger.Trigger.IsInsteadOf);
        Assert.True(trigger.Trigger.IsDisabled);
    }

    [Fact]
    public void AliasTypesAndTableTypesGoToTheirOwnPlaces()
    {
        var snapshot = DbDumperManifestReader.ToSnapshot(DbDumperManifestReader.Parse(Manifest));

        var alias = Assert.Single(snapshot.Types);
        Assert.Equal("Dinero", alias.Name);
        Assert.Equal("decimal", alias.BaseTypeName);
        Assert.Equal(19, alias.Precision);

        var tableType = snapshot.Objects.Single(o => o.Type == DbObjectType.TableType);
        Assert.Equal("IdList", tableType.Name);
        Assert.Equal("Id", tableType.TableType!.Columns[0].Name);
    }

    [Fact]
    public void SequencesKeepTheirNumbersAsText()
    {
        var snapshot = DbDumperManifestReader.ToSnapshot(DbDumperManifestReader.Parse(Manifest));
        var sequence = snapshot.Objects.Single(o => o.Type == DbObjectType.Sequence).Sequence!;

        Assert.Equal("bigint", sequence.TypeName);
        Assert.Equal("9223372036854775807", sequence.MaxValue);
        Assert.Equal("4711", sequence.CurrentValue);
        Assert.Equal(50, sequence.CacheSize);
    }

    /// <summary>
    /// dbo is implicit and is not something a schema script has to create; every other
    /// schema is a prerequisite.
    /// </summary>
    [Fact]
    public void DboIsNotListedAsASchemaToCreate()
    {
        var snapshot = DbDumperManifestReader.ToSnapshot(DbDumperManifestReader.Parse(Manifest));

        Assert.Equal(["sales"], snapshot.Schemas);
        Assert.Equal("dbo", snapshot.SchemaOwners!["sales"]);
    }

    /// <summary>
    /// The important one. dbdumper's manifest carries no row hash, so verifying against
    /// one can compare the schema and the counts and nothing more - and saying so beats
    /// filling the field with something computed from the data files, which would make an
    /// unverifiable archive look verified.
    /// </summary>
    [Fact]
    public void ThereIsNoRowHashAndTheModelSaysSo()
    {
        var manifest = DbDumperManifestReader.ToArchiveManifest(DbDumperManifestReader.Parse(Manifest));

        Assert.All(manifest.Tables, t => Assert.Null(t.RowHash));
        Assert.Equal(4711, manifest.Table("sales", "Order")!.RowCount);
    }

    [Fact]
    public void BothWaysOfNamingDataFilesAreRead()
    {
        var manifest = DbDumperManifestReader.ToArchiveManifest(DbDumperManifestReader.Parse(Manifest));

        Assert.Equal(
            ["data/sales.Order.part000.jsonl", "data/sales.Order.part001.jsonl"],
            manifest.Table("sales", "Order")!.DataFiles.ToArray());

        Assert.Equal(["data/dbo.Customer.jsonl"], manifest.Table("dbo", "Customer")!.DataFiles.ToArray());
    }

    /// <summary>
    /// A partial archive that does not say it is partial makes verify report differences
    /// that are not differences, so the filter has to survive the crossing.
    /// </summary>
    [Fact]
    public void ARowFilterSurvives() =>
        Assert.Equal("Total > 0",
            DbDumperManifestReader.ToArchiveManifest(DbDumperManifestReader.Parse(Manifest)).Table("sales", "Order")!.RowFilter);

    [Fact]
    public void TheToolAndTheSourceAreCarriedOver()
    {
        var manifest = DbDumperManifestReader.ToArchiveManifest(DbDumperManifestReader.Parse(Manifest));

        Assert.Equal("dbdumper", manifest.Tool.Name);
        Assert.Equal("dbdumper 0.9.0", manifest.Tool.Version);
        Assert.Equal("15.0.4335.1", manifest.Source.ProductVersion);
        Assert.Equal(ArchiveConsistency.PerTable, manifest.Consistency);
    }

    [Fact]
    public void PropertiesDbDumperAddsLaterAreIgnoredRatherThanFatal()
    {
        const string json = """
            {
              "formatVersion": 1,
              "tool": "dbdumper 1.2.0",
              "somethingNew": { "a": 1 },
              "database": { "schemas": [], "tables": [ { "schema": "dbo", "name": "T", "columns": [], "rowCount": 0, "vectorColumns": [] } ] }
            }
            """;

        var manifest = DbDumperManifestReader.Parse(json);

        Assert.Single(manifest.Database!.Tables);
        Assert.True(manifest.Extensions!.ContainsKey("somethingNew"));
    }

    [Fact]
    public void AVersionNewerThanThisReaderIsRefused()
    {
        const string json = """{ "formatVersion": 2, "database": { "schemas": [], "tables": [] } }""";

        var error = Assert.Throws<ArchiveFormatException>(() => DbDumperManifestReader.Parse(json));

        Assert.Contains("format version 2", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SomethingThatIsNotADbDumperManifestIsRefused()
    {
        var error = Assert.Throws<ArchiveFormatException>(
            () => DbDumperManifestReader.Parse("""{ "formatVersion": 1 }"""));

        Assert.Contains("no 'database' object", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AModuleKindThisReaderDoesNotKnowIsRefusedByName()
    {
        const string json = """
            { "formatVersion": 1, "database": { "schemas": [], "tables": [],
              "modules": [ { "schema": "dbo", "name": "X", "kind": "aggregate", "definition": "" } ] } }
            """;

        var error = Assert.Throws<ArchiveFormatException>(
            () => DbDumperManifestReader.ToSnapshot(DbDumperManifestReader.Parse(json)));

        Assert.Contains("'aggregate'", error.Message, StringComparison.Ordinal);
    }
}
