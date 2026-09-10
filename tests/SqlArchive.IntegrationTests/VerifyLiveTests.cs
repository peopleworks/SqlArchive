using Microsoft.Data.SqlClient;
using SqlArchive.Core.Export;
using SqlArchive.Core.Format;
using SqlArchive.Core.Verify;

namespace SqlArchive.IntegrationTests;

/// <summary>
/// Verify against a real server, where the only assertion that decides whether the
/// feature works is the first one: <b>a clean round trip verifies clean</b>.
/// <para>
/// Everything else here is a difference deliberately introduced and then found again. A
/// verify that finds them all and also cries drift over a database that came out of the
/// archive unaltered is worse than useless: nobody reads the output of a check that is
/// always red.
/// </para>
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class VerifyLiveTests
{
    private readonly SqlServerFixture _server;

    public VerifyLiveTests(SqlServerFixture server) => _server = server;

    /// <summary>
    /// Everything the work package names: identity columns, a computed column,
    /// <c>money</c>, <c>datetime2</c>, <c>varbinary(max)</c>, an empty table, a foreign
    /// key and a view. Plus a decimal, a date, a uniqueidentifier and a nullable
    /// everything, because the encoding table is what equality means and a round trip
    /// that only exercises int and nvarchar proves nothing about it.
    /// </summary>
    private const string Schema = """
        CREATE TABLE dbo.Region (
            Id   int          NOT NULL CONSTRAINT PK_Region PRIMARY KEY,
            Name nvarchar(50) NOT NULL
        );

        CREATE TABLE dbo.Customer (
            Id       int              IDENTITY(1,1) NOT NULL CONSTRAINT PK_Customer PRIMARY KEY,
            Name     nvarchar(100)    NOT NULL,
            Balance  money            NOT NULL CONSTRAINT DF_Customer_Balance DEFAULT (0),
            Weight   decimal(19,4)    NULL,
            Created  datetime2(3)     NOT NULL,
            Photo    varbinary(max)   NULL,
            Ref      uniqueidentifier NOT NULL CONSTRAINT DF_Customer_Ref DEFAULT (NEWID()),
            RegionId int              NULL CONSTRAINT FK_Customer_Region REFERENCES dbo.Region(Id),
            Shout    AS UPPER([Name])
        );

        CREATE TABLE sales.[Order] (
            Id         int           IDENTITY(1,1) NOT NULL CONSTRAINT PK_Order PRIMARY KEY,
            CustomerId int           NOT NULL CONSTRAINT FK_Order_Customer REFERENCES dbo.Customer(Id),
            Total      money         NOT NULL,
            Placed     date          NOT NULL,
            Note       nvarchar(200) NULL
        );

        CREATE TABLE dbo.Empty (
            Id int NOT NULL CONSTRAINT PK_Empty PRIMARY KEY
        );

        CREATE INDEX IX_Order_Placed ON sales.[Order] (Placed);
        """;

    private const string Rows = """
        INSERT INTO dbo.Region (Id, Name) VALUES (1, N'North'), (2, N'South'), (3, N'Este');

        INSERT INTO dbo.Customer (Name, Balance, Weight, Created, Photo, RegionId)
        SELECT TOP (60)
               CONCAT(N'Customer ', ROW_NUMBER() OVER (ORDER BY (SELECT NULL))),
               ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) * 1.25,
               CASE WHEN ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) % 7 = 0
                    THEN NULL ELSE ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) * 0.5 END,
               DATEADD(minute, ROW_NUMBER() OVER (ORDER BY (SELECT NULL)), '2026-01-01T00:00:00'),
               CASE WHEN ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) % 5 = 0
                    THEN NULL ELSE CONVERT(varbinary(max), REPLICATE('ab', 300)) END,
               1 + (ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) % 3)
        FROM sys.all_objects;

        INSERT INTO sales.[Order] (CustomerId, Total, Placed, Note)
        SELECT TOP (150)
               1 + (ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) % 60),
               ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) * 3.25,
               DATEADD(day, ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) % 90, '2026-02-01'),
               CASE WHEN ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) % 4 = 0 THEN NULL ELSE N'Böhm & Co — 日本語' END
        FROM sys.all_objects;
        """;

    // ------------------------------------------------------------ the one that decides it

    /// <summary>
    /// The archive against the database it was taken from: no differences at all.
    /// </summary>
    [LiveFact]
    public async Task AnArchiveMatchesTheDatabaseItCameFrom()
    {
        var source = await SeedAsync();
        var path = TempPath();

        try
        {
            await ExportAsync(source, path);

            var report = await VerifyAsync(path, source);

            Assert.False(report.HasDifferences, Explain(report));
            Assert.True(report.Integrity.Matches);
            Assert.Equal(0, report.Integrity.Corrupt);
            Assert.NotNull(report.Schema);
            Assert.True(report.Schema!.Matches, Explain(report));
            Assert.Equal(4, report.Tables.Count);
            Assert.All(report.Tables, t => Assert.Equal(TableOutcome.Matches, t.Outcome));

            // Including the empty one, whose hash is the sum of nothing and whose count is
            // zero on both sides - not "unknown", which is what dataSkipped means.
            var empty = report.Tables.Single(t => t.Name == "Empty");
            Assert.Equal(0, empty.ArchiveRows);
            Assert.Equal(0, empty.DatabaseRows);
            Assert.True(empty.ContentCompared);
        }
        finally
        {
            Delete(path);
        }
    }

    /// <summary>
    /// The archive against a database built out of it: the schema from the phases and the
    /// rows copied in, which is what a restore does. <b>No differences at all.</b>
    /// <para>
    /// This is the assertion the feature stands or falls on. A restored database is never
    /// byte-for-byte the one the archive came from - SQL Server names constraints itself,
    /// hands out its own identity values, and re-normalises every expression it is given
    /// - and if any of that reaches the report, the report is noise.
    /// </para>
    /// </summary>
    [LiveFact]
    public async Task ARestoredDatabaseMatchesTheArchiveItWasBuiltFrom()
    {
        var source = await SeedAsync();
        var destination = await _server.CreateDatabaseAsync();
        var path = TempPath();

        try
        {
            await ExportAsync(source, path);
            await RestoreAsync(path, source, destination);

            var report = await VerifyAsync(path, destination);

            Assert.True(report.Integrity.Matches, Explain(report));
            Assert.NotNull(report.Schema);
            Assert.True(report.Schema!.Matches, Explain(report));
            Assert.All(report.Tables, t => Assert.Equal(TableOutcome.Matches, t.Outcome));
            Assert.False(report.HasDifferences, Explain(report));
        }
        finally
        {
            Delete(path);
        }
    }

    /// <summary>
    /// The same round trip over everything the server names for itself.
    /// <para>
    /// A constraint SQL Server named carries a per-database random suffix -
    /// <c>PK__Orders__3214EC07CF883821</c> - so a restore necessarily gets a different
    /// one, and a comparison that matched constraints by name would report every table
    /// in the database as changed. A sequence's current value moves every time it is
    /// used and is not drift either. These are the differences a clean restore cannot
    /// avoid, and this is the test that says the report does not raise them.
    /// </para>
    /// </summary>
    [LiveFact]
    public async Task ARestoredDatabaseMatchesEvenWhereTheServerNamedThingsItself()
    {
        var source = await _server.CreateDatabaseAsync();
        var destination = await _server.CreateDatabaseAsync();
        var path = TempPath();

        await SqlServerFixture.ExecuteAsync(source, "CREATE SCHEMA sales;");
        await SqlServerFixture.ExecuteAsync(source, "CREATE TYPE dbo.Dinero FROM decimal(19,4);");
        await SqlServerFixture.ExecuteAsync(source, "CREATE SEQUENCE dbo.Ticket AS int START WITH 1 INCREMENT BY 1;");
        await SqlServerFixture.ExecuteAsync(source, HardSchema);
        await SqlServerFixture.ExecuteAsync(
            source,
            """
            CREATE TRIGGER dbo.Invoice_Touch ON dbo.Invoice AFTER UPDATE AS
            BEGIN SET NOCOUNT ON; END;
            """);

        // A module whose text has to survive the round trip through sys.sql_modules,
        // over several lines, so that the line endings in the archive's schema files are
        // exercised rather than assumed.
        await SqlServerFixture.ExecuteAsync(
            source,
            """
            CREATE FUNCTION dbo.WithTax(@amount dbo.Dinero, @rate decimal(5,4))
            RETURNS dbo.Dinero
            AS
            BEGIN
                -- deliberately more than one line, and a comment in it
                DECLARE @total dbo.Dinero = @amount * (1 + @rate);
                RETURN @total;
            END;
            """);

        await SqlServerFixture.ExecuteAsync(
            source,
            """
            CREATE PROCEDURE sales.Unpaid
                @from date
            AS
            BEGIN
                SET NOCOUNT ON;

                SELECT i.Id, i.Number, dbo.WithTax(i.Amount, 0.1800) AS Total
                FROM dbo.Invoice AS i
                WHERE i.Paid = 0 AND CONVERT(date, i.Issued) >= @from;
            END;
            """);

        await SqlServerFixture.ExecuteAsync(source, "CREATE SYNONYM dbo.Bill FOR dbo.Invoice;");
        await SqlServerFixture.ExecuteAsync(source, HardRows);

        // The sequence has been used, so its current value is no longer its start value.
        await SqlServerFixture.ScalarAsync(source, "SELECT NEXT VALUE FOR dbo.Ticket;");
        await SqlServerFixture.ScalarAsync(source, "SELECT NEXT VALUE FOR dbo.Ticket;");

        try
        {
            await ExportAsync(source, path);
            await RestoreAsync(path, source, destination);

            // And used again on the source after the archive was taken, so the two
            // current values are certain to disagree.
            await SqlServerFixture.ScalarAsync(source, "SELECT NEXT VALUE FOR dbo.Ticket;");

            var report = await VerifyAsync(path, destination);

            Assert.True(report.Schema!.Matches, Explain(report));
            Assert.All(report.Tables, t => Assert.Equal(TableOutcome.Matches, t.Outcome));
            Assert.False(report.HasDifferences, Explain(report));

            // Not vacuous: three tables really were read on both sides, content and all,
            // and the sequence really was the thing that had to be left out.
            Assert.Equal(3, report.Tables.Count);
            Assert.All(report.Tables, t => Assert.True(t.ContentCompared, t.Identifier));
            Assert.Equal(2, report.Tables.Single(t => t.Name == "Ledger").DatabaseRows);
            Assert.Contains(report.Schema.Ignored, i => i.Contains("start value", StringComparison.Ordinal));
        }
        finally
        {
            Delete(path);
        }
    }

    /// <summary>
    /// A system-versioned table verifies, which is the whole reason its period columns
    /// are not archived: SQL Server writes them itself and refuses to be told what they
    /// are, so an archive that carried them could never pass its own verify.
    /// </summary>
    [LiveFact]
    public async Task ASystemVersionedTableVerifiesBecauseItsPeriodColumnsAreNotCarried()
    {
        var source = await _server.CreateDatabaseAsync();
        var path = TempPath();

        await SqlServerFixture.ExecuteAsync(
            source,
            """
            CREATE TABLE dbo.Employee (
                Id     int          NOT NULL PRIMARY KEY,
                Name   nvarchar(80) NOT NULL,
                From_  datetime2(7) GENERATED ALWAYS AS ROW START NOT NULL,
                To_    datetime2(7) GENERATED ALWAYS AS ROW END   NOT NULL,
                PERIOD FOR SYSTEM_TIME (From_, To_)
            ) WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = dbo.EmployeeHistory));
            """);

        await SqlServerFixture.ExecuteAsync(
            source, "INSERT INTO dbo.Employee (Id, Name) VALUES (1, N'Ana'), (2, N'Beto');");

        await SqlServerFixture.ExecuteAsync(source, "UPDATE dbo.Employee SET Name = N'Ana María' WHERE Id = 1;");

        try
        {
            await ExportAsync(source, path);

            var report = await VerifyAsync(path, source);

            Assert.False(report.HasDifferences, Explain(report));

            var employee = report.Tables.Single(t => t.Name == "Employee");

            Assert.Equal(TableOutcome.Matches, employee.Outcome);
            Assert.Equal(2, employee.DatabaseRows);
            Assert.True(employee.ContentCompared);

            // The history table is not here, and that is not this command's doing:
            // SqlSchemaDiff 1.7.0's extractor does not return a temporal history table as
            // an object at all, so the export never archived it and verify has nothing to
            // compare. Asserted rather than left implicit, so that the day the extractor
            // starts returning it, this test says so instead of a silent change of
            // meaning. Reported to the lead.
            Assert.DoesNotContain(report.Tables, t => t.Name.Contains("History", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Delete(path);
        }
    }

    /// <summary>
    /// Not one constraint here is named by hand, an alias type stands in for a decimal,
    /// and an index is filtered and carries an included column.
    /// </summary>
    private const string HardSchema = """
        CREATE TABLE dbo.Invoice (
            Id       bigint       IDENTITY(10,5) NOT NULL PRIMARY KEY,
            Number   nvarchar(20) NOT NULL UNIQUE,
            Amount   dbo.Dinero   NOT NULL DEFAULT (0),
            Paid     bit          NOT NULL DEFAULT (0),
            Issued   datetimeoffset(7) NOT NULL,
            Sealed   varbinary(64) NULL,
            CHECK (Amount >= 0),
            CONSTRAINT CK_Invoice_Number CHECK (LEN(Number) > 0)
        );

        CREATE TABLE sales.Line (
            Id        int    IDENTITY(1,1) NOT NULL PRIMARY KEY,
            InvoiceId bigint NOT NULL REFERENCES dbo.Invoice(Id),
            Qty       int    NOT NULL DEFAULT (1),
            Price     dbo.Dinero NOT NULL
        );

        CREATE INDEX IX_Line_Invoice ON sales.Line (InvoiceId) INCLUDE (Qty) WHERE Qty > 0;

        CREATE TABLE dbo.Ledger (
            Entry  nvarchar(max) NULL,
            Tongue nvarchar(60)  COLLATE Latin1_General_BIN2 NULL,
            Seen   datetime      NULL,
            Ratio  real          NULL,
            Wide   float         NULL,
            Where_ hierarchyid   NULL
        );
        """;

    private const string HardRows = """
        INSERT INTO dbo.Invoice (Number, Amount, Paid, Issued, Sealed)
        SELECT TOP (40)
               CONCAT(N'INV-', ROW_NUMBER() OVER (ORDER BY (SELECT NULL))),
               ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) * 1.0001,
               CASE WHEN ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) % 2 = 0 THEN 1 ELSE 0 END,
               TODATETIMEOFFSET(DATEADD(minute, ROW_NUMBER() OVER (ORDER BY (SELECT NULL)), '2026-04-01T00:00:00'), 120),
               CONVERT(varbinary(64), REPLICATE('f', 32))
        FROM sys.all_objects;

        INSERT INTO sales.Line (InvoiceId, Qty, Price)
        SELECT TOP (100) 10 + 5 * ((ROW_NUMBER() OVER (ORDER BY (SELECT NULL))) % 40),
               1 + (ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) % 4),
               ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) * 0.3333
        FROM sys.all_objects;

        INSERT INTO dbo.Ledger (Entry, Tongue, Seen, Ratio, Wide, Where_)
        VALUES (N'Böhm & Co — 日本語 😀', N'straße', '2026-05-01T10:00:00.003', 0.1, 0.1, hierarchyid::Parse('/1/2/')),
               (NULL, NULL, NULL, NULL, NULL, NULL);
        """;

    // ------------------------------------------------------------ differences, one at a time

    /// <summary>
    /// The case a row count cannot see, and the reason the hash is in the manifest.
    /// </summary>
    [LiveFact]
    public async Task AnUpdateIsAContentDifferenceOnThatTableAndNothingElse()
    {
        var source = await SeedAsync();
        var path = TempPath();

        try
        {
            await ExportAsync(source, path);

            await SqlServerFixture.ExecuteAsync(source, "UPDATE dbo.Customer SET Balance = Balance + 1 WHERE Id % 3 = 0;");

            var report = await VerifyAsync(path, source);

            Assert.True(report.HasDifferences);
            Assert.True(report.Integrity.Matches, Explain(report));
            Assert.True(report.Schema!.Matches, Explain(report));

            var customer = report.Tables.Single(t => t.Name == "Customer");

            Assert.Equal(TableOutcome.ContentDiffers, customer.Outcome);
            Assert.Equal(customer.ArchiveRows, customer.DatabaseRows);
            Assert.NotEqual(customer.ArchiveHash, customer.DatabaseHash);
            Assert.Contains("the content differs", string.Join(' ', customer.Differences), StringComparison.Ordinal);

            // And nothing else: one UPDATE moved one table.
            Assert.All(
                report.Tables.Where(t => t.Name != "Customer"),
                t => Assert.Equal(TableOutcome.Matches, t.Outcome));
        }
        finally
        {
            Delete(path);
        }
    }

    [LiveFact]
    public async Task ADeletedRowIsACountDifference()
    {
        var source = await SeedAsync();
        var path = TempPath();

        try
        {
            await ExportAsync(source, path);

            await SqlServerFixture.ExecuteAsync(source, "DELETE FROM sales.[Order] WHERE Id = 7;");

            var report = await VerifyAsync(path, source);

            var order = report.Tables.Single(t => t.Name == "Order");

            Assert.Equal(TableOutcome.RowCountDiffers, order.Outcome);
            Assert.Equal(order.ArchiveRows - 1, order.DatabaseRows);
            Assert.True(report.Schema!.Matches, Explain(report));
        }
        finally
        {
            Delete(path);
        }
    }

    [LiveFact]
    public async Task AnAddedColumnIsASchemaDifference()
    {
        var source = await SeedAsync();
        var path = TempPath();

        try
        {
            await ExportAsync(source, path);

            await SqlServerFixture.ExecuteAsync(source, "ALTER TABLE dbo.Region ADD Code nvarchar(10) NULL;");

            var report = await VerifyAsync(path, source);

            Assert.True(report.HasDifferences);
            Assert.Contains("[dbo].[Region]", report.Schema!.Differing);

            var region = report.Tables.Single(t => t.Name == "Region");

            Assert.Equal(TableOutcome.SchemaDiffers, region.Outcome);
            Assert.Contains(
                "the database has a column the archive does not: [Code] nvarchar(10).",
                region.Differences);

            // The rows the archive does carry are still the rows that are there: a column
            // added beside them changes the shape and not the content.
            Assert.Equal(region.ArchiveRows, region.DatabaseRows);
            Assert.Equal(region.ArchiveHash, region.DatabaseHash);
        }
        finally
        {
            Delete(path);
        }
    }

    /// <summary>
    /// A table the archive has rows for and the database has emptied. A difference, and
    /// not an error: an empty table reads fine and hashes to the sum of nothing.
    /// </summary>
    [LiveFact]
    public async Task ATableEmptiedInTheDatabaseIsADifferenceAndNotAnError()
    {
        var source = await SeedAsync();
        var path = TempPath();

        try
        {
            await ExportAsync(source, path);

            await SqlServerFixture.ExecuteAsync(source, "DELETE FROM sales.[Order];");

            var report = await VerifyAsync(path, source);

            var order = report.Tables.Single(t => t.Name == "Order");

            Assert.Equal(TableOutcome.RowCountDiffers, order.Outcome);
            Assert.Equal(0, order.DatabaseRows);
            Assert.Equal(150, order.ArchiveRows);
            Assert.True(order.ContentCompared);
        }
        finally
        {
            Delete(path);
        }
    }

    [LiveFact]
    public async Task ATableOnlyInTheDatabaseIsReportedAsExtra()
    {
        var source = await SeedAsync();
        var path = TempPath();

        try
        {
            await ExportAsync(source, path);

            await SqlServerFixture.ExecuteAsync(
                source, "CREATE TABLE dbo.Later (Id int NOT NULL CONSTRAINT PK_Later PRIMARY KEY);");

            var report = await VerifyAsync(path, source);

            var later = report.Tables.Single(t => t.Name == "Later");

            Assert.Equal(TableOutcome.OnlyInDatabase, later.Outcome);
            Assert.Contains("[dbo].[Later]", report.Schema!.OnlyInDatabase);
        }
        finally
        {
            Delete(path);
        }
    }

    [LiveFact]
    public async Task ATableMissingFromTheDatabaseIsReportedAsMissing()
    {
        var source = await SeedAsync();
        var path = TempPath();

        try
        {
            await ExportAsync(source, path);

            await SqlServerFixture.ExecuteAsync(source, "DROP TABLE dbo.Empty;");

            var report = await VerifyAsync(path, source);

            var empty = report.Tables.Single(t => t.Name == "Empty");

            Assert.Equal(TableOutcome.MissingFromDatabase, empty.Outcome);
            Assert.Contains("[dbo].[Empty]", report.Schema!.OnlyInArchive);
        }
        finally
        {
            Delete(path);
        }
    }

    // ------------------------------------------------------------ the partial archive

    /// <summary>
    /// A table archived with a <c>--where</c> is compared through that same filter, so
    /// the rows the archive deliberately does not hold are not reported as missing.
    /// </summary>
    [LiveFact]
    public async Task ARowFilterIsNotADifference()
    {
        var source = await SeedAsync();
        var path = TempPath();

        try
        {
            await new DatabaseExporter(new ExportOptions
            {
                ConnectionString = source,
                RowFilters = { new KeyValuePair<string, string>("sales.Order", "Id <= 50") }
            }).ExportAsync(path);

            var report = await VerifyAsync(path, source);

            var order = report.Tables.Single(t => t.Name == "Order");

            Assert.Equal(TableOutcome.Matches, order.Outcome);
            Assert.Equal(50, order.ArchiveRows);
            Assert.Equal(50, order.DatabaseRows);
            Assert.Equal("Id <= 50", order.RowFilter);
            Assert.Contains("were read through that filter", order.Limitation, StringComparison.Ordinal);
            Assert.False(report.HasDifferences, Explain(report));
        }
        finally
        {
            Delete(path);
        }
    }

    /// <summary>
    /// A table whose data was deliberately not archived cannot be verified, and saying so
    /// is a different answer from saying it differs.
    /// </summary>
    [LiveFact]
    public async Task ATableWithNoDataArchivedIsNotADifference()
    {
        var source = await SeedAsync();
        var path = TempPath();

        try
        {
            await new DatabaseExporter(new ExportOptions
            {
                ConnectionString = source,
                ExcludeData = { "sales.Order" }
            }).ExportAsync(path);

            var report = await VerifyAsync(path, source);

            var order = report.Tables.Single(t => t.Name == "Order");

            Assert.Equal(TableOutcome.NotVerifiable, order.Outcome);
            Assert.True(order.DataSkipped);
            Assert.Null(order.ArchiveRows);
            Assert.False(report.HasDifferences, Explain(report));
        }
        finally
        {
            Delete(path);
        }
    }

    /// <summary>
    /// A table the manifest names with no row hash beside it - which is every table of a
    /// dbdumper archive. The count is compared and the content is not, and the verdict
    /// says so rather than showing green over a question nobody asked.
    /// </summary>
    [LiveFact]
    public async Task AtableWithNoRowHashIsCheckedByCountAndSaysSo()
    {
        var source = await SeedAsync();
        var path = TempPath();

        try
        {
            await ExportAsync(source, path);
            await ForgetTheRowHashAsync(path, "Order");

            // The rows moved under it. Only the count can see that, and only because the
            // row went away rather than changing.
            await SqlServerFixture.ExecuteAsync(source, "UPDATE sales.[Order] SET Total = Total + 1;");

            var report = await VerifyAsync(path, source);

            var order = report.Tables.Single(t => t.Name == "Order");

            Assert.Equal(TableOutcome.NotVerifiable, order.Outcome);
            Assert.False(order.ContentCompared);
            Assert.Equal(150, order.DatabaseRows);
            Assert.Contains("no row hash", order.Limitation, StringComparison.Ordinal);

            // Not a difference, and not a match: the manifest cannot answer.
            Assert.False(report.HasDifferences, Explain(report));
            Assert.Equal(1, report.Unverifiable);
        }
        finally
        {
            Delete(path);
        }
    }

    /// <summary>
    /// Rewrites the manifest inside the archive with one table's row hash taken out, so
    /// that the shape of a dbdumper archive can be exercised without one to hand.
    /// </summary>
    private static async Task ForgetTheRowHashAsync(string archivePath, string table)
    {
        ArchiveManifest manifest;

        using(var archive = await ArchiveReader.OpenAsync(archivePath))
            manifest = archive.Manifest;

        manifest.Tables.Single(t => t.Name == table).RowHash = null;

        using var zip = System.IO.Compression.ZipFile.Open(archivePath, System.IO.Compression.ZipArchiveMode.Update);
        var entry = zip.GetEntry(ArchiveFormat.ManifestEntry)!;

        using var stream = entry.Open();
        stream.SetLength(0);

        var bytes = ArchiveFormat.Utf8.GetBytes(ManifestSerializer.Serialize(manifest));
        await stream.WriteAsync(bytes);
    }

    // ------------------------------------------------------------ the flags

    /// <summary>
    /// <c>--schema-only</c> reads no table on either side, and that includes not
    /// decompressing the archive's own data entries to hash them.
    /// </summary>
    [LiveFact]
    public async Task SchemaOnlyReadsNoRowsOnEitherSide()
    {
        var source = await SeedAsync();
        var path = TempPath();

        try
        {
            await ExportAsync(source, path);

            await SqlServerFixture.ExecuteAsync(source, "DELETE FROM sales.[Order] WHERE Id = 7;");

            var report = await new ArchiveVerifier(new VerifyOptions
            {
                ConnectionString = source,
                SchemaOnly = true
            }).VerifyAsync(path);

            Assert.False(report.HasDifferences, Explain(report));
            Assert.True(report.Integrity.NotChecked > 0);
            Assert.All(report.Tables, t => Assert.Null(t.DatabaseRows));
        }
        finally
        {
            Delete(path);
        }
    }

    /// <summary>
    /// A glob narrows both sides at once. Comparing the archive's chosen tables against
    /// the whole database would report every table the run was told to ignore.
    /// </summary>
    [LiveFact]
    public async Task ATableGlobNarrowsBothSides()
    {
        var source = await SeedAsync();
        var path = TempPath();

        try
        {
            await ExportAsync(source, path);

            await SqlServerFixture.ExecuteAsync(source, "DELETE FROM sales.[Order] WHERE Id = 7;");

            var report = await new ArchiveVerifier(new VerifyOptions
            {
                ConnectionString = source,
                ExcludeTables = ["sales.Order"]
            }).VerifyAsync(path);

            Assert.DoesNotContain(report.Tables, t => t.Name == "Order");
            Assert.False(report.HasDifferences, Explain(report));
        }
        finally
        {
            Delete(path);
        }
    }

    /// <summary>Several tables at once, each through a connection of its own.</summary>
    [LiveFact]
    public async Task TablesAreReadInParallelAndTheAnswerIsTheSame()
    {
        var source = await SeedAsync();
        var path = TempPath();

        try
        {
            await ExportAsync(source, path);

            var one = await new ArchiveVerifier(new VerifyOptions { ConnectionString = source, Parallelism = 1 })
                .VerifyAsync(path);

            var many = await new ArchiveVerifier(new VerifyOptions { ConnectionString = source, Parallelism = 8 })
                .VerifyAsync(path);

            Assert.False(one.HasDifferences, Explain(one));
            Assert.False(many.HasDifferences, Explain(many));

            Assert.Equal(
                one.Tables.Select(t => (t.Identifier, t.DatabaseHash)),
                many.Tables.Select(t => (t.Identifier, t.DatabaseHash)));
        }
        finally
        {
            Delete(path);
        }
    }

    // ------------------------------------------------------------ the fixtures

    private static Task ExportAsync(string source, string path) =>
        new DatabaseExporter(new ExportOptions { ConnectionString = source }).ExportAsync(path);

    private static Task<VerifyReport> VerifyAsync(string path, string connectionString) =>
        new ArchiveVerifier(new VerifyOptions { ConnectionString = connectionString }).VerifyAsync(path);

    /// <summary>
    /// Builds a database out of the archive the way a restore does: the schema phases up
    /// to and including the tables, then the rows, then the phases that need the rows to
    /// be there.
    /// </summary>
    /// <remarks>
    /// Deliberately not <c>import</c>, which is another work package and would make this
    /// a test of two things. What it needs from the archive is only what the archive
    /// promises anybody: executable phases and readable rows.
    /// </remarks>
    private static async Task RestoreAsync(string archivePath, string source, string destination)
    {
        using var archive = await ArchiveReader.OpenAsync(archivePath);

        var beforeData = archive.SchemaEntries
            .Where(e => string.CompareOrdinal(e, ArchiveFormat.SchemaDirectory + "050") < 0)
            .ToArray();

        foreach(var phase in beforeData)
            await RunPhaseAsync(archive, phase, destination);

        await CopyRowsAsync(archive, source, destination);

        foreach(var phase in archive.SchemaEntries.Except(beforeData, StringComparer.Ordinal))
            await RunPhaseAsync(archive, phase, destination);
    }

    private static async Task RunPhaseAsync(ArchiveReader archive, string phase, string destination)
    {
        var sql = await archive.ReadTextAsync(phase);

        foreach(var batch in Batches(sql))
            await SqlServerFixture.ExecuteAsync(destination, batch);
    }

    /// <summary>
    /// The rows, straight across between two databases on one instance, through the
    /// archive's own column list - so the copy carries exactly what the archive carries
    /// and nothing the server assigns itself.
    /// </summary>
    private static async Task CopyRowsAsync(ArchiveReader archive, string source, string destination)
    {
        var snapshot = archive.Manifest.Schema!;
        var from = new SqlConnectionStringBuilder(source).InitialCatalog;

        foreach(var entry in archive.Manifest.Tables)
        {
            var model = SnapshotSelection.Tables(snapshot).Single(t =>
                t.Schema == entry.Schema && t.Name == entry.Name);

            var columns = ArchiveColumns.For(model, snapshot.Types);
            var list = string.Join(", ", columns.Select(c => $"[{c.Name}]"));
            var identity = model.Columns.Any(c => c.IsIdentity);
            var target = $"[{entry.Schema}].[{entry.Name}]";

            var sql =
                (identity ? $"SET IDENTITY_INSERT {target} ON;" : string.Empty) +
                $"INSERT INTO {target} ({list}) SELECT {list} FROM [{from}].[{entry.Schema}].[{entry.Name}];" +
                (identity ? $"SET IDENTITY_INSERT {target} OFF;" : string.Empty);

            await SqlServerFixture.ExecuteAsync(destination, sql);
        }
    }

    /// <summary>
    /// Splits a phase on its own <c>GO</c> lines, which is what sqlcmd does with it.
    /// <c>GO</c> is not a SQL statement, so a driver has to do the splitting itself.
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

    private async Task<string> SeedAsync()
    {
        var source = await _server.CreateDatabaseAsync();

        await SqlServerFixture.ExecuteAsync(source, "CREATE SCHEMA sales;");
        await SqlServerFixture.ExecuteAsync(source, Schema);
        await SqlServerFixture.ExecuteAsync(
            source,
            "CREATE VIEW dbo.CustomerSummary AS SELECT Id, Name, Balance FROM dbo.Customer;");
        await SqlServerFixture.ExecuteAsync(source, Rows);

        return source;
    }

    /// <summary>
    /// The whole report as a sentence, so that a failure says what verify said rather
    /// than only that a boolean was not the one expected.
    /// </summary>
    private static string Explain(VerifyReport report)
    {
        var lines = new List<string> { $"{report.Archive} against {report.Database ?? "nothing"}:" };

        foreach(var entry in report.Integrity.Entries.Where(e => e.State != EntryState.Intact))
            lines.Add($"  {entry.Entry}: {entry.State}");

        if(report.Schema is { } schema)
        {
            if(schema.OnlyInArchive.Count > 0)
                lines.Add("  only in the archive: " + string.Join(", ", schema.OnlyInArchive));

            if(schema.OnlyInDatabase.Count > 0)
                lines.Add("  only in the database: " + string.Join(", ", schema.OnlyInDatabase));

            if(schema.Differing.Count > 0)
                lines.Add("  differing: " + string.Join(", ", schema.Differing));
        }

        foreach(var table in report.Tables.Where(t => t.Outcome != TableOutcome.Matches))
            lines.Add($"  {table.Identifier}: {table.Outcome} - {string.Join(" ", table.Differences)}");

        return string.Join(Environment.NewLine, lines);
    }

    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"sqlarchive-verify-{Guid.NewGuid():N}.sqlarchive");

    private static void Delete(string path)
    {
        try
        {
            if(File.Exists(path))
                File.Delete(path);
        }
        catch(IOException)
        {
            // A leftover file in the temp directory is not a failed test.
        }
    }
}
