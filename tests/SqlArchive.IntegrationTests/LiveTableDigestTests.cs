using Microsoft.Data.SqlClient;
using SqlArchive.Core.Export;
using SqlArchive.Core.Format;
using SqlArchive.Core.Verify;

namespace SqlArchive.IntegrationTests;

/// <summary>
/// The seam <c>verify</c> and <c>import</c>'s guard both stand on, checked against the
/// exporter rather than against itself.
/// <para>
/// A digest computed here and a hash written by the export are two different reads - the
/// export slices a table into ranges, spools them and sums the pieces; this makes one
/// pass and keeps nothing. If they agree on a database with an identity column, a
/// computed column, a <c>money</c>, a <c>datetime2</c> and a <c>varbinary(max)</c>, then
/// the encoder really is the only thing deciding what equality means, which is what
/// <c>DESIGN.md</c> claims.
/// </para>
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class LiveTableDigestTests
{
    private readonly SqlServerFixture _server;

    public LiveTableDigestTests(SqlServerFixture server) => _server = server;

    private const string Seed = """
        CREATE TABLE dbo.Widget (
            Id      int            IDENTITY(1,1) NOT NULL CONSTRAINT PK_Widget PRIMARY KEY,
            Name    nvarchar(100)  NOT NULL,
            Price   money          NOT NULL,
            Weight  decimal(19,4)  NULL,
            Made    datetime2(3)   NOT NULL,
            Blob    varbinary(max) NULL,
            Shout   AS UPPER([Name])
        );

        CREATE TABLE dbo.Empty (
            Id int NOT NULL CONSTRAINT PK_Empty PRIMARY KEY
        );

        INSERT INTO dbo.Widget (Name, Price, Weight, Made, Blob)
        SELECT TOP (500)
               CONCAT(N'Widget ', ROW_NUMBER() OVER (ORDER BY (SELECT NULL))),
               ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) * 1.25,
               CASE WHEN ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) % 7 = 0
                    THEN NULL ELSE ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) * 0.5 END,
               DATEADD(minute, ROW_NUMBER() OVER (ORDER BY (SELECT NULL)), '2026-03-01T00:00:00'),
               CASE WHEN ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) % 5 = 0
                    THEN NULL ELSE CONVERT(varbinary(max), REPLICATE('ab', 400)) END
        FROM sys.all_objects;
        """;

    /// <summary>
    /// Every table's digest, read live, is the one the export wrote into the manifest -
    /// including the empty one, whose hash is the sum of nothing.
    /// </summary>
    [LiveFact]
    public async Task TheDigestOfALiveTableIsTheOneTheManifestWrote()
    {
        var source = await SeedAsync();
        var path = TempPath();

        try
        {
            // Ranges small enough that the big table is read in several pieces and summed,
            // so the two sides of this assertion are not the same read done twice.
            await new DatabaseExporter(new ExportOptions
            {
                ConnectionString = source,
                RowsPerRange = 100,
                MinimumRowsToSplit = 100
            }).ExportAsync(path);

            using var archive = await ArchiveReader.OpenAsync(path);
            var manifest = archive.Manifest;
            var snapshot = manifest.Schema!;

            Assert.Equal(2, manifest.Tables.Count);

            foreach(var entry in manifest.Tables)
            {
                var model = SnapshotSelection.Tables(snapshot).Single(t =>
                    string.Equals(t.Schema, entry.Schema, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(t.Name, entry.Name, StringComparison.OrdinalIgnoreCase));

                var digest = await LiveTableDigest.ComputeAsync(
                    source, entry.Schema, entry.Name, ArchiveColumns.For(model, snapshot.Types), entry.RowFilter);

                Assert.Equal(entry.RowCount, digest.Rows);
                Assert.Equal(entry.RowHash, digest.RowHash);
            }

            // The big table really was cut, or the paragraph above is describing a test
            // that is not being run.
            Assert.True(manifest.Table("dbo", "Widget")!.DataFiles.Count > 1);
        }
        finally
        {
            Delete(path);
        }
    }

    /// <summary>
    /// The reason the tool exists: a thousand rows changed are still a thousand rows, and
    /// the count says nothing.
    /// </summary>
    [LiveFact]
    public async Task AnUpdateMovesTheHashAndLeavesTheCountWhereItWas()
    {
        var source = await SeedAsync();
        var columns = await ColumnsAsync(source, "Widget");

        var before = await LiveTableDigest.ComputeAsync(source, "dbo", "Widget", columns);

        await SqlServerFixture.ExecuteAsync(source, "UPDATE dbo.Widget SET Price = Price + 1 WHERE Id % 3 = 0;");

        var after = await LiveTableDigest.ComputeAsync(source, "dbo", "Widget", columns);

        Assert.Equal(before.Rows, after.Rows);
        Assert.NotEqual(before.RowHash, after.RowHash);
    }

    /// <summary>
    /// A filter hashes the rows it covers and no others, so that an archive written with
    /// a <c>--where</c> can be verified against the same slice it was cut from.
    /// </summary>
    [LiveFact]
    public async Task AFilterCoversTheSameRowsOnBothSides()
    {
        var source = await SeedAsync();
        var columns = await ColumnsAsync(source, "Widget");

        var filtered = await LiveTableDigest.ComputeAsync(
            source, "dbo", "Widget", columns, rowFilter: "Id <= 100");

        Assert.Equal(100, filtered.Rows);

        var whole = await LiveTableDigest.ComputeAsync(source, "dbo", "Widget", columns);

        Assert.Equal(500, whole.Rows);
        Assert.NotEqual(whole.RowHash, filtered.RowHash);

        // The parts sum to the whole, which is the property the additive hash was chosen
        // for and the one a XOR would have lost.
        var rest = await LiveTableDigest.ComputeAsync(
            source, "dbo", "Widget", columns, rowFilter: "Id > 100");

        var sum = new RowHash.Accumulator();
        sum.Add(Convert.FromHexString(filtered.RowHash));
        sum.Add(Convert.FromHexString(rest.RowHash));

        Assert.Equal(whole.RowHash, sum.Value);
    }

    /// <summary>
    /// A closed connection is a caller's mistake and says so, rather than opening one -
    /// the whole point of taking a connection is that import hands over the one that is
    /// inside the publishing transaction.
    /// </summary>
    [LiveFact]
    public async Task AClosedConnectionIsRefusedByName()
    {
        var source = await SeedAsync();
        var columns = await ColumnsAsync(source, "Widget");

        await using var connection = new SqlConnection(source);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            LiveTableDigest.ComputeAsync(connection, "dbo", "Widget", columns));

        Assert.Contains("[dbo].[Widget]", error.Message, StringComparison.Ordinal);
    }

    private async Task<string> SeedAsync()
    {
        var database = await _server.CreateDatabaseAsync();
        await SqlServerFixture.ExecuteAsync(database, Seed);
        return database;
    }

    /// <summary>
    /// The archived columns of one table, taken the way a manifest reader takes them:
    /// through the schema snapshot, not through the live table's own column list.
    /// </summary>
    private static async Task<IReadOnlyList<ArchiveColumn>> ColumnsAsync(string connectionString, string table)
    {
        var snapshot = await new SqlSchemaDiff.Services.SqlServerSchemaExtractor()
            .ExtractAsync(connectionString, CancellationToken.None);

        var model = SnapshotSelection.Tables(snapshot).Single(t =>
            string.Equals(t.Name, table, StringComparison.OrdinalIgnoreCase));

        return ArchiveColumns.For(model, snapshot.Types);
    }

    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"sqlarchive-digest-{Guid.NewGuid():N}.sqlarchive");

    private static void Delete(string path)
    {
        try
        {
            if(File.Exists(path))
                File.Delete(path);
        }
        catch(IOException)
        {
            // A leftover file in the temp directory is not worth failing a green test for.
        }
    }
}
