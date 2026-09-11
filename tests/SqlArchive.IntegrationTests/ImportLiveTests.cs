using System.IO.Compression;
using System.Text;
using SqlArchive.Core.Export;
using SqlArchive.Core.Format;
using SqlArchive.Core.Import;
using SqlArchive.Core.Verify;

namespace SqlArchive.IntegrationTests;

/// <summary>
/// A restore of a real archive into a real database, checked against the server rather
/// than against the importer's own arithmetic.
/// <para>
/// The assertion that matters is one line and it is the whole tool: every table's
/// <see cref="LiveTableDigest"/> on the restored database is the row count and the row
/// hash the manifest declares. Both sides of that come from the encoder, which is the
/// point - <c>FORMAT.md</c> defines equality and this is the sentence that says the
/// definition held across an export, a file and a restore. Everything else here is either
/// a witness from outside our own code - the server's own <c>COUNT(*)</c>, a literal
/// somebody typed - or a fact about the destination that no hash could carry: that the
/// foreign key is back and trusted, that the identity counter carries on where the source
/// left off, that the view is there.
/// </para>
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class ImportLiveTests
{
    private readonly SqlServerFixture _server;

    public ImportLiveTests(SqlServerFixture server) => _server = server;

    /// <summary>
    /// Everything the work package asks for in one database: identity columns, a computed
    /// column, <c>money</c>, <c>datetime2</c>, <c>varbinary(max)</c>, an empty table, a
    /// foreign key and a view - plus the types whose encoding is most easily got wrong on
    /// the way back, which is what a restore is.
    /// </summary>
    private const string Schema = """
        CREATE TABLE dbo.Region (
            Id   int          NOT NULL CONSTRAINT PK_Region PRIMARY KEY,
            Name nvarchar(50) NOT NULL
        );

        CREATE TABLE dbo.Customer (
            Id       int             IDENTITY(1,1) NOT NULL CONSTRAINT PK_Customer PRIMARY KEY,
            Name     nvarchar(100)   NOT NULL,
            Balance  decimal(19,4)   NOT NULL CONSTRAINT DF_Customer_Balance DEFAULT (0),
            Huge     decimal(38,10)  NULL,
            Owed     money           NOT NULL,
            Created  datetime2(3)    NOT NULL,
            Day      date            NULL,
            At       time(7)         NULL,
            Zoned    datetimeoffset(7) NULL,
            Ref      uniqueidentifier NULL,
            Active   bit             NOT NULL,
            Ratio    float           NULL,
            Tiny     real            NULL,
            Photo    varbinary(max)  NULL,
            Notes    nvarchar(max)   NULL,
            -- Nullable and with a default, which is the pair that proves the bulk copy
            -- keeps nulls: without that, a null arrives as the default, and the default
            -- is a value that was never in the source.
            Apodo    nvarchar(50)    NULL CONSTRAINT DF_Customer_Apodo DEFAULT (N'sin apodo'),
            RegionId int             NULL CONSTRAINT FK_Customer_Region REFERENCES dbo.Region(Id),
            Shout    AS UPPER([Name])
        );

        CREATE TABLE sales.[Order] (
            Id         int   IDENTITY(1,1) NOT NULL CONSTRAINT PK_Order PRIMARY KEY,
            CustomerId int   NOT NULL CONSTRAINT FK_Order_Customer REFERENCES dbo.Customer(Id),
            Total      money NOT NULL,
            Placed     date  NOT NULL
        );

        CREATE TABLE dbo.Empty (
            Id int NOT NULL CONSTRAINT PK_Empty PRIMARY KEY
        );

        CREATE INDEX IX_Order_Placed ON sales.[Order] (Placed);
        """;

    private const string Rows = """
        INSERT INTO dbo.Region (Id, Name) VALUES (1, N'North'), (2, N'South'), (3, N'Este');

        INSERT INTO dbo.Customer (Name, Balance, Huge, Owed, Created, Day, At, Zoned, Ref, Active, Ratio, Tiny, Photo, Notes, Apodo, RegionId)
        SELECT TOP (50)
               CONCAT(N'Cliente Böhm ', ROW_NUMBER() OVER (ORDER BY (SELECT NULL))),
               ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) * 1.5,
               CASE WHEN ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) % 4 = 0
                    THEN NULL ELSE CONVERT(decimal(38,10), 1234567890123456789.0123456789) END,
               ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) * 2.25,
               DATEADD(millisecond, ROW_NUMBER() OVER (ORDER BY (SELECT NULL)), '2026-01-01T09:14:22.003'),
               DATEADD(day, ROW_NUMBER() OVER (ORDER BY (SELECT NULL)), '2026-01-01'),
               CASE WHEN ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) % 3 = 0
                    THEN NULL ELSE CONVERT(time(7), '10:00:00.1234567') END,
               CONVERT(datetimeoffset(7), '2026-01-15T10:00:00.1234567+02:00'),
               CASE WHEN ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) % 5 = 0
                    THEN NULL ELSE CONVERT(uniqueidentifier, '3F2504E0-4F89-11D3-9A0C-0305E82C3301') END,
               CASE WHEN ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) % 2 = 0 THEN 1 ELSE 0 END,
               CASE WHEN ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) % 7 = 0 THEN NULL ELSE 1.7976931348623157E+308 END,
               CASE WHEN ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) % 7 = 0 THEN NULL ELSE CONVERT(real, 0.1) END,
               CASE WHEN ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) % 6 = 0
                    THEN NULL ELSE CONVERT(varbinary(max), REPLICATE(CONVERT(varchar(max), 'ab'), 40000)) END,
               CASE WHEN ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) % 8 = 0 THEN NULL ELSE N'日本語 😀 <&>' END,
               CASE WHEN ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) % 2 = 0 THEN NULL ELSE N'apodo' END,
               1 + (ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) % 3)
        FROM sys.all_objects;

        INSERT INTO sales.[Order] (CustomerId, Total, Placed)
        SELECT TOP (200)
               1 + (ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) % 50),
               ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) * 3.25,
               DATEADD(day, ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) % 90, '2026-02-01')
        FROM sys.all_objects;
        """;

    // ------------------------------------------------------------------ round trip

    /// <summary>
    /// The whole tool in one test: a database out, a database in, and every table on the
    /// far side hashing to what the manifest says.
    /// </summary>
    [LiveFact]
    public async Task ADatabaseSurvivesTheRoundTripByItsOwnDefinitionOfEquality()
    {
        var (source, destination) = await _server.CreatePairAsync();
        await SeedAsync(source);

        var path = TempPath();

        try
        {
            await ExportAsync(source, path);

            var result = await ImportAsync(path, destination);

            Assert.True(result.Complete, Explain(result));
            Assert.Equal(4, result.Published);
            Assert.Equal(253, result.Rows);

            await AssertRestoredAsync(path, destination);

            // Witnesses from outside this tool's arithmetic: the server's own count, and
            // literals somebody typed while writing the seed.
            Assert.Equal(50, await SqlServerFixture.CountAsync(destination, "dbo.Customer"));
            Assert.Equal(200, await SqlServerFixture.CountAsync(destination, "sales.[Order]"));
            Assert.Equal(3, await SqlServerFixture.CountAsync(destination, "dbo.Region"));
            Assert.Equal(0, await SqlServerFixture.CountAsync(destination, "dbo.Empty"));

            // A computed column is not carried and is computed again on the far side, so
            // it is right without ever having been in the archive.
            Assert.Equal(
                "CLIENTE BÖHM 1",
                await ScalarStringAsync(destination, "SELECT Shout FROM dbo.Customer WHERE Id = 1;"));

            // A null in a column that has a default is still a null. Twenty-five of the
            // fifty rows, so a bulk copy that let the default in would be visible here
            // and, before that, in the row hash.
            Assert.Equal(
                25,
                Convert.ToInt32(await SqlServerFixture.ScalarAsync(
                    destination, "SELECT COUNT(*) FROM dbo.Customer WHERE Apodo IS NULL;")));

            // The view and the procedure came across, which is the modules phase.
            Assert.Equal(50, Convert.ToInt32(await SqlServerFixture.ScalarAsync(destination, "SELECT COUNT(*) FROM dbo.CustomerNames;")));
            Assert.NotNull(await SqlServerFixture.ScalarAsync(destination, "SELECT OBJECT_ID(N'dbo.TopCustomers');"));

            // The foreign keys are back, enabled and trusted. Trusted is the part that
            // matters: the restore switched them off to publish the tables, and a key
            // that came back enabled but untrusted would be one nothing had checked.
            Assert.Equal(
                0,
                Convert.ToInt32(await SqlServerFixture.ScalarAsync(
                    destination,
                    "SELECT COUNT(*) FROM sys.foreign_keys WHERE is_disabled = 1 OR is_not_trusted = 1;")));

            Assert.Equal(2, Convert.ToInt32(await SqlServerFixture.ScalarAsync(destination, "SELECT COUNT(*) FROM sys.foreign_keys;")));

            // The index of phase 050 is there, and it is not there twice.
            Assert.Equal(
                1,
                Convert.ToInt32(await SqlServerFixture.ScalarAsync(
                    destination, "SELECT COUNT(*) FROM sys.indexes WHERE name = N'IX_Order_Placed';")));

            // The identity carries on from the archived rows rather than colliding with
            // them, which is what a switch would otherwise leave behind: a switch resets
            // the destination's counter to its seed.
            Assert.Equal(
                51,
                Convert.ToInt32(await SqlServerFixture.ScalarAsync(
                    destination,
                    """
                    INSERT INTO dbo.Customer (Name, Balance, Owed, Created, Active) VALUES (N'nuevo', 0, 0, SYSUTCDATETIME(), 0);
                    SELECT CONVERT(int, SCOPE_IDENTITY());
                    """)));

            // And nothing was left behind on the destination.
            Assert.Equal(
                0,
                Convert.ToInt32(await SqlServerFixture.ScalarAsync(
                    destination, "SELECT COUNT(*) FROM sys.tables WHERE name LIKE N'%[_]sqlarchive[_]%';")));
        }
        finally
        {
            Clean(path);
        }
    }

    /// <summary>
    /// The two halves compose: <c>--schema-only</c> then <c>--data-only</c> ends where the
    /// default mode ends. And <c>--data-only</c> is the one that has to find the shape
    /// already there, which is the case that proves the shape check runs.
    /// </summary>
    [LiveFact]
    public async Task SchemaOnlyAndThenDataOnlyReachTheSamePlace()
    {
        var (source, destination) = await _server.CreatePairAsync();
        await SeedAsync(source);

        var path = TempPath();

        try
        {
            await ExportAsync(source, path);

            var schema = await ImportAsync(path, destination, mode: ImportMode.SchemaOnly);

            Assert.True(schema.SchemaBatches > 0);
            Assert.Equal(0, schema.Rows);
            Assert.Equal(0, await SqlServerFixture.CountAsync(destination, "dbo.Customer"));

            // The foreign keys are already there this time, so the data phase has to take
            // them off and put them back - which is the path the fresh restore never
            // exercises.
            var data = await ImportAsync(path, destination, mode: ImportMode.DataOnly);

            Assert.True(data.Complete, Explain(data));
            Assert.Equal(253, data.Rows);
            Assert.Contains(data.Notices, n => n.Contains("foreign key", StringComparison.Ordinal));

            await AssertRestoredAsync(path, destination);

            Assert.Equal(
                0,
                Convert.ToInt32(await SqlServerFixture.ScalarAsync(
                    destination,
                    "SELECT COUNT(*) FROM sys.foreign_keys WHERE is_disabled = 1 OR is_not_trusted = 1;")));
        }
        finally
        {
            Clean(path);
        }
    }

    /// <summary>
    /// <c>--data-only</c> into a destination whose shape is not the archive's is refused
    /// by name, before a row is read and before a staging table is created.
    /// </summary>
    [LiveFact]
    public async Task DataOnlyRefusesAShapeThatWillNotTakeTheRows()
    {
        var (source, destination) = await _server.CreatePairAsync();
        await SeedAsync(source);

        var path = TempPath();

        try
        {
            await ExportAsync(source, path);
            await ImportAsync(path, destination, mode: ImportMode.SchemaOnly);

            // A column the archive carries values for, turned into one SQL Server assigns
            // itself. Nothing about the row count or the hash would notice.
            await SqlServerFixture.ExecuteAsync(destination, "ALTER TABLE dbo.Region DROP COLUMN Name;");

            var error = await Assert.ThrowsAsync<ImportShapeException>(() =>
                ImportAsync(path, destination, mode: ImportMode.DataOnly));

            Assert.Contains("[dbo].[Region]", error.Message, StringComparison.Ordinal);
            Assert.Contains("Name", error.Message, StringComparison.Ordinal);

            // Refused before anything was created.
            Assert.Equal(
                0,
                Convert.ToInt32(await SqlServerFixture.ScalarAsync(
                    destination, "SELECT COUNT(*) FROM sys.tables WHERE name LIKE N'%[_]sqlarchive[_]%';")));
        }
        finally
        {
            Clean(path);
        }
    }

    // ------------------------------------------------------------------ migration

    /// <summary>
    /// Restoring over a destination that already holds an older version of the schema,
    /// with rows in it, is a migration: the table is altered rather than dropped, and the
    /// rows of a table this run is not publishing are still there afterwards.
    /// </summary>
    /// <remarks>
    /// The table left out of the publication is the point. Every table that <i>is</i>
    /// published ends up holding the archive's rows by definition, so it could not tell a
    /// migration from a drop-and-recreate; a table the diff had to alter and the data
    /// phase never touched can.
    /// </remarks>
    [LiveFact]
    public async Task AMigrationAltersTheDestinationAndKeepsTheRowsItIsNotPublishing()
    {
        var (source, destination) = await _server.CreatePairAsync();
        await SeedAsync(source);

        // An older shape of two of the archive's tables, with rows of its own, and one
        // table that is nobody's business but the destination's.
        await SqlServerFixture.ExecuteAsync(destination, "CREATE SCHEMA sales;");
        await SqlServerFixture.ExecuteAsync(
            destination,
            """
            CREATE TABLE dbo.Region (Id int NOT NULL CONSTRAINT PK_Region PRIMARY KEY, Name nvarchar(50) NOT NULL);
            INSERT INTO dbo.Region (Id, Name) VALUES (1, N'viejo norte'), (9, N'una region que el archivo no tiene');

            CREATE TABLE dbo.Empty (Id int NOT NULL CONSTRAINT PK_Empty PRIMARY KEY);
            INSERT INTO dbo.Empty (Id) VALUES (1), (2), (3), (4), (5), (6), (7);

            CREATE TABLE dbo.Local (Id int NOT NULL CONSTRAINT PK_Local PRIMARY KEY, Note nvarchar(50) NULL);
            INSERT INTO dbo.Local (Id, Note) VALUES (1, N'del destino');
            """);

        var path = TempPath();

        try
        {
            await ExportAsync(source, path);

            // dbo.Empty is excluded from the publication, so what happens to its seven
            // rows is decided by the diff alone.
            var result = await ImportAsync(path, destination, exclude: ["dbo.Empty"]);

            Assert.True(result.Complete, Explain(result));
            Assert.Contains(result.Notices, n => n.Contains("migration", StringComparison.Ordinal));

            // The destination's own table is untouched: a restore does not remove what it
            // was not asked about.
            Assert.Equal(1, await SqlServerFixture.CountAsync(destination, "dbo.Local"));

            // dbo.Empty was altered by the diff and never published, and its rows are
            // still there. The archive's copy of it has none, so this is the assertion
            // that a migration is not a drop.
            Assert.Equal(7, await SqlServerFixture.CountAsync(destination, "dbo.Empty"));

            // The published tables hold the archive's rows and hash to the archive's hash.
            await AssertRestoredAsync(path, destination, except: "dbo.Empty");

            Assert.Equal(3, await SqlServerFixture.CountAsync(destination, "dbo.Region"));
            Assert.Equal(
                "North",
                await ScalarStringAsync(destination, "SELECT Name FROM dbo.Region WHERE Id = 1;"));

            // The foreign keys of the tables the diff created are there, enabled and
            // trusted. Trusted is the part that matters: the fence took them off to
            // publish, and one that came back untrusted would be a key nothing had
            // checked against the rows that are now in the destination.
            Assert.Equal(2, Convert.ToInt32(await SqlServerFixture.ScalarAsync(destination, "SELECT COUNT(*) FROM sys.foreign_keys;")));
            Assert.Equal(
                0,
                Convert.ToInt32(await SqlServerFixture.ScalarAsync(
                    destination,
                    "SELECT COUNT(*) FROM sys.foreign_keys WHERE is_disabled = 1 OR is_not_trusted = 1;")));
        }
        finally
        {
            Clean(path);
        }
    }

    // ------------------------------------------------------------------ resume

    /// <summary>
    /// A restore killed halfway is picked up by the tables that are missing, and the ones
    /// that were already published are not published again - and, either way, not twice.
    /// </summary>
    [LiveFact]
    public async Task AnInterruptedRestoreResumesWithoutDuplicating()
    {
        var (source, destination) = await _server.CreatePairAsync();
        await SeedAsync(source);

        var path = TempPath();
        var work = path + ".work";

        try
        {
            await ExportAsync(source, path);

            await InterruptAsync(path, destination, work);

            var done = Directory.GetFiles(Path.Combine(work, "units")).Length;
            Assert.InRange(done, 1, 4);

            var resumed = await ImportAsync(path, destination, resumable: true, workDir: work, parallelism: 1);

            Assert.True(resumed.Complete, Explain(resumed));
            Assert.Contains(resumed.Notices, n => n.Contains("Resumed", StringComparison.Ordinal));

            // Something really was skipped, or this test is a second full restore wearing
            // a resume's clothes.
            Assert.Contains(resumed.Tables, t => t.Reason == "an earlier run published it");

            await AssertRestoredAsync(path, destination);

            Assert.Equal(50, await SqlServerFixture.CountAsync(destination, "dbo.Customer"));
            Assert.Equal(200, await SqlServerFixture.CountAsync(destination, "sales.[Order]"));
        }
        finally
        {
            Clean(path);

            if(Directory.Exists(work))
                Directory.Delete(work, recursive: true);
        }
    }

    /// <summary>Resuming a journal left by a restore of a different archive is refused rather than mixed.</summary>
    [LiveFact]
    public async Task ResumingWithADifferentArchiveIsRefused()
    {
        var (source, destination) = await _server.CreatePairAsync();
        await SeedAsync(source);

        var path = TempPath();
        var other = TempPath();
        var work = path + ".work";

        try
        {
            await ExportAsync(source, path);

            // Interrupted, because a restore that finished takes its journal away with
            // it - there is nothing left to resume from and nothing to refuse.
            await InterruptAsync(path, destination, work);

            // The same database again, with one row more: a different archive by content,
            // and one whose file name could easily have been the same.
            await SqlServerFixture.ExecuteAsync(source, "INSERT INTO dbo.Region (Id, Name) VALUES (4, N'Oeste');");
            await ExportAsync(source, other);

            var error = await Assert.ThrowsAsync<ImportResumeException>(() =>
                ImportAsync(other, destination, resumable: true, workDir: work));

            Assert.Contains("different archive", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Clean(path);
            Clean(other);

            if(Directory.Exists(work))
                Directory.Delete(work, recursive: true);
        }
    }

    // ------------------------------------------------------------------ the guard

    /// <summary>
    /// An archive whose manifest no longer describes its data is refused, and the
    /// destination holds exactly what it held before - to the row hash, not just the count.
    /// </summary>
    [LiveFact]
    public async Task AManifestThatDisagreesWithItsDataIsRefusedAndTheDestinationIsUntouched()
    {
        var (source, destination) = await _server.CreatePairAsync();
        await SeedAsync(source);

        var path = TempPath();

        try
        {
            await ExportAsync(source, path);

            var seeded = await ImportAsync(path, destination);

            Assert.True(seeded.Complete, Explain(seeded));

            var before = await DigestAsync(path, destination, "dbo", "Customer");

            Tamper(path, ArchiveFormat.ManifestEntry, text => text.Replace("\"rowCount\": 50", "\"rowCount\": 49", StringComparison.Ordinal));

            var result = await ImportAsync(path, destination, continueOnError: true);

            Assert.False(result.Complete);

            var customer = result.Tables.Single(t => t.Name == "Customer");

            Assert.True(
                customer.Outcome == ImportTableOutcome.Refused,
                $"expected a refusal and got {customer.Outcome}: {customer.Reason}");

            Assert.Contains("49", customer.Reason!, StringComparison.Ordinal);

            // Not "the same number of rows" - the same rows.
            var after = await DigestAsync(path, destination, "dbo", "Customer");

            Assert.Equal(before.Rows, after.Rows);
            Assert.Equal(before.RowHash, after.RowHash);
            Assert.Equal(50, await SqlServerFixture.CountAsync(destination, "dbo.Customer"));
        }
        finally
        {
            Clean(path);
        }
    }

    /// <summary>
    /// The case a row count cannot see: the right number of rows, and one of them is not
    /// the row that was archived.
    /// </summary>
    [LiveFact]
    public async Task ACorruptedDataFileIsRefusedAtTheSameRowCount()
    {
        var (source, destination) = await _server.CreatePairAsync();
        await SeedAsync(source);

        var path = TempPath();

        try
        {
            await ExportAsync(source, path);

            var seeded = await ImportAsync(path, destination);

            Assert.True(seeded.Complete, Explain(seeded));

            var before = await DigestAsync(path, destination, "dbo", "Region");

            // One character of one value. The line is still legal JSON, the file still
            // has three lines, and only the hash can tell.
            Tamper(path, "data/dbo.Region.jsonl", text => text.Replace("North", "Norte", StringComparison.Ordinal));

            var result = await ImportAsync(path, destination, continueOnError: true);

            Assert.False(result.Complete);

            var region = result.Tables.Single(t => t.Name == "Region");

            Assert.True(
                region.Outcome == ImportTableOutcome.Refused,
                $"expected a refusal and got {region.Outcome}: {region.Reason}");

            Assert.Contains("3 rows on both sides", region.Reason!, StringComparison.Ordinal);

            var after = await DigestAsync(path, destination, "dbo", "Region");

            Assert.Equal(before.RowHash, after.RowHash);
            Assert.Equal(
                "North",
                await ScalarStringAsync(destination, "SELECT Name FROM dbo.Region WHERE Id = 1;"));
        }
        finally
        {
            Clean(path);
        }
    }

    // ------------------------------------------------------------------ temporal

    /// <summary>
    /// A system-versioned table end to end, history and all: <c>040</c> creates the table
    /// without its period and the history as an ordinary table, the rows of both go in with
    /// their own periods, and <c>090</c> adds the period and turns versioning on, adopting
    /// the history as it stands.
    /// </summary>
    /// <remarks>
    /// <b>Until WP 2.6 this test pinned the loss</b>, and was called
    /// <c>ASystemVersionedTableIsRestoredWithItsCurrentRowsAndWithoutItsHistory</c>: the
    /// history table was not in the archive at all, the restored table came back with an
    /// empty one, and the assertions said so out loud so that the day it changed a test
    /// would say that too. This is that day. The history is a table of the archive now,
    /// and the two counts that used to be 2 and 0 are 2 and 2. What <c>AS OF</c> answers
    /// across the whole timeline is asserted in <c>TemporalLiveTests</c>.
    /// </remarks>
    [LiveFact]
    public async Task ASystemVersionedTableIsRestoredWithItsHistory()
    {
        var (source, destination) = await _server.CreatePairAsync();

        await SqlServerFixture.ExecuteAsync(
            source,
            """
            CREATE TABLE dbo.Precio (
                Id        int           NOT NULL CONSTRAINT PK_Precio PRIMARY KEY,
                Valor     decimal(19,4) NOT NULL,
                ValidFrom datetime2 GENERATED ALWAYS AS ROW START NOT NULL,
                ValidTo   datetime2 GENERATED ALWAYS AS ROW END   NOT NULL,
                PERIOD FOR SYSTEM_TIME (ValidFrom, ValidTo)
            ) WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = dbo.PrecioHistory));
            """);

        await SqlServerFixture.ExecuteAsync(source, "INSERT INTO dbo.Precio (Id, Valor) VALUES (1, 10.0), (2, 20.0), (3, 30.0);");
        await SqlServerFixture.ExecuteAsync(source, "UPDATE dbo.Precio SET Valor = Valor + 1 WHERE Id <= 2;");

        var path = TempPath();

        try
        {
            var exported = await new DatabaseExporter(new ExportOptions { ConnectionString = source }).ExportAsync(path);

            // The export says what it did with the history table - archived it - where it
            // used to say it was not coming.
            Assert.Contains(exported.Notices, n => n.Contains("[dbo].[PrecioHistory] is the history of [dbo].[Precio]", StringComparison.Ordinal));

            using(var archive = await ArchiveReader.OpenAsync(path))
            {
                Assert.Equal(
                    ["[dbo].[Precio]", "[dbo].[PrecioHistory]"],
                    archive.Manifest.Tables.Select(t => t.Identifier).ToArray());

                // The period columns are carried now, so the table leaves nothing out.
                Assert.Empty(archive.Manifest.Table("dbo", "Precio")!.OmittedColumns);
                Assert.Equal(2, archive.Manifest.Table("dbo", "PrecioHistory")!.RowCount);
            }

            var result = await ImportAsync(path, destination);

            Assert.True(result.Complete, Explain(result));

            // The table has no period while its rows load - 040 created it without one -
            // so the switch takes it, which it could not while the period was inline.
            Assert.Equal("swap", result.Tables.Single(t => t.Name == "Precio").Publication);

            await AssertRestoredAsync(path, destination);

            Assert.Equal(3, await SqlServerFixture.CountAsync(destination, "dbo.Precio"));
            Assert.Equal(
                11.0m,
                Convert.ToDecimal(await SqlServerFixture.ScalarAsync(destination, "SELECT Valor FROM dbo.Precio WHERE Id = 1;")));

            // Versioning is back on, pointed at the history table 040 created and the data
            // phase filled - adopted, not created afresh by the finalize phase's clause.
            Assert.Equal(
                2,
                Convert.ToInt32(await SqlServerFixture.ScalarAsync(
                    destination, "SELECT temporal_type FROM sys.tables WHERE name = N'Precio';")));

            Assert.Equal(
                "PrecioHistory",
                await ScalarStringAsync(
                    destination, "SELECT OBJECT_NAME(history_table_id) FROM sys.tables WHERE name = N'Precio';"));

            // What used to be the known limitation, stated the other way round: two history
            // rows on the source, and the same two on the restored copy.
            Assert.Equal(2, await SqlServerFixture.CountAsync(source, "dbo.PrecioHistory"));
            Assert.Equal(2, await SqlServerFixture.CountAsync(destination, "dbo.PrecioHistory"));

            // And the current rows kept the ValidFrom they had, rather than taking the
            // restore's instant - which is what made AS OF answer nothing before it.
            Assert.Equal(
                await ScalarStringAsync(source, "SELECT CONVERT(nvarchar(40), MAX(ValidFrom), 126) FROM dbo.Precio;"),
                await ScalarStringAsync(destination, "SELECT CONVERT(nvarchar(40), MAX(ValidFrom), 126) FROM dbo.Precio;"));
        }
        finally
        {
            Clean(path);
        }
    }

    // ------------------------------------------------------------------ dry run

    /// <summary>A dry run says what it would do and leaves the destination as empty as it found it.</summary>
    [LiveFact]
    public async Task ADryRunTouchesNothingAndStillReadsTheDestination()
    {
        var (source, destination) = await _server.CreatePairAsync();
        await SeedAsync(source);

        var path = TempPath();

        try
        {
            await ExportAsync(source, path);

            var result = await ImportAsync(path, destination, dryRun: true);

            Assert.True(result.DryRun);
            Assert.True(result.SchemaBatches > 0, "the statements it would run are counted, so a dry run says how much work this is");
            Assert.Equal(4, result.Tables.Count(t => t.Outcome == ImportTableOutcome.WouldPublish));
            Assert.Equal(0, result.Rows);

            Assert.Equal(0, Convert.ToInt32(await SqlServerFixture.ScalarAsync(destination, "SELECT COUNT(*) FROM sys.tables;")));
            Assert.False(Directory.Exists(path + ".restore"), "a dry run does not leave a journal behind either");

            // And again over a destination that already holds something, which is the
            // other route entirely: there the schema comes from a diff rather than from
            // the phases, and the diff is real - it is read off this table - while still
            // not being applied.
            await SqlServerFixture.ExecuteAsync(
                destination,
                "CREATE TABLE dbo.Region (Id int NOT NULL CONSTRAINT PK_Region PRIMARY KEY); INSERT INTO dbo.Region (Id) VALUES (1);");

            var migration = await ImportAsync(path, destination, dryRun: true);

            Assert.Contains(migration.Notices, n => n.Contains("migration", StringComparison.Ordinal));
            Assert.True(migration.SchemaBatches > 0, "the diff it would apply is counted");

            Assert.Equal(1, Convert.ToInt32(await SqlServerFixture.ScalarAsync(destination, "SELECT COUNT(*) FROM sys.tables;")));
            Assert.Equal(1, await SqlServerFixture.CountAsync(destination, "dbo.Region"));

            // The column the diff would have added is not there, because it was not applied.
            Assert.Equal(
                1,
                Convert.ToInt32(await SqlServerFixture.ScalarAsync(
                    destination, "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.Region');")));
        }
        finally
        {
            Clean(path);
        }
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// Every table in the manifest, read back off the restored database through the same
    /// encoder that wrote it, and compared with what the manifest declares.
    /// </summary>
    private static async Task AssertRestoredAsync(string archivePath, string destination, params string[] except)
    {
        using var archive = await ArchiveReader.OpenAsync(archivePath);
        var manifest = archive.Manifest;
        var snapshot = manifest.Schema!;

        var skip = except.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var checkedTables = 0;

        foreach(var entry in manifest.Tables)
        {
            if(skip.Contains($"{entry.Schema}.{entry.Name}"))
                continue;

            var model = SnapshotSelection.Tables(snapshot).Single(t =>
                string.Equals(t.Schema, entry.Schema, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(t.Name, entry.Name, StringComparison.OrdinalIgnoreCase));

            var digest = await LiveTableDigest.ComputeAsync(
                destination, entry.Schema, entry.Name, ArchiveColumns.For(model, snapshot.Types));

            Assert.Equal(entry.RowCount, digest.Rows);
            Assert.Equal(entry.RowHash, digest.RowHash);
            checkedTables++;
        }

        Assert.True(checkedTables > 0, "a comparison over no tables is not a comparison");
    }

    private static async Task<TableDigest> DigestAsync(string archivePath, string database, string schema, string name)
    {
        using var archive = await ArchiveReader.OpenAsync(archivePath);
        var snapshot = archive.Manifest.Schema!;

        var model = SnapshotSelection.Tables(snapshot).Single(t =>
            string.Equals(t.Schema, schema, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

        return await LiveTableDigest.ComputeAsync(
            database, schema, name, ArchiveColumns.For(model, snapshot.Types));
    }

    /// <summary>Rewrites one entry of a written archive, which is what a corrupted or edited file is.</summary>
    private static void Tamper(string archivePath, string entryName, Func<string, string> edit)
    {
        using var zip = ZipFile.Open(archivePath, ZipArchiveMode.Update);

        var entry = zip.GetEntry(entryName) ?? throw new InvalidOperationException($"no entry '{entryName}'");

        string text;

        using(var reader = new StreamReader(entry.Open(), new UTF8Encoding(false)))
            text = reader.ReadToEnd();

        var edited = edit(text);

        Assert.NotEqual(text, edited);

        entry.Delete();

        var replacement = zip.CreateEntry(entryName);
        using var writer = new StreamWriter(replacement.Open(), new UTF8Encoding(false));
        writer.Write(edited);
    }

    /// <summary>
    /// Starts a resumable restore and kills it once the first table is published, which
    /// is what leaves a journal to resume from.
    /// </summary>
    private static async Task InterruptAsync(string archivePath, string destination, string work)
    {
        using var stop = new CancellationTokenSource();
        var seen = 0;

        // Cancelled from inside the progress callback, after the first table has been
        // published and while there are tables left. One at a time, so the point at which
        // it stops is a point and not a race.
        var options = new ImportOptions
        {
            ConnectionString = destination,
            Resumable = true,
            WorkingDirectory = work,
            Parallelism = 1,
            Progress = new Callback(progress =>
            {
                if(progress.Phase == "data" && ++seen == 1)
                    stop.Cancel();
            })
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new DatabaseImporter(options).ImportAsync(archivePath, stop.Token));

        Assert.True(Directory.Exists(work), "a resumable restore that was cancelled has to leave its journal behind");
    }

    private static Task ExportAsync(string source, string path) =>
        new DatabaseExporter(new ExportOptions { ConnectionString = source }).ExportAsync(path);

    private static Task<ImportResult> ImportAsync(
        string archivePath,
        string destination,
        ImportMode mode = ImportMode.Migrate,
        bool dryRun = false,
        bool continueOnError = false,
        bool resumable = false,
        string? workDir = null,
        int parallelism = 4,
        string[]? exclude = null) =>
        new DatabaseImporter(new ImportOptions
        {
            ConnectionString = destination,
            Mode = mode,
            DryRun = dryRun,
            ContinueOnError = continueOnError,
            Resumable = resumable,
            WorkingDirectory = workDir,
            Parallelism = parallelism,
            ExcludeTables = exclude ?? []
        }).ImportAsync(archivePath);

    private async Task SeedAsync(string source)
    {
        await SqlServerFixture.ExecuteAsync(source, "CREATE SCHEMA sales;");
        await SqlServerFixture.ExecuteAsync(source, Schema);
        await SqlServerFixture.ExecuteAsync(source, "CREATE VIEW dbo.CustomerNames AS SELECT Id, Name FROM dbo.Customer;");
        await SqlServerFixture.ExecuteAsync(
            source, "CREATE PROCEDURE dbo.TopCustomers AS SELECT TOP (10) Id, Name FROM dbo.Customer ORDER BY Balance DESC;");
        await SqlServerFixture.ExecuteAsync(source, Rows);

        _ = _server;
    }

    private static async Task<string> ScalarStringAsync(string connectionString, string sql) =>
        (string)(await SqlServerFixture.ScalarAsync(connectionString, sql))!;

    /// <summary>Everything the restore said, for the assertion message that has to explain a red build.</summary>
    private static string Explain(ImportResult result) =>
        string.Join(
            Environment.NewLine,
            result.Tables
                .Where(t => t.Outcome is ImportTableOutcome.Refused or ImportTableOutcome.Failed)
                .Select(t => $"{t.Identifier}: {t.Reason}")
                .Concat(result.Notices));

    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"sqlarchive-import-{Guid.NewGuid():N}.sqlarchive");

    private static void Clean(string path)
    {
        try
        {
            if(File.Exists(path))
                File.Delete(path);

            if(Directory.Exists(path + ".restore"))
                Directory.Delete(path + ".restore", recursive: true);
        }
        catch(IOException)
        {
            // A leftover file in the temp directory is not worth failing a green test for.
        }
    }

    /// <summary>A progress sink that runs the callback where it is reported, so a test can act on it.</summary>
    private sealed class Callback : IProgress<ImportProgress>
    {
        private readonly Action<ImportProgress> _action;

        public Callback(Action<ImportProgress> action) => _action = action;

        public void Report(ImportProgress value) => _action(value);
    }
}
