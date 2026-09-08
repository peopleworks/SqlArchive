using SqlArchive.Core.Export;
using SqlArchive.Core.Format;

namespace SqlArchive.IntegrationTests;

/// <summary>
/// An export cut in half and picked up again.
/// <para>
/// The archive a resumed export produces has to be the archive one pass would have
/// produced, and the comparison is by hash rather than by size: two files of the same
/// length are not the same file, and the row hash is the only thing in the manifest that
/// sees a row that changed rather than a row that went missing.
/// </para>
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class ExportResumeLiveTests
{
    private readonly SqlServerFixture _server;

    public ExportResumeLiveTests(SqlServerFixture server) => _server = server;

    /// <summary>Four tables, so that an export stopped after the first one has something left to do.</summary>
    private const string Seed = """
        CREATE TABLE dbo.Alpha  (Id int NOT NULL PRIMARY KEY, Payload nvarchar(50) NOT NULL);
        CREATE TABLE dbo.Bravo  (Id int NOT NULL PRIMARY KEY, Payload nvarchar(50) NOT NULL);
        CREATE TABLE dbo.Charlie(Id int NOT NULL PRIMARY KEY, Payload nvarchar(50) NOT NULL);
        CREATE TABLE dbo.Delta  (Id int NOT NULL PRIMARY KEY, Payload nvarchar(50) NOT NULL);

        WITH n AS (SELECT TOP (40) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_objects)
        INSERT INTO dbo.Alpha   SELECT i, CONCAT(N'alpha ',   i) FROM n;
        WITH n AS (SELECT TOP (40) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_objects)
        INSERT INTO dbo.Bravo   SELECT i, CONCAT(N'bravo ',   i) FROM n;
        WITH n AS (SELECT TOP (40) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_objects)
        INSERT INTO dbo.Charlie SELECT i, CONCAT(N'charlie ', i) FROM n;
        WITH n AS (SELECT TOP (40) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_objects)
        INSERT INTO dbo.Delta   SELECT i, CONCAT(N'delta ',   i) FROM n;
        """;

    /// <summary>
    /// Stopped after two tables and run again, the archive is the one a single pass gives
    /// - the same counts, the same row hashes, and the same bytes in every data entry.
    /// </summary>
    [LiveFact]
    public async Task AnInterruptedExportResumesToTheSameArchive()
    {
        var source = await SeedAsync();
        var reference = ExportLiveTests.TempPath();
        var resumed = ExportLiveTests.TempPath();
        var work = WorkPath();

        try
        {
            await new DatabaseExporter(new ExportOptions { ConnectionString = source }).ExportAsync(reference);

            var stopped = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => RunUntilAsync(source, resumed, work, stopAfter: 2));

            Assert.NotNull(stopped);
            Assert.True(Directory.Exists(work), "a resumable export keeps its spool when it fails");
            Assert.False(File.Exists(resumed), "an export that did not finish writes no archive");

            var second = await new DatabaseExporter(new ExportOptions
            {
                ConnectionString = source,
                Parallelism = 1,
                Resumable = true,
                WorkingDirectory = work
            }).ExportAsync(resumed);

            Assert.Contains(second.Notices, n => n.Contains("Resumed", StringComparison.Ordinal));
            Assert.False(Directory.Exists(work), "a finished export takes its spool away");

            using var expected = await ArchiveReader.OpenAsync(reference);
            using var actual = await ArchiveReader.OpenAsync(resumed);

            Assert.Equal(expected.Manifest.Tables.Count, actual.Manifest.Tables.Count);

            foreach(var table in expected.Manifest.Tables)
            {
                var other = actual.Manifest.Table(table.Schema, table.Name)!;

                Assert.Equal(table.RowCount, other.RowCount);
                Assert.Equal(table.RowHash, other.RowHash);
                Assert.Equal(table.DataFiles, other.DataFiles);

                // By hash and not by length: two files of the same size are not the same
                // file, and the point of the exercise is that the rows are identical.
                foreach(var file in table.DataFiles)
                    Assert.Equal(table.FileHashes[file], other.FileHashes[file]);
            }
        }
        finally
        {
            ExportLiveTests.Clean(reference);
            ExportLiveTests.Clean(resumed);
            Clean(work);
        }
    }

    /// <summary>
    /// The tables that finished are not read again, proved by changing them in between.
    /// <para>
    /// A resumed export that quietly re-read everything would pass the test above,
    /// because re-reading an unchanged database gives the same answer. So the database is
    /// changed between the two runs: the table that had already finished keeps the rows it
    /// had, and the one that had not picks up the new ones.
    /// </para>
    /// </summary>
    [LiveFact]
    public async Task AResumedExportDoesNotReadAgainWhatItAlreadyRead()
    {
        var source = await SeedAsync();
        var path = ExportLiveTests.TempPath();
        var work = WorkPath();

        try
        {
            // Tables are planned in name order, so one unit finished means dbo.Alpha.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => RunUntilAsync(source, path, work, stopAfter: 1));

            await SqlServerFixture.ExecuteAsync(source, "INSERT INTO dbo.Alpha VALUES (999, N'after the interruption');");
            await SqlServerFixture.ExecuteAsync(source, "INSERT INTO dbo.Delta VALUES (999, N'after the interruption');");

            await new DatabaseExporter(new ExportOptions
            {
                ConnectionString = source,
                Parallelism = 1,
                Resumable = true,
                WorkingDirectory = work
            }).ExportAsync(path);

            using var archive = await ArchiveReader.OpenAsync(path);

            Assert.Equal(41, Convert.ToInt64(await SqlServerFixture.ScalarAsync(source, "SELECT COUNT_BIG(*) FROM dbo.Alpha;")));

            // Alpha was already spooled, so the row added afterwards is not in the archive.
            Assert.Equal(40, archive.Manifest.Table("dbo", "Alpha")!.RowCount);

            // Delta had not been read yet, so it is.
            Assert.Equal(41, archive.Manifest.Table("dbo", "Delta")!.RowCount);
        }
        finally
        {
            ExportLiveTests.Clean(path);
            Clean(work);
        }
    }

    /// <summary>
    /// Resuming with other parameters is an error and not a merge. Half a table read with
    /// one filter and half with another is an archive the format cannot describe, and the
    /// manifest would report it as whole.
    /// </summary>
    [LiveFact]
    public async Task ResumingWithDifferentFiltersIsRefused()
    {
        var source = await SeedAsync();
        var path = ExportLiveTests.TempPath();
        var work = WorkPath();

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => RunUntilAsync(source, path, work, stopAfter: 1));

            var refused = await Assert.ThrowsAsync<ExportResumeException>(() => new DatabaseExporter(new ExportOptions
            {
                ConnectionString = source,
                Resumable = true,
                WorkingDirectory = work,
                RowFilters = [new KeyValuePair<string, string>("*", "Id < 10")]
            }).ExportAsync(path));

            Assert.Contains("filters", refused.Message, StringComparison.Ordinal);

            // And pointing somewhere else entirely says which two databases.
            var elsewhere = await _server.CreateDatabaseAsync();

            var wrongDatabase = await Assert.ThrowsAsync<ExportResumeException>(() => new DatabaseExporter(new ExportOptions
            {
                ConnectionString = elsewhere,
                Resumable = true,
                WorkingDirectory = work
            }).ExportAsync(path));

            Assert.Contains(Database(source), wrongDatabase.Message, StringComparison.Ordinal);
            Assert.Contains(Database(elsewhere), wrongDatabase.Message, StringComparison.Ordinal);
        }
        finally
        {
            ExportLiveTests.Clean(path);
            Clean(work);
        }
    }

    /// <summary>
    /// A spool file damaged between the read and the pack is caught, rather than sealed
    /// into an archive together with a manifest that agrees with it.
    /// </summary>
    [LiveFact]
    public async Task ASpoolFileChangedBetweenTheRunsIsRefused()
    {
        var source = await SeedAsync();
        var path = ExportLiveTests.TempPath();
        var work = WorkPath();

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => RunUntilAsync(source, path, work, stopAfter: 1));

            var spooled = Directory.GetFiles(Path.Combine(work, "units"), "*.jsonl").Single();
            var rows = await File.ReadAllLinesAsync(spooled);

            // Same number of rows, same number of bytes, one character different: the case
            // a length check cannot see and the reason the spool carries a hash.
            rows[0] = rows[0].Replace("alpha 1\"", "alpha X\"", StringComparison.Ordinal);
            await File.WriteAllTextAsync(spooled, string.Join('\n', rows) + '\n');

            var refused = await Assert.ThrowsAsync<ExportException>(() => new DatabaseExporter(new ExportOptions
            {
                ConnectionString = source,
                Resumable = true,
                WorkingDirectory = work
            }).ExportAsync(path));

            Assert.Contains("changed after it was written", refused.Message, StringComparison.Ordinal);
        }
        finally
        {
            ExportLiveTests.Clean(path);
            Clean(work);
        }
    }

    /// <summary>A run that is not resumable takes its spool away even when it fails, so a dead run does not leave a copy of the database behind.</summary>
    [LiveFact]
    public async Task ANonResumableRunLeavesNothingBehindWhenItFails()
    {
        var source = await SeedAsync();
        var path = ExportLiveTests.TempPath();
        var work = WorkPath();

        try
        {
            using var stop = new CancellationTokenSource();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DatabaseExporter(new ExportOptions
            {
                ConnectionString = source,
                Parallelism = 1,
                Resumable = false,
                WorkingDirectory = work,
                Progress = new CancelAfter(1, stop)
            }).ExportAsync(path, stop.Token));

            Assert.False(Directory.Exists(work));
        }
        finally
        {
            ExportLiveTests.Clean(path);
            Clean(work);
        }
    }

    private static async Task RunUntilAsync(string source, string path, string work, int stopAfter)
    {
        using var stop = new CancellationTokenSource();

        await new DatabaseExporter(new ExportOptions
        {
            ConnectionString = source,
            Parallelism = 1,
            Resumable = true,
            WorkingDirectory = work,
            Progress = new CancelAfter(stopAfter, stop)
        }).ExportAsync(path, stop.Token);
    }

    private async Task<string> SeedAsync()
    {
        var source = await _server.CreateDatabaseAsync();
        await SqlServerFixture.ExecuteAsync(source, Seed);
        return source;
    }

    private static string Database(string connectionString) =>
        new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connectionString).InitialCatalog;

    private static string WorkPath() =>
        Path.Combine(Path.GetTempPath(), $"sqlarchive-work-{Guid.NewGuid():N}");

    private static void Clean(string directory)
    {
        if(Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }

    /// <summary>
    /// Stops the export once a given number of table ranges have been written, which is
    /// as close to being killed half way through as a test can get without killing the
    /// process. The marker for a finished unit is written inside the unit, so what is on
    /// disk afterwards is what a real interruption would have left.
    /// </summary>
    private sealed class CancelAfter : IProgress<ExportProgress>
    {
        private readonly int _units;
        private readonly CancellationTokenSource _stop;

        public CancelAfter(int units, CancellationTokenSource stop)
        {
            _units = units;
            _stop = stop;
        }

        public void Report(ExportProgress value)
        {
            if(value.Phase == "reading" && value.UnitsDone >= _units)
                _stop.Cancel();
        }
    }
}
