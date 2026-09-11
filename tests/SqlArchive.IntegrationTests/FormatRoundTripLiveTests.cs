using System.Data;
using System.Data.Common;
using System.Data.SqlTypes;
using System.Text;
using Microsoft.Data.SqlClient;
using SqlArchive.Core.Format;
using SqlSchemaDiff.Models;
using SqlSchemaDiff.Services;

namespace SqlArchive.IntegrationTests;

/// <summary>
/// The test the whole package rests on: one column of every type in the encoding table,
/// with values at the edges of what each type can hold, out to an archive and back into a
/// different database, with the bytes compared.
/// <para>
/// Everything else in this package can be checked against itself. This cannot, and that
/// asymmetry is the point: an encoding that loses precision produces an archive that
/// parses, that <c>verify</c> declares intact - because it is comparing what it wrote with
/// what it wrote - and that restores something subtly other than what was there. Only a
/// real server can say whether the value that came out is the value that went in.
/// </para>
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class FormatRoundTripLiveTests
{
    private readonly SqlServerFixture _server;

    public FormatRoundTripLiveTests(SqlServerFixture server) => _server = server;

    /// <summary>
    /// One column per row of the encoding table. Named so a failure says which type broke
    /// rather than which ordinal.
    /// </summary>
    private const string CreateTable = """
        CREATE TABLE dbo.EveryType (
            [Id]            int IDENTITY(1,1) NOT NULL PRIMARY KEY,
            [Bit]           bit               NULL,
            [TinyInt]       tinyint           NULL,
            [SmallInt]      smallint          NULL,
            [Int]           int               NULL,
            [BigInt]        bigint            NULL,
            [Decimal38]     decimal(38,10)    NULL,
            [Decimal380]    decimal(38,0)     NULL,
            [Numeric194]    numeric(19,4)     NULL,
            [Money]         money             NULL,
            [SmallMoney]    smallmoney        NULL,
            [Alias]         dbo.Dinero        NULL,
            [Float]         float             NULL,
            [Real]          real              NULL,
            [Date]          date              NULL,
            [Time7]         time(7)           NULL,
            [Time0]         time(0)           NULL,
            [DateTime]      datetime          NULL,
            [SmallDateTime] smalldatetime     NULL,
            [DateTime2_7]   datetime2(7)      NULL,
            [DateTime2_0]   datetime2(0)      NULL,
            [DateTimeOff]   datetimeoffset(7) NULL,
            [Guid]          uniqueidentifier  NULL,
            [Char]          char(10)          NULL,
            [VarChar]       varchar(100)      NULL,
            [NChar]         nchar(10)         NULL,
            [NVarChar]      nvarchar(max)     NULL,
            [Text]          text              NULL,
            [NText]         ntext             NULL,
            [Xml]           xml               NULL,
            [Binary]        binary(4)         NULL,
            [VarBinary]     varbinary(max)    NULL,
            [Image]         image             NULL,
            [Hierarchy]     hierarchyid       NULL,
            [Geography]     geography         NULL,
            [Geometry]      geometry          NULL,
            [Computed]      AS ([Int] * 2),
            [Version]       rowversion        NOT NULL
        );
        """;

    private const string CreateAliasType = "CREATE TYPE dbo.Dinero FROM decimal(19,4) NULL;";

    /// <summary>
    /// Four rows: everything null, everything at the largest a type can hold, everything
    /// at the smallest - plus the empty string and the zero-length binary, which are not
    /// null and have to stay that way - and one row whose values are large enough to make
    /// a line bigger than any buffer in the reader.
    /// <para>
    /// The non-ASCII text is built with <c>NCHAR</c> rather than written as a literal, so
    /// that no editor, no encoding and no copy of this file between machines can quietly
    /// turn the emoji into a question mark and leave a test that looks like it covers
    /// surrogate pairs and does not.
    /// </para>
    /// </summary>
    private const string InsertRows = """
        INSERT INTO dbo.EveryType (
            [Bit],[TinyInt],[SmallInt],[Int],[BigInt],
            [Decimal38],[Decimal380],[Numeric194],[Money],[SmallMoney],[Alias],
            [Float],[Real],
            [Date],[Time7],[Time0],[DateTime],[SmallDateTime],[DateTime2_7],[DateTime2_0],[DateTimeOff],
            [Guid],[Char],[VarChar],[NChar],[NVarChar],[Text],[NText],[Xml],
            [Binary],[VarBinary],[Image],[Hierarchy],[Geography],[Geometry])
        VALUES
        (NULL,NULL,NULL,NULL,NULL,
         NULL,NULL,NULL,NULL,NULL,NULL,
         NULL,NULL,
         NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,
         NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,
         NULL,NULL,NULL,NULL,NULL,NULL),

        (1, 255, 32767, 2147483647, 9223372036854775807,
         9999999999999999999999999999.9999999999, 99999999999999999999999999999999999999,
         999999999999999.9999, 922337203685477.5807, 214748.3647, 999999999999999.9999,
         1.7976931348623157E+308, 3.40282346E+38,
         '9999-12-31', '23:59:59.9999999', '23:59:59', '9999-12-31T23:59:59.997',
         '2079-06-06T23:59:00', '9999-12-31T23:59:59.9999999', '9999-12-31T23:59:59',
         '9999-12-31T23:59:59.9999999+14:00',
         'FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF', 'abc', 'x', N'abc',
         N'accent ' + NCHAR(0x00F1) + N' cjk ' + NCHAR(0x65E5) + N' emoji ' + NCHAR(0xD83D) + NCHAR(0xDE00)
             + N' quote " backslash \ tab ' + NCHAR(9) + N' newline ' + NCHAR(10),
         'text', N'ntext', '<root a="1"><child/></root>',
         0xFFFFFFFF, 0x00, 0xFF,
         hierarchyid::Parse('/1/2/3/'), geography::Point(47.6062, -122.3321, 4326), geometry::Point(1.5, -2.5, 0)),

        (0, 0, -32768, -2147483648, -9223372036854775808,
         -9999999999999999999999999999.9999999999, -99999999999999999999999999999999999999,
         -999999999999999.9999, -922337203685477.5808, -214748.3648, -999999999999999.9999,
         -1.7976931348623157E+308, -3.40282346E+38,
         '0001-01-01', '00:00:00', '00:00:00', '1753-01-01T00:00:00',
         '1900-01-01T00:00:00', '0001-01-01T00:00:00', '0001-01-01T00:00:00',
         '0001-01-01T00:00:00.0000000-14:00',
         '00000000-0000-0000-0000-000000000000', '', '', N'', N'',
         '', N'', '<root/>',
         0x00000000, 0x, 0x,
         hierarchyid::Parse('/'), geography::Point(0, 0, 4326), geometry::Point(0, 0, 0));

        -- One row bigger than any buffer in the reader, so growing a line is exercised
        -- against real data rather than only against a unit test's fabricated one.
        INSERT INTO dbo.EveryType ([VarBinary], [NVarChar], [Int])
        VALUES (CONVERT(varbinary(max), REPLICATE(CONVERT(varchar(max), 'ab'), 50000)),
                REPLICATE(CONVERT(nvarchar(max), N'x'), 60000),
                7);
        """;

    /// <summary>
    /// Read the table, archive it, read the archive, load it into a second database, read
    /// that, and compare the two archives byte for byte. Anything the encoding rounds,
    /// truncates or reorders shows up as a difference in the lines.
    /// </summary>
    [LiveFact]
    public async Task EveryTypeSurvivesTheRoundTripThroughARealServer()
    {
        var (source, destination) = await _server.CreatePairAsync();

        await Seed(source);
        await SqlServerFixture.ExecuteAsync(destination, CreateAliasType);
        await SqlServerFixture.ExecuteAsync(destination, CreateTable);

        var snapshot = await new SqlServerSchemaExtractor().ExtractAsync(source, CancellationToken.None);
        var table = snapshot.Objects.Single(o => o.Type == DbObjectType.Table && o.Name == "EveryType").Table!;
        var columns = ArchiveColumns.For(table, snapshot.Types);

        // The three columns SQL Server assigns itself are not in the archive, and the
        // reason each one is out is a different sentence in FORMAT.md.
        Assert.DoesNotContain(columns, c => c.Name is "Computed" or "Version");
        Assert.Equal(
            [("Computed", "computed"), ("Version", "rowversion")],
            ArchiveColumns.Omitted(table).ToArray());

        var path = Path.Combine(Path.GetTempPath(), $"sqlarchive-live-{Guid.NewGuid():N}.sqlarchive");

        try
        {
            var exported = await ExportAsync(path, source, snapshot, table, columns);

            Assert.Equal(4, exported.RowCount);

            await RestoreAsync(path, destination, columns, exported);

            // And now the same read again, from the database the archive was loaded into.
            var restored = await ReadLinesAsync(destination, columns);
            var original = await ReadLinesAsync(source, columns);

            Assert.Equal(original.Count, restored.Count);

            for(var i = 0; i < original.Count; i++)
                Assert.Equal(original[i], restored[i]);

            // The hash is what a verify compares, so assert on it too rather than only on
            // the lines it is computed from.
            var again = new RowHash.Accumulator();
            foreach(var line in restored)
                again.AddRow(Encoding.UTF8.GetBytes(line));

            Assert.Equal(exported.RowHash, again.Value);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A line read out of a real server, pinned to a literal.
    /// <para>
    /// The round-trip test above cannot catch an encoding that is wrong in the same way
    /// on both sides - it reads, writes, reads again, and compares what this build
    /// produced with what this build produced, which is exactly the trap the work package
    /// warns about. Writing money as <c>SqlMoney.ToString()</c> passes it. So one row has
    /// its bytes written down here, where changing them means changing this file on
    /// purpose.
    /// </para>
    /// </summary>
    [LiveFact]
    public async Task ARowFromARealServerHasTheseExactBytes()
    {
        var source = await _server.CreateDatabaseAsync();

        await SqlServerFixture.ExecuteAsync(source, """
            CREATE TABLE dbo.Pinned (
                [Money]      money             NOT NULL,
                [SmallMoney] smallmoney        NOT NULL,
                [Numeric]    numeric(19,4)     NOT NULL,
                [Decimal38]  decimal(38,10)    NOT NULL,
                [Float]      float             NOT NULL,
                [Real]       real              NOT NULL,
                [Bit]        bit               NOT NULL,
                [Guid]       uniqueidentifier  NOT NULL,
                [Date]       date              NOT NULL,
                [Time]       time(7)           NOT NULL,
                [DateTime]   datetime          NOT NULL,
                [DateTime2]  datetime2(7)      NOT NULL,
                [Offset]     datetimeoffset(7) NOT NULL,
                [Binary]     varbinary(8)      NOT NULL,
                [Text]       nvarchar(50)      NOT NULL
            );
            """);

        await SqlServerFixture.ExecuteAsync(source, """
            INSERT INTO dbo.Pinned VALUES (
                1, 1, 1, 1,
                0.1, 0.1,
                1,
                '3F2504E0-4F89-11D3-9A0C-0305E82C3301',
                '2026-01-15', '10:00:00.1234567',
                '2026-01-15T10:00:00.003', '2026-01-15T10:00:00.1234567',
                '2026-01-15T10:00:00.1234567+02:00',
                0xDEADBEEF,
                N'a' + NCHAR(0x00F1) + NCHAR(0xD83D) + NCHAR(0xDE00));
            """);

        await using var connection = new SqlConnection(source);
        await connection.OpenAsync();
        await using var command = new SqlCommand("SELECT * FROM dbo.Pinned", connection);
        await using DbDataReader reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());

        var columns = ArchiveColumns.ForReader(reader);
        var buffer = new CanonicalJsonBuffer();
        RowEncoder.Encode(columns, reader, buffer);

        Assert.Equal(
            """{"Money":"1.0000","SmallMoney":"1.0000","Numeric":"1.0000","Decimal38":"1.0000000000","Float":0.1,"Real":0.1,"Bit":true,"Guid":"3f2504e0-4f89-11d3-9a0c-0305e82c3301","Date":"2026-01-15","Time":"10:00:00.1234567","DateTime":"2026-01-15T10:00:00.003","DateTime2":"2026-01-15T10:00:00.1234567","Offset":"2026-01-15T10:00:00.1234567+02:00","Binary":"3q2+7w==","Text":"añ😀"}""",
            Encoding.UTF8.GetString(buffer.WrittenSpan));
    }

    /// <summary>
    /// The two ways of describing a table's columns have to agree, or export and restore
    /// would encode the same row differently. One reads a snapshot, the other reads a
    /// live result set, and neither knows about the other.
    /// </summary>
    [LiveFact]
    public async Task TheSnapshotAndTheReaderDescribeTheSameColumns()
    {
        var source = await _server.CreateDatabaseAsync();
        await Seed(source);

        var snapshot = await new SqlServerSchemaExtractor().ExtractAsync(source, CancellationToken.None);
        var table = snapshot.Objects.Single(o => o.Name == "EveryType").Table!;
        var fromSnapshot = ArchiveColumns.For(table, snapshot.Types);

        await using var connection = new SqlConnection(source);
        await connection.OpenAsync();
        await using var command = new SqlCommand(Select(fromSnapshot), connection);
        await using var reader = await command.ExecuteReaderAsync();

        var fromReader = ArchiveColumns.ForReader(reader);

        Assert.Equal(fromSnapshot.Count, fromReader.Count);

        for(var i = 0; i < fromSnapshot.Count; i++)
        {
            Assert.Equal(fromSnapshot[i].Name, fromReader[i].Name);
            Assert.Equal(fromSnapshot[i].Kind, fromReader[i].Kind);
            Assert.Equal(RowDecoder.ParameterType(fromSnapshot[i]), RowDecoder.ParameterType(fromReader[i]));
        }

        // Including the alias column, which both sides have to resolve to the decimal it
        // is built from without either of them consulting the other.
        Assert.Equal("decimal", fromSnapshot.Single(c => c.Name == "Alias").TypeName);

        // The names themselves do not always match, and that is the server's doing rather
        // than a bug: numeric and decimal are one type under two spellings, sys.types
        // keeps whichever was written and the driver reports the one on the wire. What
        // has to agree is the encoding, which is what the assertions above check.
        Assert.Equal("numeric", fromSnapshot.Single(c => c.Name == "Numeric194").TypeName);
        Assert.Equal("decimal", fromReader.Single(c => c.Name == "Numeric194").TypeName);

        // The proof that the difference does not reach the bytes: the same row, encoded
        // through each description, comes out identical.
        var bySnapshot = new CanonicalJsonBuffer();
        var byReader = new CanonicalJsonBuffer();

        Assert.True(await reader.ReadAsync());
        RowEncoder.Encode(fromSnapshot, reader, bySnapshot);
        RowEncoder.Encode(fromReader, reader, byReader);

        Assert.Equal(bySnapshot.WrittenSpan.ToArray(), byReader.WrittenSpan.ToArray());
    }

    /// <summary>
    /// The trap the work package warns about, demonstrated. The ordinary accessor throws
    /// on the largest value a decimal(38,10) can hold, so an exporter written the obvious
    /// way falls over on exactly the data that most needs archiving - or, worse, silently
    /// widens the column to something that fits.
    /// </summary>
    [LiveFact]
    public async Task GetValueOverflowsOnADecimalThatTheEncoderHandles()
    {
        var source = await _server.CreateDatabaseAsync();
        await SqlServerFixture.ExecuteAsync(source, "CREATE TABLE dbo.T (D decimal(38,10) NOT NULL);");
        await SqlServerFixture.ExecuteAsync(source,
            "INSERT INTO dbo.T VALUES (9999999999999999999999999999.9999999999);");

        var columns = new[] { new ArchiveColumn("D", "decimal", 38, 10) };
        var buffer = new CanonicalJsonBuffer();

        await using var connection = new SqlConnection(source);
        await connection.OpenAsync();
        await using var command = new SqlCommand("SELECT D FROM dbo.T", connection);
        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        Assert.Throws<OverflowException>(() => reader.GetValue(0));

        RowEncoder.Encode(columns, reader, buffer);

        Assert.Equal(
            """{"D":"9999999999999999999999999999.9999999999"}""",
            Encoding.UTF8.GetString(buffer.WrittenSpan));
    }

    /// <summary>
    /// Why the fraction is trimmed rather than padded. A line has to be something a
    /// person can paste back into the server it came from, and datetime refuses seven
    /// fractional digits even when all seven are zeros.
    /// </summary>
    [LiveFact]
    public async Task ADateTimeLiteralFromTheArchiveGoesStraightBackIn()
    {
        var source = await _server.CreateDatabaseAsync();
        await SqlServerFixture.ExecuteAsync(source, "CREATE TABLE dbo.T (D datetime NOT NULL);");

        var columns = new[] { new ArchiveColumn("D", "datetime") };
        var buffer = new CanonicalJsonBuffer();
        RowEncoder.Encode(columns, new object?[] { new DateTime(2026, 1, 15, 10, 0, 0) }, buffer);

        var text = Encoding.UTF8.GetString(buffer.WrittenSpan);
        var literal = text["{\"D\":\"".Length..^2];

        Assert.Equal("2026-01-15T10:00:00", literal);
        await SqlServerFixture.ExecuteAsync(source, $"INSERT INTO dbo.T VALUES ('{literal}');");

        var padded = await Assert.ThrowsAsync<SqlException>(
            () => SqlServerFixture.ExecuteAsync(source, "INSERT INTO dbo.T VALUES ('2026-01-15T10:00:00.0000000');"));

        Assert.Contains("date and/or time", padded.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// SQL Server stores an unpaired surrogate; Microsoft.Data.SqlClient replaces it with
    /// U+FFFD on the way out. So the loss happens in the driver, before this format sees
    /// anything, and what SqlArchive writes is what every other .NET reader of that
    /// database sees.
    /// <para>
    /// Asserted rather than assumed, because the day a driver stops sanitising, the
    /// encoder starts refusing rows and somebody has to know why.
    /// </para>
    /// </summary>
    [LiveFact]
    public async Task AnUnpairedSurrogateIsAlreadyLostBeforeTheEncoderSeesIt()
    {
        var source = await _server.CreateDatabaseAsync();
        await SqlServerFixture.ExecuteAsync(source, "CREATE TABLE dbo.T (S nvarchar(20) NOT NULL);");
        await SqlServerFixture.ExecuteAsync(source, "INSERT INTO dbo.T VALUES (NCHAR(0xD800) + N'y');");

        // The server really is holding one.
        Assert.Equal(0xD800, Convert.ToInt32(
            await SqlServerFixture.ScalarAsync(source, "SELECT UNICODE(LEFT(S, 1)) FROM dbo.T;")));

        await using var connection = new SqlConnection(source);
        await connection.OpenAsync();
        await using var command = new SqlCommand("SELECT S FROM dbo.T", connection);
        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());

        var value = reader.GetString(0);

        Assert.Equal('�', value[0]);
        Assert.False(char.IsSurrogate(value[0]));

        var buffer = new CanonicalJsonBuffer();
        RowEncoder.Encode([new ArchiveColumn("S", "nvarchar")], reader, buffer);

        Assert.Equal("{\"S\":\"�y\"}", Encoding.UTF8.GetString(buffer.WrittenSpan));
    }

    /// <summary>
    /// A rowversion refuses an explicit value, always. A period column refuses one only
    /// while the period exists - with versioning off too - and takes one the moment the
    /// period is gone. That difference is the whole reason the first is left out of the
    /// archive and the second is carried, so it is checked against the server rather than
    /// taken from the documentation.
    /// </summary>
    /// <remarks>
    /// Until WP 2.6 this was <c>TheServerRefusesTheColumnsTheArchiveLeavesOut</c> and ended
    /// asserting that a temporal table's archived columns were its key alone. The refusal
    /// it measured is still true; what it concluded from it was the loss of every period.
    /// </remarks>
    [LiveFact]
    public async Task TheServerRefusesARowVersionAlwaysAndAPeriodColumnOnlyWhileThePeriodExists()
    {
        var source = await _server.CreateDatabaseAsync();

        await SqlServerFixture.ExecuteAsync(source,
            "CREATE TABLE dbo.RV (Id int NOT NULL PRIMARY KEY, V rowversion NOT NULL);");

        var rowversion = await Assert.ThrowsAsync<SqlException>(
            () => SqlServerFixture.ExecuteAsync(source, "INSERT INTO dbo.RV (Id, V) VALUES (1, 0x01);"));

        Assert.Contains("timestamp column", rowversion.Message, StringComparison.OrdinalIgnoreCase);

        await SqlServerFixture.ExecuteAsync(source, """
            CREATE TABLE dbo.Temporal (
                Id int NOT NULL PRIMARY KEY,
                SysStart datetime2(7) GENERATED ALWAYS AS ROW START NOT NULL,
                SysEnd   datetime2(7) GENERATED ALWAYS AS ROW END   NOT NULL,
                PERIOD FOR SYSTEM_TIME (SysStart, SysEnd)
            ) WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = dbo.TemporalHistory));
            """);

        // Even with versioning off, which is the state a restore loads rows in.
        await SqlServerFixture.ExecuteAsync(source, "ALTER TABLE dbo.Temporal SET (SYSTEM_VERSIONING = OFF);");

        var period = await Assert.ThrowsAsync<SqlException>(
            () => SqlServerFixture.ExecuteAsync(source,
                "INSERT INTO dbo.Temporal (Id, SysStart, SysEnd) VALUES (1, '2020-01-01', '9999-12-31');"));

        Assert.Contains("GENERATED ALWAYS", period.Message, StringComparison.Ordinal);

        var snapshot = await new SqlServerSchemaExtractor().ExtractAsync(source, CancellationToken.None);
        var temporal = snapshot.Objects.Single(o => o.Name == "Temporal").Table!;

        // The archive carries the period, because a restore can take it: with the period
        // dropped the same columns are plain datetime2, the same INSERT goes in, and the
        // period goes back on over the rows.
        Assert.Equal(["Id", "SysStart", "SysEnd"], ArchiveColumns.For(temporal).Select(c => c.Name).ToArray());

        await SqlServerFixture.ExecuteAsync(source, "ALTER TABLE dbo.Temporal DROP PERIOD FOR SYSTEM_TIME;");
        await SqlServerFixture.ExecuteAsync(
            source, "INSERT INTO dbo.Temporal (Id, SysStart, SysEnd) VALUES (1, '2020-01-01', '9999-12-31 23:59:59.9999999');");
        await SqlServerFixture.ExecuteAsync(source, "ALTER TABLE dbo.Temporal ADD PERIOD FOR SYSTEM_TIME (SysStart, SysEnd);");

        Assert.Equal(
            new DateTime(2020, 1, 1),
            (DateTime)(await SqlServerFixture.ScalarAsync(source, "SELECT SysStart FROM dbo.Temporal WHERE Id = 1;"))!);

        Assert.Equal(
            1,
            Convert.ToInt32(await SqlServerFixture.ScalarAsync(
                source, "SELECT COUNT(*) FROM sys.periods WHERE object_id = OBJECT_ID(N'dbo.Temporal');")));
    }

    private async Task Seed(string connectionString)
    {
        await SqlServerFixture.ExecuteAsync(connectionString, CreateAliasType);
        await SqlServerFixture.ExecuteAsync(connectionString, CreateTable);
        await SqlServerFixture.ExecuteAsync(connectionString, InsertRows);
    }

    private static string Select(IReadOnlyList<ArchiveColumn> columns) =>
        "SELECT " + string.Join(", ", columns.Select(c => $"[{c.Name}]")) + " FROM dbo.EveryType ORDER BY [Id];";

    /// <summary>Reads the table and writes a whole archive: schema phase, data, README, manifest.</summary>
    private static async Task<ArchiveTableEntry> ExportAsync(
        string path,
        string connectionString,
        DatabaseSnapshot snapshot,
        TableModel table,
        IReadOnlyList<ArchiveColumn> columns)
    {
        var entryName = ArchiveFormat.DataEntry(table.Schema, table.Name);

        var entry = new ArchiveTableEntry
        {
            Schema = table.Schema,
            Name = table.Name,
            DataFiles = [entryName],
            OmittedColumns = ArchiveColumns.Omitted(table).ToDictionary(o => o.Column, o => o.Reason, StringComparer.Ordinal)
        };

        var writer = ArchiveWriter.Create(path);

        await using(writer)
        {
            await writer.AddTextAsync(ArchiveFormat.SchemaEntry("040_tables.sql"), CreateTable);

            var stream = writer.CreateEntry(entryName);

            await using(stream)
            {
                var rows = new JsonlRowWriter(stream, columns);

                await using(rows)
                {
                    await using var connection = new SqlConnection(connectionString);
                    await connection.OpenAsync();
                    await using var command = new SqlCommand(Select(columns), connection);
                    await using var reader = await command.ExecuteReaderAsync();

                    entry.RowCount = await rows.WriteAllAsync(reader);
                }

                entry.RowHash = rows.Hash.Value;
            }

            var manifest = new ArchiveManifest
            {
                Tool = new ArchiveTool { Version = "0.1.0" },
                CreatedAt = DateTimeOffset.UtcNow,
                Source = new ArchiveSource { Server = "localhost", Database = snapshot.DatabaseName },
                Schema = snapshot,
                Tables = [entry]
            };

            await writer.AddTextAsync(ArchiveFormat.ReadmeEntry, ArchiveReadme.Compose(manifest));

            foreach(var hash in writer.EntryHashes)
            {
                if(hash.Key == entryName)
                    entry.FileHashes[hash.Key] = hash.Value;
                else
                    manifest.Files[hash.Key] = hash.Value;
            }

            await writer.WriteManifestAsync(manifest);
        }

        return entry;
    }

    /// <summary>
    /// Reads the archive back and loads it into the destination, refusing to write
    /// anything unless the rows are the ones the manifest declared. That guard is exact
    /// here rather than heuristic, which is the whole reason the manifest carries a count
    /// and a hash.
    /// </summary>
    private static async Task RestoreAsync(
        string path,
        string connectionString,
        IReadOnlyList<ArchiveColumn> columns,
        ArchiveTableEntry expected)
    {
        var loaded = new List<object?[]>();

        using(var archive = await ArchiveReader.OpenAsync(path))
        {
            Assert.False(archive.Manifest.RequiresNewerReader);

            var declared = archive.Manifest.Table("dbo", "EveryType")!;
            var rows = archive.OpenTable(declared, columns);

            await using(rows)
            {
                while(await rows.ReadAsync())
                    loaded.Add(rows.Current.ToArray());

                Assert.True(rows.Matches(), rows.Difference());
                Assert.Equal(expected.RowHash, rows.Hash.Value);
            }

            foreach(var name in archive.Manifest.DeclaredEntries())
                Assert.Equal(archive.Manifest.HashOf(name), await archive.ComputeEntryHashAsync(name));
        }

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        var names = string.Join(", ", columns.Select(c => $"[{c.Name}]"));
        var parameters = string.Join(", ", columns.Select((_, i) => $"@p{i}"));

        foreach(var row in loaded)
        {
            await using var command = new SqlCommand(
                $"SET IDENTITY_INSERT dbo.EveryType ON; INSERT INTO dbo.EveryType ({names}) VALUES ({parameters}); SET IDENTITY_INSERT dbo.EveryType OFF;",
                connection);

            for(var i = 0; i < columns.Count; i++)
                command.Parameters.Add(Parameter($"@p{i}", columns[i], row[i]));

            await command.ExecuteNonQueryAsync();
        }
    }

    /// <summary>
    /// One decoded value as a parameter. The type comes from the format's own mapping, so
    /// what goes back in is decided by the same table that decided what came out.
    /// </summary>
    private static SqlParameter Parameter(string name, ArchiveColumn column, object? value)
    {
        var parameter = new SqlParameter(name, RowDecoder.ParameterType(column))
        {
            Value = value ?? DBNull.Value
        };

        if(column.Kind == SqlValueKind.Decimal)
        {
            // Without these the driver infers them from the value, and a SqlDecimal wider
            // than a .NET decimal has nothing to infer from.
            parameter.Precision = column.Precision;
            parameter.Scale = column.Scale;
        }

        // -1 is what sys.columns records for a MAX column, and it is also what the driver
        // wants told; everything else it can work out from the value.
        if(column.MaxLength == -1)
            parameter.Size = -1;

        return parameter;
    }

    /// <summary>Reads a table and gives back one canonical line per row.</summary>
    private static async Task<List<string>> ReadLinesAsync(string connectionString, IReadOnlyList<ArchiveColumn> columns)
    {
        var lines = new List<string>();
        var buffer = new CanonicalJsonBuffer();

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(Select(columns), connection);
        await using DbDataReader reader = await command.ExecuteReaderAsync();

        while(await reader.ReadAsync())
        {
            RowEncoder.Encode(columns, reader, buffer);
            lines.Add(Encoding.UTF8.GetString(buffer.WrittenSpan));
        }

        return lines;
    }
}
