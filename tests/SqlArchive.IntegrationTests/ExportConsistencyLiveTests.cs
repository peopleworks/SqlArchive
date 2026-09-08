using Microsoft.Data.SqlClient;
using SqlArchive.Core.Export;
using SqlArchive.Core.Format;

namespace SqlArchive.IntegrationTests;

/// <summary>
/// The three steps of <c>--consistent</c>, each one made to happen for the reason it
/// really happens rather than by a flag that says which branch to take.
/// <para>
/// The check in every case is the same, and it is not that the manifest says the right
/// word. A row is inserted into the source <b>after</b> the session has been established
/// and before any table has been read; an export that has a point in time does not
/// contain it and an export that only claims one does. That is the assertion that fails
/// if <c>BeginTransaction()</c> is called without an isolation level - which sets READ
/// COMMITTED and reports snapshot isolation in the manifest anyway - and the one that
/// fails if the snapshot is left to be taken by the first table read instead of when the
/// session opens.
/// </para>
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class ExportConsistencyLiveTests
{
    private const string Password = "SqlArchive!Live#2026";

    private readonly SqlServerFixture _server;

    public ExportConsistencyLiveTests(SqlServerFixture server) => _server = server;

    private const string Seed = """
        CREATE TABLE dbo.Ledger (Id int NOT NULL PRIMARY KEY, Amount decimal(19,4) NOT NULL);
        CREATE TABLE dbo.Note   (Id int NOT NULL PRIMARY KEY, Text nvarchar(100) NOT NULL);

        WITH n AS (SELECT TOP (100) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_objects)
        INSERT INTO dbo.Ledger SELECT i, i * 1.25 FROM n;

        WITH n AS (SELECT TOP (100) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_objects)
        INSERT INTO dbo.Note SELECT i, CONCAT(N'note ', i) FROM n;
        """;

    /// <summary>Without the flag there is nothing to negotiate, and the manifest says so rather than being silent about it.</summary>
    [LiveFact]
    public async Task WithoutTheFlagTheArchiveSaysItWasReadTableByTable()
    {
        var source = await SeedAsync();
        var path = ExportLiveTests.TempPath();

        try
        {
            var result = await new DatabaseExporter(new ExportOptions { ConnectionString = source }).ExportAsync(path);

            Assert.Equal(ArchiveConsistency.PerTable, result.Consistency);

            using var archive = await ArchiveReader.OpenAsync(path);

            Assert.Equal(ArchiveConsistency.PerTable, archive.Manifest.Consistency);
            Assert.Contains("table by table", await archive.ReadTextAsync(ArchiveFormat.ReadmeEntry), StringComparison.Ordinal);
        }
        finally
        {
            ExportLiveTests.Clean(path);
        }
    }

    /// <summary>
    /// Step one: a database snapshot. The rows come out of the snapshot, so writes to the
    /// source while the export runs are not in the archive - and several connections can
    /// still read at once, which is the reason this is the first thing tried.
    /// </summary>
    [LiveFact]
    public async Task ADatabaseSnapshotIsTakenAndTheArchiveDoesNotSeeLaterWrites()
    {
        var source = await SeedAsync();
        var path = ExportLiveTests.TempPath();
        string? snapshotDatabase = null;

        try
        {
            var result = await new DatabaseExporter(new ExportOptions
            {
                ConnectionString = source,
                Consistent = true,
                OnConsistencyEstablished = async (established, token) =>
                {
                    snapshotDatabase = established.SnapshotDatabase;

                    // Written to the source after the instant was fixed and before a
                    // single row was read.
                    await SqlServerFixture.ExecuteAsync(source, "INSERT INTO dbo.Ledger VALUES (1000, 9.99);");
                    await SqlServerFixture.ExecuteAsync(source, "INSERT INTO dbo.Note VALUES (1000, N'too late');");
                    await Task.CompletedTask;
                    _ = token;
                }
            }).ExportAsync(path);

            Assert.Equal(ArchiveConsistency.Snapshot, result.Consistency);
            Assert.NotNull(snapshotDatabase);

            // It reads from the snapshot, and it also keeps the parallelism, which is why
            // this branch is preferred over the next one.
            Assert.True(result.Consistency == ArchiveConsistency.Snapshot);

            using var archive = await ArchiveReader.OpenAsync(path);

            Assert.Equal(ArchiveConsistency.Snapshot, archive.Manifest.Consistency);
            Assert.Equal(100, archive.Manifest.Table("dbo", "Ledger")!.RowCount);
            Assert.Equal(100, archive.Manifest.Table("dbo", "Note")!.RowCount);

            // The server really did move on, so the assertion above is about the snapshot
            // and not about an insert that never happened.
            Assert.Equal(101, Convert.ToInt64(await SqlServerFixture.ScalarAsync(source, "SELECT COUNT_BIG(*) FROM dbo.Ledger;")));

            // The archive is stamped with the source database, not with the throwaway one
            // it was read through - the name is what a later diff matches on.
            Assert.Equal(Database(source), archive.Manifest.Source.Database);
            Assert.Equal(Database(source), archive.Manifest.Schema!.DatabaseName);

            // And the snapshot is gone; it holds a sparse file beside the source's data
            // files for as long as it exists.
            Assert.Equal(
                0,
                Convert.ToInt32(await SqlServerFixture.ScalarAsync(
                    SqlServerFixture.ServerConnectionString,
                    $"SELECT COUNT(*) FROM sys.databases WHERE name = '{snapshotDatabase}';")));
        }
        finally
        {
            ExportLiveTests.Clean(path);
            await DropAsync(snapshotDatabase);
        }
    }

    /// <summary>
    /// Step two: no permission to create a database, so one connection under
    /// <c>SNAPSHOT</c> isolation, and the parallelism is what that costs.
    /// </summary>
    [LiveFact]
    public async Task WithoutCreateDatabaseItFallsBackToSnapshotIsolation()
    {
        var source = await SeedAsync();
        var login = await CreateLimitedLoginAsync(source);
        var path = ExportLiveTests.TempPath();

        try
        {
            await SqlServerFixture.ExecuteAsync(
                SqlServerFixture.ServerConnectionString,
                $"ALTER DATABASE [{Database(source)}] SET ALLOW_SNAPSHOT_ISOLATION ON;");

            var result = await new DatabaseExporter(new ExportOptions
            {
                ConnectionString = As(source, login),
                Consistent = true,
                OnConsistencyEstablished = async (_, _) =>
                {
                    await SqlServerFixture.ExecuteAsync(source, "INSERT INTO dbo.Ledger VALUES (1000, 9.99);");
                    await SqlServerFixture.ExecuteAsync(source, "INSERT INTO dbo.Note VALUES (1000, N'too late');");
                }
            }).ExportAsync(path);

            Assert.Equal(ArchiveConsistency.SnapshotIsolation, result.Consistency);
            Assert.Contains(result.Notices, n => n.Contains("SNAPSHOT isolation", StringComparison.Ordinal));

            // The notice says why the better mechanism was not available, in the server's
            // own words, so the operator can go and fix it.
            Assert.Contains(result.Notices, n => n.Contains("CREATE DATABASE", StringComparison.Ordinal));

            using var archive = await ArchiveReader.OpenAsync(path);

            Assert.Equal(ArchiveConsistency.SnapshotIsolation, archive.Manifest.Consistency);

            // Both tables at the same instant, and that instant is before the writes -
            // which is what fails if BeginTransaction is called without an isolation
            // level, or if the snapshot is left to be taken by the first table read.
            Assert.Equal(100, archive.Manifest.Table("dbo", "Ledger")!.RowCount);
            Assert.Equal(100, archive.Manifest.Table("dbo", "Note")!.RowCount);

            Assert.Equal(101, Convert.ToInt64(await SqlServerFixture.ScalarAsync(source, "SELECT COUNT_BIG(*) FROM dbo.Ledger;")));
        }
        finally
        {
            ExportLiveTests.Clean(path);
            await DropLoginAsync(login);
        }
    }

    /// <summary>
    /// Step three, which is the one it is easiest to cheat on: neither mechanism is there,
    /// so the export refuses, names both conditions, and writes nothing.
    /// <para>
    /// Falling back to table-by-table here would leave somebody who asked for a single
    /// point in time holding an archive that is not one and does not say so - worse off
    /// than somebody whose export failed.
    /// </para>
    /// </summary>
    [LiveFact]
    public async Task WithNeitherAvailableItRefusesAndNamesBothConditions()
    {
        var source = await SeedAsync();
        var login = await CreateLimitedLoginAsync(source);
        var path = ExportLiveTests.TempPath();

        try
        {
            Assert.Equal(
                0,
                Convert.ToInt32(await SqlServerFixture.ScalarAsync(
                    source, "SELECT snapshot_isolation_state FROM sys.databases WHERE database_id = DB_ID();")));

            var refused = await Assert.ThrowsAsync<ExportConsistencyException>(
                () => new DatabaseExporter(new ExportOptions
                {
                    ConnectionString = As(source, login),
                    Consistent = true
                }).ExportAsync(path));

            // Both conditions, each with what is actually missing.
            Assert.Contains("CREATE DATABASE permission denied", refused.SnapshotRefusal, StringComparison.Ordinal);
            Assert.Contains("ALLOW_SNAPSHOT_ISOLATION", refused.IsolationRefusal, StringComparison.Ordinal);

            Assert.Contains("database snapshot", refused.Message, StringComparison.Ordinal);
            Assert.Contains("ALLOW_SNAPSHOT_ISOLATION", refused.Message, StringComparison.Ordinal);

            // And it says what to do about it, including that exporting without the flag
            // is a choice rather than a failure.
            Assert.Contains("without --consistent", refused.Message, StringComparison.Ordinal);

            Assert.False(File.Exists(path), "a refused export writes no archive");
        }
        finally
        {
            ExportLiveTests.Clean(path);
            await DropLoginAsync(login);
        }
    }

    /// <summary>
    /// The same login, with the same lack of CREATE DATABASE, exports perfectly well
    /// without the flag - which is the point of the default being table by table.
    /// </summary>
    [LiveFact]
    public async Task TheSameLoginExportsWithoutTheFlag()
    {
        var source = await SeedAsync();
        var login = await CreateLimitedLoginAsync(source);
        var path = ExportLiveTests.TempPath();

        try
        {
            var result = await new DatabaseExporter(new ExportOptions { ConnectionString = As(source, login) })
                .ExportAsync(path);

            Assert.Equal(ArchiveConsistency.PerTable, result.Consistency);
            Assert.Equal(200, result.Rows);
        }
        finally
        {
            ExportLiveTests.Clean(path);
            await DropLoginAsync(login);
        }
    }

    /// <summary>
    /// The count check runs where the answer means something, and it compares the rows
    /// written against a <c>COUNT(*)</c> the range planner had no part in.
    /// </summary>
    [LiveFact]
    public async Task UnderAPointInTimeTheRowCountsAreCheckedAgainstTheServer()
    {
        var source = await SeedAsync();
        var path = ExportLiveTests.TempPath();

        try
        {
            var result = await new DatabaseExporter(new ExportOptions
            {
                ConnectionString = source,
                Consistent = true,
                Ranges = ExportRanges.Always,
                RowsPerRange = 10,
                VerifyRowCounts = true
            }).ExportAsync(path);

            Assert.Equal(ArchiveConsistency.Snapshot, result.Consistency);
            Assert.True(result.Units > result.Tables, "the tables have to be split for this to check the ranges");

            using var archive = await ArchiveReader.OpenAsync(path);

            Assert.Equal(100, archive.Manifest.Table("dbo", "Ledger")!.RowCount);
            Assert.True(archive.Manifest.Table("dbo", "Ledger")!.DataFiles.Count > 1);
        }
        finally
        {
            ExportLiveTests.Clean(path);
        }
    }

    private async Task<string> SeedAsync()
    {
        var source = await _server.CreateDatabaseAsync();
        await SqlServerFixture.ExecuteAsync(source, Seed);
        return source;
    }

    /// <summary>
    /// A login that owns the database and still cannot create one, which is the ordinary
    /// shape of an account a client hands over: everything inside, nothing at the server.
    /// </summary>
    private static async Task<string> CreateLimitedLoginAsync(string databaseConnectionString)
    {
        var login = "sqlarchive_it_" + Guid.NewGuid().ToString("N")[..8];

        await SqlServerFixture.ExecuteAsync(
            SqlServerFixture.ServerConnectionString,
            $"CREATE LOGIN [{login}] WITH PASSWORD = '{Password}', CHECK_POLICY = OFF;");

        await SqlServerFixture.ExecuteAsync(
            databaseConnectionString,
            $"CREATE USER [{login}] FOR LOGIN [{login}]; ALTER ROLE db_owner ADD MEMBER [{login}];");

        return login;
    }

    private static string As(string connectionString, string login) =>
        new SqlConnectionStringBuilder(connectionString)
        {
            IntegratedSecurity = false,
            UserID = login,
            Password = Password
        }.ConnectionString;

    private static string Database(string connectionString) =>
        new SqlConnectionStringBuilder(connectionString).InitialCatalog;

    private static async Task DropLoginAsync(string login)
    {
        try
        {
            SqlConnection.ClearAllPools();
            await SqlServerFixture.ExecuteAsync(SqlServerFixture.ServerConnectionString, $"DROP LOGIN [{login}];");
        }
        catch(SqlException)
        {
            // A login that will not drop is a nuisance in a test server, not a failure,
            // and the prefix makes it easy to find by hand.
        }
    }

    private static async Task DropAsync(string? database)
    {
        if(database is null)
            return;

        try
        {
            await SqlServerFixture.ExecuteAsync(SqlServerFixture.ServerConnectionString, $"DROP DATABASE [{database}];");
        }
        catch(SqlException)
        {
            // Already gone, which is what the test asserted.
        }
    }
}
