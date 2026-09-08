using Microsoft.Data.SqlClient;
using SqlArchive.Core.Export;
using SqlArchive.Core.Format;

namespace SqlArchive.IntegrationTests;

/// <summary>
/// An export of a real database, checked against the database rather than against itself.
/// <para>
/// The counts and the row hashes in a manifest are computed from the same read that wrote
/// the files, so they agree with the archive whatever the read did or did not fetch.
/// Every assertion here that matters therefore has the server on the other side of it:
/// <c>COUNT(*)</c>, or the actual list of keys, read by a query that knows nothing about
/// how the export chose to slice the table.
/// </para>
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class ExportLiveTests
{
    private readonly SqlServerFixture _server;

    public ExportLiveTests(SqlServerFixture server) => _server = server;

    /// <summary>
    /// Five tables, a foreign key, a view, a computed column and an empty table. Enough
    /// that the phases have something in each of them and the manifest has something to
    /// be wrong about.
    /// </summary>
    private const string Schema = """
        CREATE TABLE dbo.Region (
            Id   int          NOT NULL CONSTRAINT PK_Region PRIMARY KEY,
            Name nvarchar(50) NOT NULL
        );

        CREATE TABLE dbo.Customer (
            Id       int            IDENTITY(1,1) NOT NULL CONSTRAINT PK_Customer PRIMARY KEY,
            Name     nvarchar(100)  NOT NULL,
            Balance  decimal(19,4)  NOT NULL CONSTRAINT DF_Customer_Balance DEFAULT (0),
            Created  datetime2(3)   NOT NULL,
            RegionId int            NULL CONSTRAINT FK_Customer_Region REFERENCES dbo.Region(Id),
            Shout    AS UPPER([Name])
        );

        CREATE TABLE sales.[Order] (
            Id         int   IDENTITY(1,1) NOT NULL CONSTRAINT PK_Order PRIMARY KEY,
            CustomerId int   NOT NULL CONSTRAINT FK_Order_Customer REFERENCES dbo.Customer(Id),
            Total      money NOT NULL,
            Placed     date  NOT NULL
        );

        CREATE TABLE dbo.AuditLog (
            Id      bigint         IDENTITY(1,1) NOT NULL CONSTRAINT PK_AuditLog PRIMARY KEY,
            Message nvarchar(max)  NULL
        );

        CREATE TABLE dbo.Empty (
            Id int NOT NULL CONSTRAINT PK_Empty PRIMARY KEY
        );
        """;

    private const string Rows = """
        INSERT INTO dbo.Region (Id, Name) VALUES (1, N'North'), (2, N'South'), (3, N'Este');

        INSERT INTO dbo.Customer (Name, Balance, Created, RegionId)
        SELECT TOP (50)
               CONCAT(N'Customer ', ROW_NUMBER() OVER (ORDER BY (SELECT NULL))),
               ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) * 1.5,
               DATEADD(day, ROW_NUMBER() OVER (ORDER BY (SELECT NULL)), '2026-01-01'),
               1 + (ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) % 3)
        FROM sys.all_objects;

        INSERT INTO sales.[Order] (CustomerId, Total, Placed)
        SELECT TOP (200)
               1 + (ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) % 50),
               ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) * 3.25,
               DATEADD(day, ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) % 90, '2026-02-01')
        FROM sys.all_objects;

        INSERT INTO dbo.AuditLog (Message)
        SELECT TOP (30) CONCAT(N'entry ', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)))
        FROM sys.all_objects;
        """;

    /// <summary>
    /// The whole database, and every count in the manifest checked against the server's
    /// own <c>COUNT(*)</c> rather than against the export's arithmetic.
    /// </summary>
    [LiveFact]
    public async Task AWholeDatabaseComesOutWithTheCountsTheServerHas()
    {
        var source = await SeedAsync();
        var path = TempPath();

        try
        {
            var result = await new DatabaseExporter(new ExportOptions { ConnectionString = source })
                .ExportAsync(path);

            Assert.Equal(ArchiveConsistency.PerTable, result.Consistency);

            using var archive = await ArchiveReader.OpenAsync(path);
            var manifest = archive.Manifest;

            Assert.Equal(ArchiveFormat.FormatName, manifest.Format);
            Assert.Equal(ArchiveConsistency.PerTable, manifest.Consistency);
            Assert.Equal(5, manifest.Tables.Count);

            foreach(var table in manifest.Tables)
            {
                var counted = await SqlServerFixture.ScalarAsync(
                    source, $"SELECT COUNT_BIG(*) FROM [{table.Schema}].[{table.Name}];");

                Assert.Equal(Convert.ToInt64(counted), table.RowCount);
                Assert.NotNull(table.RowHash);
                Assert.NotEmpty(table.DataFiles);
            }

            Assert.Equal(283, result.Rows);

            // The source is the database, and the database's own collation rather than
            // the connection's - the archive has to say what it will need to reproduce.
            Assert.Equal(await ScalarStringAsync(source, "SELECT DB_NAME();"), manifest.Source.Database);
            Assert.Equal(
                await ScalarStringAsync(source, "SELECT CONVERT(nvarchar(128), DATABASEPROPERTYEX(DB_NAME(), 'Collation'));"),
                manifest.Source.Collation);

            // Eleven phases, always all of them, so a reader never has to work out
            // whether a missing file means an empty phase or a forgotten one.
            Assert.Equal(11, archive.SchemaEntries.Count);
            Assert.Equal(
                ["schema/010_schemas.sql", "schema/020_types.sql", "schema/030_sequences.sql", "schema/035_synonyms.sql",
                 "schema/040_tables.sql", "schema/050_indexes.sql", "schema/060_checks.sql", "schema/070_foreignkeys.sql",
                 "schema/080_modules.sql", "schema/085_triggers.sql", "schema/090_finalize.sql"],
                archive.SchemaEntries.ToArray());

            var tables = await archive.ReadTextAsync("schema/040_tables.sql");
            var keys = await archive.ReadTextAsync("schema/070_foreignkeys.sql");

            Assert.Contains("[Customer]", tables, StringComparison.Ordinal);
            Assert.Contains("FK_Order_Customer", keys, StringComparison.Ordinal);

            // Rows load into a table with no key to maintain, which is what
            // ConstraintsAfterData buys: the primary key is added in a later phase.
            Assert.DoesNotContain("PK_Customer", tables, StringComparison.Ordinal);
            Assert.Contains("PK_Customer", await archive.ReadTextAsync("schema/050_indexes.sql"), StringComparison.Ordinal);

            Assert.NotEmpty(await archive.ReadTextAsync(ArchiveFormat.ReadmeEntry));

            // The manifest covers every entry exactly once, and every hash is the hash of
            // what is actually in the file.
            var declared = manifest.DeclaredEntries().ToList();

            Assert.Equal(
                archive.Entries.Select(e => e.Name).Where(n => n != ArchiveFormat.ManifestEntry).OrderBy(n => n, StringComparer.Ordinal),
                declared.OrderBy(n => n, StringComparer.Ordinal));

            foreach(var name in declared)
                Assert.Equal(manifest.HashOf(name), await archive.ComputeEntryHashAsync(name));

            // A computed column is not carried and the manifest says which and why.
            Assert.Equal(
                new Dictionary<string, string> { ["Shout"] = "computed" },
                manifest.Table("dbo", "Customer")!.OmittedColumns);
        }
        finally
        {
            Clean(path);
        }
    }

    /// <summary>
    /// An empty table writes an empty file. "Zero rows" and "not exported" are different
    /// facts and the archive has to keep them apart; the second one is <c>dataSkipped</c>.
    /// </summary>
    [LiveFact]
    public async Task AnEmptyTableWritesAnEmptyFileRatherThanNoFile()
    {
        var source = await SeedAsync();
        var path = TempPath();

        try
        {
            await new DatabaseExporter(new ExportOptions { ConnectionString = source }).ExportAsync(path);

            using var archive = await ArchiveReader.OpenAsync(path);
            var table = archive.Manifest.Table("dbo", "Empty")!;

            Assert.Equal(0, table.RowCount);
            Assert.False(table.DataSkipped);
            Assert.Equal(["data/dbo.Empty.jsonl"], table.DataFiles);

            // An empty table hashes to sixty-four zeros, which is what the format says.
            Assert.Equal(new string('0', 64), table.RowHash);
            Assert.Equal(0, archive.Entries.Single(e => e.Name == "data/dbo.Empty.jsonl").Length);
        }
        finally
        {
            Clean(path);
        }
    }

    /// <summary>A <c>--where</c> is applied to the read and written into the manifest, because an archive that is partial has to say so.</summary>
    [LiveFact]
    public async Task ARowFilterIsAppliedAndRecorded()
    {
        var source = await SeedAsync();
        var path = TempPath();

        try
        {
            await new DatabaseExporter(new ExportOptions
            {
                ConnectionString = source,
                RowFilters = [new KeyValuePair<string, string>("sales.Order", "Total > 100")]
            }).ExportAsync(path);

            using var archive = await ArchiveReader.OpenAsync(path);

            var order = archive.Manifest.Table("sales", "Order")!;
            var expected = Convert.ToInt64(
                await SqlServerFixture.ScalarAsync(source, "SELECT COUNT_BIG(*) FROM sales.[Order] WHERE Total > 100;"));

            Assert.Equal("Total > 100", order.RowFilter);
            Assert.Equal(expected, order.RowCount);
            Assert.True(expected < 200, "the filter has to actually remove rows or this test proves nothing");

            // And only that table. A filter keyed to one glob does not leak into the rest.
            Assert.Null(archive.Manifest.Table("dbo", "Customer")!.RowFilter);
            Assert.Equal(50, archive.Manifest.Table("dbo", "Customer")!.RowCount);
        }
        finally
        {
            Clean(path);
        }
    }

    /// <summary>A table whose data is excluded keeps its schema, and says in the manifest that its rows were left out on purpose.</summary>
    [LiveFact]
    public async Task ATableExcludedFromDataKeepsItsSchema()
    {
        var source = await SeedAsync();
        var path = TempPath();

        try
        {
            await new DatabaseExporter(new ExportOptions
            {
                ConnectionString = source,
                ExcludeData = ["AuditLog"]
            }).ExportAsync(path);

            using var archive = await ArchiveReader.OpenAsync(path);
            var audit = archive.Manifest.Table("dbo", "AuditLog")!;

            Assert.True(audit.DataSkipped);
            Assert.Null(audit.RowHash);
            Assert.Empty(audit.DataFiles);
            Assert.DoesNotContain(archive.Entries, e => e.Name.Contains("AuditLog", StringComparison.Ordinal));

            // The schema is still there, which is the whole point of the flag.
            Assert.Contains("[AuditLog]", await archive.ReadTextAsync("schema/040_tables.sql"), StringComparison.Ordinal);
        }
        finally
        {
            Clean(path);
        }
    }

    /// <summary>
    /// A table left out by a glob is out of the schema too, and takes the foreign keys
    /// that point at it with it - otherwise the foreign-key phase would not run and the
    /// archive's schema would be one that cannot be executed.
    /// </summary>
    [LiveFact]
    public async Task ATableExcludedByAGlobIsNotInTheArchiveAtAll()
    {
        var source = await SeedAsync();
        var path = TempPath();

        try
        {
            var result = await new DatabaseExporter(new ExportOptions
            {
                ConnectionString = source,
                ExcludeTables = ["Region"]
            }).ExportAsync(path);

            using var archive = await ArchiveReader.OpenAsync(path);

            Assert.Null(archive.Manifest.Table("dbo", "Region"));
            Assert.Equal(4, archive.Manifest.Tables.Count);
            Assert.DoesNotContain("[Region]", await archive.ReadTextAsync("schema/040_tables.sql"), StringComparison.Ordinal);

            var keys = await archive.ReadTextAsync("schema/070_foreignkeys.sql");

            Assert.DoesNotContain("FK_Customer_Region", keys, StringComparison.Ordinal);
            Assert.Contains("FK_Order_Customer", keys, StringComparison.Ordinal);

            Assert.Contains(result.Notices, n => n.Contains("FK_Customer_Region", StringComparison.Ordinal));
        }
        finally
        {
            Clean(path);
        }
    }

    /// <summary>An include glob keeps a schema and nothing else.</summary>
    [LiveFact]
    public async Task AnIncludeGlobTakesOneSchema()
    {
        var source = await SeedAsync();
        var path = TempPath();

        try
        {
            await new DatabaseExporter(new ExportOptions
            {
                ConnectionString = source,
                IncludeTables = ["sales.*"]
            }).ExportAsync(path);

            using var archive = await ArchiveReader.OpenAsync(path);

            Assert.Single(archive.Manifest.Tables);
            Assert.Equal("Order", archive.Manifest.Tables[0].Name);
            Assert.Equal(200, archive.Manifest.Tables[0].RowCount);
        }
        finally
        {
            Clean(path);
        }
    }

    /// <summary>
    /// A table read in ranges holds exactly the rows the server holds - not the rows the
    /// export thinks it read.
    /// <para>
    /// This is the assertion the work package asks for and the reason it asks for it. A
    /// round trip would compare what this build wrote with what this build read and would
    /// pass with any pair of ranges that agreed with each other, including a pair with a
    /// hole between them. So the keys are pulled out of the archive and compared against
    /// <c>SELECT Id ... ORDER BY Id</c>, which knows nothing about the ranges: a dropped
    /// row, a duplicated row and a row in the wrong file all show up as a difference
    /// between two lists.
    /// </para>
    /// <para>
    /// The key is deliberately horrible. It runs from <c>bigint</c>'s smallest value to
    /// its largest with almost everything crowded into a few hundred small integers, so
    /// evenly spaced cuts put nearly every row in one range and leave others empty. That
    /// is the shape a boundary is allowed to get wrong.
    /// </para>
    /// </summary>
    [LiveFact]
    public async Task ATableReadInRangesHoldsExactlyTheRowsTheServerHas()
    {
        var source = await SeedWideAsync();
        var path = TempPath();

        try
        {
            // Ranges left at Auto, which is the default and what the CLI's --range-size
            // reaches: asking for ranges of fifty rows is asking for a table of six
            // hundred to be split, and it is.
            var result = await new DatabaseExporter(new ExportOptions
            {
                ConnectionString = source,
                RowsPerRange = 50,
                MaxRangesPerTable = 16
            }).ExportAsync(path);

            using var archive = await ArchiveReader.OpenAsync(path);
            var entry = archive.Manifest.Table("dbo", "Wide")!;

            Assert.True(entry.DataFiles.Count >= 8, $"the table has to be split for this to test anything; it was in {entry.DataFiles.Count}");
            Assert.Equal(entry.DataFiles.Count, result.Units);

            var counted = Convert.ToInt64(await SqlServerFixture.ScalarAsync(source, "SELECT COUNT_BIG(*) FROM dbo.Wide;"));

            Assert.Equal(counted, entry.RowCount);

            // And now the part a count cannot answer: the same rows, not merely as many.
            var fromServer = await IdsFromServerAsync(source);
            var fromArchive = await IdsFromArchiveAsync(archive, "Wide");

            fromArchive.Sort();

            Assert.Equal(fromServer.Count, fromArchive.Count);
            Assert.Equal(fromServer, fromArchive);

            // The extremes, which is where an interpolation over the whole 64-bit range
            // puts a boundary if it is going to put one anywhere silly.
            Assert.Contains(long.MinValue, fromArchive);
            Assert.Contains(long.MaxValue, fromArchive);
        }
        finally
        {
            Clean(path);
        }
    }

    /// <summary>
    /// The same table split and unsplit comes to the same hash, which is the property the
    /// hash was chosen for: the ranges are added up, so the order they were read in and
    /// where the cuts fell do not change the answer.
    /// </summary>
    [LiveFact]
    public async Task RangesAndOnePassGiveTheSameCountAndTheSameHash()
    {
        var source = await SeedWideAsync();
        var split = TempPath();
        var whole = TempPath();

        try
        {
            await new DatabaseExporter(new ExportOptions
            {
                ConnectionString = source,
                Ranges = ExportRanges.Always,
                RowsPerRange = 50
            }).ExportAsync(split);

            await new DatabaseExporter(new ExportOptions
            {
                ConnectionString = source,
                Ranges = ExportRanges.Off
            }).ExportAsync(whole);

            using var a = await ArchiveReader.OpenAsync(split);
            using var b = await ArchiveReader.OpenAsync(whole);

            var ranged = a.Manifest.Table("dbo", "Wide")!;
            var single = b.Manifest.Table("dbo", "Wide")!;

            Assert.True(ranged.DataFiles.Count > 1);
            Assert.Single(single.DataFiles);

            Assert.Equal(single.RowCount, ranged.RowCount);
            Assert.Equal(single.RowHash, ranged.RowHash);
        }
        finally
        {
            Clean(split);
            Clean(whole);
        }
    }

    /// <summary>
    /// A table with nothing to split by is read in one pass and says so, rather than
    /// being sliced by something whose order is not ours to compute.
    /// </summary>
    [LiveFact]
    public async Task ATableWithNoPartitionColumnIsReadInOnePassAndTheOperatorIsTold()
    {
        var source = await _server.CreateDatabaseAsync();

        await SqlServerFixture.ExecuteAsync(source,
            "CREATE TABLE dbo.Heap (Code nvarchar(20) NOT NULL, Payload nvarchar(50) NOT NULL);");

        await SqlServerFixture.ExecuteAsync(source, """
            INSERT INTO dbo.Heap (Code, Payload)
            SELECT TOP (300) CONCAT(N'c', ROW_NUMBER() OVER (ORDER BY (SELECT NULL))), N'x'
            FROM sys.all_objects;
            """);

        var path = TempPath();

        try
        {
            var result = await new DatabaseExporter(new ExportOptions
            {
                ConnectionString = source,
                Ranges = ExportRanges.Always,
                RowsPerRange = 20
            }).ExportAsync(path);

            using var archive = await ArchiveReader.OpenAsync(path);
            var entry = archive.Manifest.Table("dbo", "Heap")!;

            Assert.Single(entry.DataFiles);
            Assert.Equal("data/dbo.Heap.jsonl", entry.DataFiles[0]);
            Assert.Equal(300, entry.RowCount);
            Assert.Equal(300, result.Rows);
        }
        finally
        {
            Clean(path);
        }
    }

    /// <summary>
    /// A nullable key is not split by, and the rows whose key is null are all still there.
    /// <para>
    /// This is the failure the whole partition design is arranged around. Every comparison
    /// against NULL is unknown, so <c>Seq &gt;= a</c> and <c>Seq &lt; a</c> are both false
    /// for a null key: the row is in no range at all. It disappears from every range at
    /// once, the row count in the manifest is computed from those same ranges and agrees
    /// with them, and the archive looks complete. The only thing that catches it is a
    /// count from the server, which is what this asserts.
    /// </para>
    /// </summary>
    [LiveFact]
    public async Task ANullableKeyIsNotSplitByAndTheRowsWithNoKeyAreStillThere()
    {
        var source = await _server.CreateDatabaseAsync();

        await SqlServerFixture.ExecuteAsync(source, """
            CREATE TABLE dbo.Loose (Seq int NULL, Payload nvarchar(50) NOT NULL);
            CREATE INDEX IX_Loose_Seq ON dbo.Loose (Seq);
            """);

        await SqlServerFixture.ExecuteAsync(source, """
            WITH n AS (SELECT TOP (200) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_objects)
            INSERT INTO dbo.Loose SELECT i, CONCAT(N'keyed ', i) FROM n;

            WITH n AS (SELECT TOP (50) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_objects)
            INSERT INTO dbo.Loose SELECT NULL, CONCAT(N'unkeyed ', i) FROM n;
            """);

        var path = TempPath();

        try
        {
            var result = await new DatabaseExporter(new ExportOptions
            {
                ConnectionString = source,
                Ranges = ExportRanges.Always,
                RowsPerRange = 20
            }).ExportAsync(path);

            using var archive = await ArchiveReader.OpenAsync(path);
            var entry = archive.Manifest.Table("dbo", "Loose")!;

            // The only indexed column of the right type is nullable, so there is nothing
            // to split by and the table is read in one pass.
            Assert.Single(entry.DataFiles);

            var counted = Convert.ToInt64(await SqlServerFixture.ScalarAsync(source, "SELECT COUNT_BIG(*) FROM dbo.Loose;"));
            var unkeyed = Convert.ToInt64(await SqlServerFixture.ScalarAsync(source, "SELECT COUNT_BIG(*) FROM dbo.Loose WHERE Seq IS NULL;"));

            Assert.Equal(250, counted);
            Assert.Equal(50, unkeyed);
            Assert.Equal(counted, entry.RowCount);
            Assert.Equal(counted, result.Rows);
        }
        finally
        {
            Clean(path);
        }
    }

    /// <summary>
    /// The schema files in the archive run, in the order of their prefixes, against an
    /// empty database.
    /// <para>
    /// That they are executable by hand is the archival promise in <c>DESIGN.md</c> - the
    /// case the whole design is for is the one where this tool is gone and the archive is
    /// not. Nothing else in Phase 2 executes them until the import lands, so the phases
    /// would otherwise be composed against nothing until then.
    /// </para>
    /// </summary>
    [LiveFact]
    public async Task TheSchemaFilesRunAgainstAnEmptyDatabaseInTheOrderOfTheirPrefixes()
    {
        var source = await SeedAsync();
        var destination = await _server.CreateDatabaseAsync();
        var path = TempPath();

        try
        {
            await new DatabaseExporter(new ExportOptions { ConnectionString = source }).ExportAsync(path);

            using var archive = await ArchiveReader.OpenAsync(path);

            foreach(var phase in archive.SchemaEntries)
            {
                var sql = await archive.ReadTextAsync(phase);

                foreach(var batch in Batches(sql))
                    await SqlServerFixture.ExecuteAsync(destination, batch);
            }

            // Every table is there, and empty, because the phases carry no rows.
            foreach(var table in archive.Manifest.Tables)
                Assert.Equal(0, await SqlServerFixture.CountAsync(destination, $"[{table.Schema}].[{table.Name}]"));

            Assert.Equal(0, await SqlServerFixture.CountAsync(destination, "dbo.CustomerNames"));
            Assert.Equal(0, await SqlServerFixture.CountAsync(destination, "dbo.NamedRegions"));

            Assert.Equal(
                1,
                Convert.ToInt32(await SqlServerFixture.ScalarAsync(
                    destination, "SELECT COUNT(*) FROM sys.procedures WHERE name = 'TopCustomers';")));

            // The keys, indexes and foreign keys of the later phases really did land, so
            // the rows a restore loads have something to be checked against afterwards.
            Assert.Equal(
                1,
                Convert.ToInt32(await SqlServerFixture.ScalarAsync(
                    destination, "SELECT COUNT(*) FROM sys.foreign_keys WHERE name = 'FK_Order_Customer';")));

            Assert.Equal(
                1,
                Convert.ToInt32(await SqlServerFixture.ScalarAsync(
                    destination, "SELECT COUNT(*) FROM sys.key_constraints WHERE name = 'PK_Customer';")));
        }
        finally
        {
            Clean(path);
        }
    }

    /// <summary>
    /// Splits a phase on its own <c>GO</c> lines, which is what sqlcmd and SSMS do with
    /// it. <c>GO</c> is not a SQL statement, so a driver has to do the splitting itself.
    /// </summary>
    private static IEnumerable<string> Batches(string sql)
    {
        var batch = new List<string>();

        foreach(var line in sql.Split('\n'))
        {
            if(line.Trim().Equals("GO", StringComparison.OrdinalIgnoreCase))
            {
                var text = string.Join('\n', batch).Trim();

                if(text.Length > 0)
                    yield return text;

                batch.Clear();
                continue;
            }

            batch.Add(line);
        }

        var last = string.Join('\n', batch).Trim();

        if(last.Length > 0)
            yield return last;
    }

    /// <summary>Reads a table's keys straight out of the server, which is the side of the comparison the export did not compute.</summary>
    private static async Task<List<long>> IdsFromServerAsync(string connectionString)
    {
        var ids = new List<long>();

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("SELECT Id FROM dbo.Wide ORDER BY Id;", connection);
        await using var reader = await command.ExecuteReaderAsync();

        while(await reader.ReadAsync())
            ids.Add(reader.GetInt64(0));

        return ids;
    }

    /// <summary>Reads a table's keys back out of the archive, across however many entries hold them.</summary>
    private static async Task<List<long>> IdsFromArchiveAsync(ArchiveReader archive, string name)
    {
        var snapshot = archive.Manifest.Schema!;
        var table = SnapshotSelection.Tables(snapshot).Single(t => t.Name == name);
        var columns = ArchiveColumns.For(table, snapshot.Types);
        var entry = archive.Manifest.Table(table.Schema, name)!;

        var ids = new List<long>();
        var rows = archive.OpenTable(entry, columns);

        await using(rows)
        {
            while(await rows.ReadAsync())
                ids.Add(Convert.ToInt64(rows.Current[0]));

            Assert.True(rows.Matches(), rows.Difference());
        }

        return ids;
    }

    private async Task<string> SeedAsync()
    {
        var source = await _server.CreateDatabaseAsync();

        await SqlServerFixture.ExecuteAsync(source, "CREATE SCHEMA sales;");
        await SqlServerFixture.ExecuteAsync(source, Schema);
        // Three modules, so the modules phase has more than one CREATE in it. Each of
        // them has to be the first statement of its own batch, which is what the GO
        // separators in a phase file are for; one view on another also gives the
        // composer's dependency ordering something to get wrong.
        await SqlServerFixture.ExecuteAsync(source, "CREATE VIEW dbo.CustomerNames AS SELECT Id, Name FROM dbo.Customer;");
        await SqlServerFixture.ExecuteAsync(source, "CREATE VIEW dbo.NamedRegions AS SELECT Id, Name FROM dbo.CustomerNames WHERE Id > 0;");
        await SqlServerFixture.ExecuteAsync(source,
            "CREATE PROCEDURE dbo.TopCustomers AS SELECT TOP (10) Id, Name FROM dbo.Customer ORDER BY Balance DESC;");
        await SqlServerFixture.ExecuteAsync(source, Rows);

        return source;
    }

    /// <summary>
    /// One table with a <c>bigint</c> key spread as badly as a key can be: the two ends of
    /// the type, a few hundred small values, a cluster a million up, and a block of large
    /// negatives.
    /// </summary>
    private async Task<string> SeedWideAsync()
    {
        var source = await _server.CreateDatabaseAsync();

        await SqlServerFixture.ExecuteAsync(source, """
            CREATE TABLE dbo.Wide (
                Id      bigint       NOT NULL CONSTRAINT PK_Wide PRIMARY KEY CLUSTERED,
                Payload nvarchar(40) NOT NULL
            );
            """);

        await SqlServerFixture.ExecuteAsync(source, """
            WITH n AS (SELECT TOP (500) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_objects)
            INSERT INTO dbo.Wide (Id, Payload) SELECT i, CONCAT(N'low ', i) FROM n;

            WITH n AS (SELECT TOP (120) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_objects)
            INSERT INTO dbo.Wide (Id, Payload) SELECT 1000000 + i, CONCAT(N'mid ', i) FROM n;

            WITH n AS (SELECT TOP (60) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_objects)
            INSERT INTO dbo.Wide (Id, Payload) SELECT -9000000000 - i, CONCAT(N'neg ', i) FROM n;

            INSERT INTO dbo.Wide (Id, Payload) VALUES
                (-9223372036854775808, N'the smallest bigint there is'),
                (9223372036854775807, N'the largest bigint there is'),
                (0, N'zero'),
                (4000000000, N'past the end of an int');
            """);

        return source;
    }

    private static async Task<string?> ScalarStringAsync(string connectionString, string sql) =>
        await SqlServerFixture.ScalarAsync(connectionString, sql) as string;

    internal static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"sqlarchive-export-{Guid.NewGuid():N}{ArchiveFormat.FileExtension}");

    internal static void Clean(string path)
    {
        if(File.Exists(path))
            File.Delete(path);

        if(Directory.Exists(path + ".work"))
            Directory.Delete(path + ".work", recursive: true);
    }
}
