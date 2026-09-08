using System.Text.Json;
using SqlArchive.Core.Format;
using SqlSchemaDiff.Models;
using SqlSchemaDiff.Services;

namespace SqlArchive.Core.Tests;

/// <summary>
/// The manifest: what it says, that it survives a round trip, and that it can be read by
/// a build that is older than the file.
/// </summary>
public sealed class FormatManifestTests
{
    private static ArchiveManifest Sample() => new()
    {
        Tool = new ArchiveTool { Name = "SqlArchive", Version = "0.1.0" },
        CreatedAt = new DateTimeOffset(2026, 9, 7, 22, 30, 0, TimeSpan.Zero),
        Source = new ArchiveSource
        {
            Server = "SQL2022",
            Database = "Ventas",
            Edition = "Developer Edition (64-bit)",
            Collation = "SQL_Latin1_General_CP1_CI_AS"
        },
        Consistency = ArchiveConsistency.PerTable,
        Schema = new DatabaseSnapshot
        {
            DatabaseName = "Ventas",
            Objects =
            [
                new DbSchemaObject
                {
                    Type = DbObjectType.Table,
                    Schema = "dbo",
                    Name = "Customer",
                    Table = new TableModel
                    {
                        Schema = "dbo",
                        Name = "Customer",
                        Columns = [new ColumnModel { Name = "Id", TypeName = "int" }]
                    }
                },
                new DbSchemaObject { Type = DbObjectType.View, Schema = "dbo", Name = "V", Definition = "CREATE VIEW..." }
            ]
        },
        Tables =
        [
            new ArchiveTableEntry
            {
                Schema = "dbo",
                Name = "Customer",
                RowCount = 41203,
                RowHash = "9f2c",
                DataFiles = ["data/dbo.Customer.jsonl"],
                FileHashes = { ["data/dbo.Customer.jsonl"] = "sha256:abcd" },
                OmittedColumns = { ["Version"] = "rowversion" }
            }
        ],
        Files = { ["schema/040_tables.sql"] = "sha256:ef01", ["README.txt"] = "sha256:2345" }
    };

    [Fact]
    public void ItRoundTrips()
    {
        var manifest = ManifestSerializer.Deserialize(ManifestSerializer.Serialize(Sample()));

        Assert.Equal(ArchiveFormat.FormatName, manifest.Format);
        Assert.Equal(1, manifest.FormatVersion);
        Assert.Equal("Ventas", manifest.Source.Database);
        Assert.Equal(ArchiveConsistency.PerTable, manifest.Consistency);
        Assert.Equal(41203, manifest.Tables[0].RowCount);
        Assert.Equal("9f2c", manifest.Tables[0].RowHash);
        Assert.Equal("rowversion", manifest.Tables[0].OmittedColumns["Version"]);
        Assert.Equal("sha256:ef01", manifest.Files["schema/040_tables.sql"]);
        Assert.Equal(2, manifest.Schema!.Objects.Count);
        Assert.Equal(DbObjectType.View, manifest.Schema.Objects[1].Type);
    }

    [Fact]
    public void PropertyNamesAreTheOnesDesignSays()
    {
        using var document = JsonDocument.Parse(ManifestSerializer.Serialize(Sample()));
        var root = document.RootElement;

        foreach(var name in new[] { "format", "formatVersion", "tool", "createdAt", "source", "consistency", "schema", "tables", "files" })
            Assert.True(root.TryGetProperty(name, out _), $"the manifest has no '{name}'");

        var table = root.GetProperty("tables")[0];

        foreach(var name in new[] { "schema", "name", "rowCount", "rowHash", "dataFiles", "fileHashes", "rowFilter", "dataSkipped" })
            Assert.True(table.TryGetProperty(name, out _), $"the table entry has no '{name}'");
    }

    /// <summary>
    /// A null tells a reader the field exists and this table was not filtered. Leaving it
    /// out leaves them working out whether the export had no filter or the writer had no
    /// such concept.
    /// </summary>
    [Fact]
    public void ANullRowFilterIsWrittenRatherThanOmitted()
    {
        using var document = JsonDocument.Parse(ManifestSerializer.Serialize(Sample()));

        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("tables")[0].GetProperty("rowFilter").ValueKind);
    }

    /// <summary>
    /// Dictionary keys are entry names and column names. Camel-casing them would rename
    /// the files the manifest points at.
    /// </summary>
    [Fact]
    public void DictionaryKeysAreNotRenamed()
    {
        var json = ManifestSerializer.Serialize(Sample());

        Assert.Contains("\"README.txt\"", json, StringComparison.Ordinal);
        Assert.Contains("\"Version\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"rEADME.txt\"", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ArchiveConsistency.PerTable, "per-table")]
    [InlineData(ArchiveConsistency.Snapshot, "snapshot")]
    [InlineData(ArchiveConsistency.SnapshotIsolation, "snapshot-isolation")]
    public void ConsistencyIsWrittenWithTheNameDesignUses(ArchiveConsistency value, string expected)
    {
        var manifest = Sample();
        manifest.Consistency = value;

        Assert.Contains($"\"consistency\": \"{expected}\"", ManifestSerializer.Serialize(manifest), StringComparison.Ordinal);
        Assert.Equal(value, ManifestSerializer.Deserialize(ManifestSerializer.Serialize(manifest)).Consistency);
    }

    /// <summary>
    /// The snapshot has to be readable by the package that owns it, or the whole reason
    /// for embedding one - that one side of the diff comes out of the file - falls apart.
    /// </summary>
    [Fact]
    public void TheEmbeddedSnapshotIsStillOneSqlSchemaDiffCanRead()
    {
        using var document = JsonDocument.Parse(ManifestSerializer.Serialize(Sample()));
        var snapshot = SnapshotSerializer.Deserialize(document.RootElement.GetProperty("schema").GetRawText());

        Assert.Equal("Ventas", snapshot.DatabaseName);
        Assert.Equal(DbObjectType.Table, snapshot.Objects[0].Type);
        Assert.Equal("Id", snapshot.Objects[0].Table!.Columns[0].Name);
    }

    /// <summary>
    /// An archive from a version that does not exist yet still opens far enough to say
    /// what it is. Refusing is the job of whatever tries to restore from it.
    /// </summary>
    [Fact]
    public void AFileFromTheFutureStillOpens()
    {
        var manifest = Sample();
        manifest.FormatVersion = 3;

        var read = ManifestSerializer.Deserialize(ManifestSerializer.Serialize(manifest));

        Assert.Equal(3, read.FormatVersion);
        Assert.True(read.RequiresNewerReader);
        Assert.Equal("Ventas", read.Source.Database);
    }

    [Fact]
    public void PropertiesThisBuildDoesNotKnowAreKept()
    {
        const string json = """
            {
              "format": "sqlarchive",
              "formatVersion": 2,
              "tool": { "name": "SqlArchive", "version": "9.9.9" },
              "createdAt": "2026-09-07T22:30:00+00:00",
              "source": { "server": "s", "database": "d" },
              "consistency": "per-table",
              "encryption": { "algorithm": "aes-256-gcm" },
              "schema": null,
              "tables": [ { "schema": "dbo", "name": "T", "rowCount": 1, "maskedColumns": ["Email"] } ],
              "files": {}
            }
            """;

        var manifest = ManifestSerializer.Deserialize(json);

        Assert.True(manifest.RequiresNewerReader);
        Assert.NotNull(manifest.Extensions);
        Assert.Equal("aes-256-gcm", manifest.Extensions!["encryption"].GetProperty("algorithm").GetString());
        Assert.Equal("Email", manifest.Tables[0].Extensions!["maskedColumns"][0].GetString());

        // And they survive being written back out, so describing an archive does not
        // quietly strip what the describer did not understand.
        Assert.Contains("aes-256-gcm", ManifestSerializer.Serialize(manifest), StringComparison.Ordinal);
    }

    [Fact]
    public void AManifestOfSomeoneElsesFormatIsRefusedByName()
    {
        const string json = """{ "format": "bacpac", "formatVersion": 1 }""";

        var error = Assert.Throws<ArchiveFormatException>(() => ManifestSerializer.Deserialize(json));

        Assert.Contains("'bacpac'", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// dbdumper's manifest also declares formatVersion 1. Without something naming whose
    /// format it is, telling the two apart comes down to which properties happen to bind
    /// - so the failure has to say what the file actually is and what to do with it.
    /// </summary>
    [Fact]
    public void ADbDumperManifestIsRecognisedAndPointedAtItsReader()
    {
        const string json = """
            {
              "formatVersion": 1,
              "tool": "dbdumper 0.9.0",
              "createdAt": "2026-08-27T09:14:22Z",
              "source": { "server": "s", "database": "d" },
              "database": { "schemas": [], "tables": [] }
            }
            """;

        var error = Assert.Throws<ArchiveFormatException>(() => ManifestSerializer.Deserialize(json));

        Assert.Contains("dbdumper's", error.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(DbDumperManifestReader), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AVersionBelowOneIsRefused()
    {
        const string json = """{ "format": "sqlarchive", "formatVersion": 0 }""";

        Assert.Throws<ArchiveFormatException>(() => ManifestSerializer.Deserialize(json));
    }

    [Fact]
    public void RubbishIsRefusedWithTheParserSayingWhy()
    {
        var error = Assert.Throws<ArchiveFormatException>(() => ManifestSerializer.Deserialize("not json"));

        Assert.Contains("could not be parsed", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryEntryTheManifestClaimsIsFindableByName()
    {
        var manifest = Sample();

        Assert.Equal("sha256:abcd", manifest.HashOf("data/dbo.Customer.jsonl"));
        Assert.Equal("sha256:2345", manifest.HashOf("README.txt"));
        Assert.Null(manifest.HashOf("data/dbo.Missing.jsonl"));

        Assert.Equal(
            ["schema/040_tables.sql", "README.txt", "data/dbo.Customer.jsonl"],
            manifest.DeclaredEntries().ToArray());
    }

    [Fact]
    public void ATableIsFoundWithoutMindingTheCase() =>
        Assert.NotNull(Sample().Table("DBO", "customer"));
}
