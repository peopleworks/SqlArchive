using System.Data;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using SqlArchive.Core.Export;
using SqlArchive.Core.Format;
using SqlArchive.Core.Import;
using SqlArchive.Core.Verify;

namespace SqlArchive.IntegrationTests;

/// <summary>
/// A system-versioned table out of one database and into another, judged by the only
/// question that decides whether its history survived: <b>does <c>FOR SYSTEM_TIME AS
/// OF</c> give the same answer on both sides?</b>
/// <para>
/// Asked of the server on both sides, at three instants - one inside the history, one in
/// the gap between the last change on the source and the restore, and now - and over the
/// whole timeline with <c>FOR SYSTEM_TIME ALL</c>. The gap is the instant that used to
/// return nothing: a restore that stamped the rows with its own instant left a queryable
/// hole there. Every instant is taken from the server's clock, never from this machine's:
/// CI runs against a container whose clock is not the test runner's, and a comparison
/// between the two is a race waiting for a slow day.
/// </para>
/// <para>
/// The comparisons are source against target, through the server, as JSON the server
/// wrote. Nothing here compares this tool's output with itself.
/// </para>
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class TemporalLiveTests
{
    private readonly SqlServerFixture _server;

    public TemporalLiveTests(SqlServerFixture server) => _server = server;

    /// <summary>
    /// Two temporal tables, chosen to be awkward in every way the recipe has to survive.
    /// <c>Precio</c> has an identity, a computed column and its history in another schema
    /// - so its history carries a materialised copy of a column the parent omits.
    /// <c>Tarifa</c> has a <c>rowversion</c>, and hidden period columns at a scale that is
    /// not the default, so its period's end is <c>…59.999</c> and not <c>…59.9999999</c>.
    /// <c>Pedido</c> points a foreign key at <c>Precio</c>.
    /// </summary>
    private const string Schema = """
        CREATE TABLE dbo.Precio (
            Id        int           IDENTITY(1,1) NOT NULL CONSTRAINT PK_Precio PRIMARY KEY,
            Sku       nvarchar(20)  NOT NULL,
            Valor     decimal(19,4) NOT NULL,
            Grito     AS UPPER(Sku),
            ValidFrom datetime2(7)  GENERATED ALWAYS AS ROW START NOT NULL,
            ValidTo   datetime2(7)  GENERATED ALWAYS AS ROW END   NOT NULL,
            PERIOD FOR SYSTEM_TIME (ValidFrom, ValidTo)
        ) WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = hist.PrecioHistory));

        CREATE TABLE dbo.Tarifa (
            Id      int          NOT NULL CONSTRAINT PK_Tarifa PRIMARY KEY,
            Nombre  nvarchar(40) NOT NULL,
            Version rowversion   NOT NULL,
            Desde   datetime2(3) GENERATED ALWAYS AS ROW START HIDDEN NOT NULL,
            Hasta   datetime2(3) GENERATED ALWAYS AS ROW END   HIDDEN NOT NULL,
            PERIOD FOR SYSTEM_TIME (Desde, Hasta)
        ) WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = dbo.TarifaHistory));

        CREATE TABLE dbo.Pedido (
            Id       int NOT NULL CONSTRAINT PK_Pedido PRIMARY KEY,
            PrecioId int NOT NULL CONSTRAINT FK_Pedido_Precio REFERENCES dbo.Precio(Id)
        );
        """;

    /// <summary>What <c>AS OF</c> is asked for on <c>Precio</c>: every column, the computed one and the period included.</summary>
    private const string PrecioColumns = "Id, Sku, Valor, Grito, ValidFrom, ValidTo";

    /// <summary>
    /// And on <c>Tarifa</c>: the hidden period columns by name, and not the rowversion,
    /// whose value in another database is necessarily another one - the archive declares it
    /// omitted, in the history as in the table.
    /// </summary>
    private const string TarifaColumns = "Id, Nombre, Desde, Hasta";

    // ------------------------------------------------------------------ the acceptance test

    /// <summary>
    /// The acceptance test. A source whose rows were updated several times - one twice in
    /// one transaction, one deleted - is exported and restored into an empty database, and
    /// <c>AS OF</c> answers the same on both sides at every instant asked.
    /// </summary>
    [LiveFact]
    public async Task ARestoredTemporalTableAnswersAsOfExactlyAsTheSourceDid()
    {
        var (source, destination) = await _server.CreatePairAsync();
        var timeline = await SeedAsync(source);
        var path = TempPath();

        try
        {
            var exported = await ExportAsync(source, path);

            // The export says what it did with each history, and no longer says it skipped one.
            Assert.Contains(exported.Notices, n => n.Contains("[hist].[PrecioHistory] is the history of [dbo].[Precio]", StringComparison.Ordinal));
            Assert.DoesNotContain(exported.Notices, n => n.StartsWith("skipped", StringComparison.Ordinal));

            await AssertArchiveShapeAsync(path, source);

            // The gap has to really be a gap: the restore starts strictly after it.
            await WaitPastAsync(source, timeline.Gap);

            var result = await ImportAsync(path, destination);

            Assert.True(result.Complete, Explain(result));

            // On an empty destination the table has no period while its rows load - 040
            // created it without one - so the ordinary switch takes it. It is the
            // destination's period, not its temporality, that no switch can reach.
            Assert.Equal("swap", result.Tables.Single(t => t.Name == "Precio").Publication);
            Assert.Equal("swap", result.Tables.Single(t => t.Name == "PrecioHistory").Publication);

            var now = await ServerNowAsync(destination);

            await AssertSameTimelineAsync(source, destination, "dbo.Precio", PrecioColumns, "ValidFrom", timeline.InHistory, timeline.Gap, now);
            await AssertSameTimelineAsync(source, destination, "dbo.Tarifa", TarifaColumns, "Desde", timeline.InHistory, timeline.Gap, now);

            // Every table of the manifest - the two histories included - read back off the
            // restored database through the archive's own encoder.
            await AssertDigestsAsync(path, destination, expectedTables: 5);

            await AssertTemporalStateAsync(destination);

            // Witnesses from outside the tool: the server's own count of each history.
            Assert.Equal(
                await SqlServerFixture.CountAsync(source, "hist.PrecioHistory"),
                await SqlServerFixture.CountAsync(destination, "hist.PrecioHistory"));

            Assert.Equal(
                await SqlServerFixture.CountAsync(source, "dbo.TarifaHistory"),
                await SqlServerFixture.CountAsync(destination, "dbo.TarifaHistory"));

            // And the timeline is still live: a change made now lands in the history of
            // the restored table, which is versioning working rather than merely switched on.
            await SqlServerFixture.ExecuteAsync(destination, "UPDATE dbo.Precio SET Valor = Valor + 1 WHERE Id = 1;");

            Assert.Equal(
                await SqlServerFixture.CountAsync(source, "hist.PrecioHistory") + 1,
                await SqlServerFixture.CountAsync(destination, "hist.PrecioHistory"));
        }
        finally
        {
            Clean(path);
        }
    }

    /// <summary>
    /// Verify compares a history as a table in its own right, and the current table's hash
    /// includes its period columns: a restored timeline verifies clean, a changed history
    /// row is reported on that table and nowhere else, and so is a changed period.
    /// </summary>
    [LiveFact]
    public async Task VerifyComparesTheHistoryAndThePeriodColumns()
    {
        var (source, destination) = await _server.CreatePairAsync();
        await SeedAsync(source);
        var path = TempPath();

        try
        {
            await ExportAsync(source, path);

            // Against the database it was taken from - the audit use - and against the
            // one restored from it.
            var audit = await VerifyAsync(path, source);
            Assert.False(audit.HasDifferences, Explain(audit));

            var imported = await ImportAsync(path, destination);
            Assert.True(imported.Complete, Explain(imported));

            var restored = await VerifyAsync(path, destination);

            Assert.False(restored.HasDifferences, Explain(restored));
            Assert.True(restored.Schema!.Matches, Explain(restored));

            var history = restored.Tables.Single(t => t.Name == "PrecioHistory");

            Assert.Equal(TableOutcome.Matches, history.Outcome);
            Assert.True(history.ContentCompared);
            Assert.True(history.DatabaseRows > 0, "a history with no rows proves nothing about histories");

            // One history row altered on the restored side. Versioning has to come off to
            // do it at all - the server refuses writes to a history table otherwise.
            await SqlServerFixture.ExecuteAsync(destination, "ALTER TABLE dbo.Precio SET (SYSTEM_VERSIONING = OFF);");
            await SqlServerFixture.ExecuteAsync(destination, "UPDATE TOP (1) hist.PrecioHistory SET Valor = Valor + 100;");
            await SqlServerFixture.ExecuteAsync(
                destination, "ALTER TABLE dbo.Precio SET (SYSTEM_VERSIONING = ON (HISTORY_TABLE = hist.PrecioHistory));");

            var altered = await VerifyAsync(path, destination);

            Assert.Equal(TableOutcome.ContentDiffers, altered.Tables.Single(t => t.Name == "PrecioHistory").Outcome);
            Assert.All(altered.Tables.Where(t => t.Name != "PrecioHistory"), t => Assert.Equal(TableOutcome.Matches, t.Outcome));

            // And a period moved on a current row - the one change that only a hash over
            // the period columns can see, because every other value is untouched. Later
            // rather than earlier, which would overlap the row's last history entry and be
            // refused (13573); and HIDDEN put back, which DROP PERIOD takes away, so that
            // what differs is the content and not the shape.
            await SqlServerFixture.ExecuteAsync(destination, "ALTER TABLE dbo.Tarifa SET (SYSTEM_VERSIONING = OFF);");
            await SqlServerFixture.ExecuteAsync(destination, "ALTER TABLE dbo.Tarifa DROP PERIOD FOR SYSTEM_TIME;");
            await SqlServerFixture.ExecuteAsync(destination, "UPDATE dbo.Tarifa SET Desde = DATEADD(millisecond, 1, Desde) WHERE Id = 1;");
            await SqlServerFixture.ExecuteAsync(destination, "ALTER TABLE dbo.Tarifa ADD PERIOD FOR SYSTEM_TIME (Desde, Hasta);");
            await SqlServerFixture.ExecuteAsync(destination, "ALTER TABLE dbo.Tarifa ALTER COLUMN Desde ADD HIDDEN;");
            await SqlServerFixture.ExecuteAsync(destination, "ALTER TABLE dbo.Tarifa ALTER COLUMN Hasta ADD HIDDEN;");
            await SqlServerFixture.ExecuteAsync(
                destination, "ALTER TABLE dbo.Tarifa SET (SYSTEM_VERSIONING = ON (HISTORY_TABLE = dbo.TarifaHistory));");

            var moved = await VerifyAsync(path, destination);

            Assert.Equal(TableOutcome.ContentDiffers, moved.Tables.Single(t => t.Name == "Tarifa").Outcome);
        }
        finally
        {
            Clean(path);
        }
    }

    // ------------------------------------------------------------------ the migration

    /// <summary>
    /// The hard half: restoring over a destination that is already temporal and already has
    /// a history of its own. The destination refuses writes to its period columns and to its
    /// history table while it stands, so both are taken off and put back in one transaction
    /// per table - and afterwards <c>AS OF</c> answers as the source did, not as the
    /// destination used to.
    /// </summary>
    [LiveFact]
    public async Task AMigrationOverATemporalDestinationRestoresTheSourcesTimeline()
    {
        var (source, destination) = await _server.CreatePairAsync();
        var timeline = await SeedAsync(source);
        await SeedOwnTimelineAsync(destination);

        // A retention the destination chose. The archive knows nothing of it - SqlSchemaDiff
        // does not read it - and versioning is switched off and on again under it, so the
        // restore has to put it back itself or silently lift somebody's policy.
        await SqlServerFixture.ExecuteAsync(
            destination, "ALTER TABLE dbo.Precio SET (SYSTEM_VERSIONING = ON (HISTORY_RETENTION_PERIOD = 6 MONTHS));");

        var path = TempPath();

        try
        {
            await ExportAsync(source, path);
            await WaitPastAsync(source, timeline.Gap);

            var result = await ImportAsync(path, destination);

            Assert.True(result.Complete, Explain(result));
            Assert.Contains(result.Notices, n => n.Contains("migration", StringComparison.Ordinal));

            Assert.Equal(
                "6 MONTH",
                await ScalarStringAsync(
                    destination,
                    "SELECT CONCAT(history_retention_period, ' ', history_retention_period_unit_desc) FROM sys.tables WHERE name = N'Precio';"));

            Assert.Contains("period taken off", result.Tables.Single(t => t.Name == "Precio").Publication!, StringComparison.Ordinal);
            Assert.Contains("history of [dbo].[Precio]", result.Tables.Single(t => t.Name == "PrecioHistory").Publication!, StringComparison.Ordinal);

            var now = await ServerNowAsync(destination);

            await AssertSameTimelineAsync(source, destination, "dbo.Precio", PrecioColumns, "ValidFrom", timeline.InHistory, timeline.Gap, now);
            await AssertSameTimelineAsync(source, destination, "dbo.Tarifa", TarifaColumns, "Desde", timeline.InHistory, timeline.Gap, now);

            await AssertDigestsAsync(path, destination, expectedTables: 5);
            await AssertTemporalStateAsync(destination);

            // The foreign key the fence took off is back, enabled and trusted.
            Assert.Equal(
                0,
                Convert.ToInt32(await SqlServerFixture.ScalarAsync(
                    destination, "SELECT COUNT(*) FROM sys.foreign_keys WHERE is_disabled = 1 OR is_not_trusted = 1;")));

            // Nothing left behind: no staging table, and the destination's own history is
            // gone rather than merged - the archive's timeline replaced it.
            Assert.Equal(
                0,
                Convert.ToInt32(await SqlServerFixture.ScalarAsync(
                    destination, "SELECT COUNT(*) FROM sys.tables WHERE name LIKE N'%[_]sqlarchive[_]%';")));

            Assert.Equal(0, Convert.ToInt32(await SqlServerFixture.ScalarAsync(
                destination, "SELECT COUNT(*) FROM hist.PrecioHistory WHERE Sku = N'destino';")));

            var verified = await VerifyAsync(path, destination);
            Assert.False(verified.HasDifferences, Explain(verified));
        }
        finally
        {
            Clean(path);
        }
    }

    /// <summary>
    /// <c>--schema-only</c> and then <c>--data-only</c>: the first runs every phase, so by the
    /// time the rows come the tables already have their periods and are versioned, and the
    /// second has to take both off and put them back - with no diff and no finalize phase
    /// around it to help.
    /// </summary>
    [LiveFact]
    public async Task SchemaOnlyAndThenDataOnlyRestoreTheTimeline()
    {
        var (source, destination) = await _server.CreatePairAsync();
        var timeline = await SeedAsync(source);
        var path = TempPath();

        try
        {
            await ExportAsync(source, path);
            await WaitPastAsync(source, timeline.Gap);

            await ImportAsync(path, destination, mode: ImportMode.SchemaOnly);

            Assert.Equal(
                2,
                Convert.ToInt32(await SqlServerFixture.ScalarAsync(
                    destination, "SELECT COUNT(*) FROM sys.tables WHERE temporal_type = 2;")));

            var data = await ImportAsync(path, destination, mode: ImportMode.DataOnly);

            Assert.True(data.Complete, Explain(data));

            var now = await ServerNowAsync(destination);

            await AssertSameTimelineAsync(source, destination, "dbo.Precio", PrecioColumns, "ValidFrom", timeline.InHistory, timeline.Gap, now);
            await AssertSameTimelineAsync(source, destination, "dbo.Tarifa", TarifaColumns, "Desde", timeline.InHistory, timeline.Gap, now);
            await AssertDigestsAsync(path, destination, expectedTables: 5);
            await AssertTemporalStateAsync(destination);
        }
        finally
        {
            Clean(path);
        }
    }

    /// <summary>
    /// A failure half way through the migration's transaction - after versioning is off and
    /// the period is gone, when the rows are being replaced - leaves the destination exactly
    /// as it was: the same rows in both tables, the same timeline, the period, the
    /// versioning, and the hidden flags.
    /// </summary>
    /// <remarks>
    /// The failure is the destination's own: a trigger that refuses deletes by rolling the
    /// transaction back, which is a thing real databases have and the archive knows nothing
    /// about. It fires on the <c>DELETE</c>, after the period has been dropped - and since
    /// it ends the transaction on the server's side, it is also what shows that the
    /// server's reason survives a rollback that has nothing left to do.
    /// <para>
    /// The restore is not allowed to carry on past the failure. Carrying on runs the
    /// finalize phase, and the finalize phase adds the period and turns versioning on: it
    /// would heal the very damage this is looking for, and a test that looked after it
    /// passed with the period dropped outside the transaction. Measured, by mutating it.
    /// </para>
    /// </remarks>
    [LiveFact]
    public async Task AFailureHalfWayThroughTheMigrationLeavesTheTimelineExactlyAsItWas()
    {
        var (source, destination) = await _server.CreatePairAsync();
        await SeedAsync(source);
        await SeedOwnTimelineAsync(destination);

        await SqlServerFixture.ExecuteAsync(
            destination,
            """
            CREATE TRIGGER dbo.Precio_NoSeBorra ON dbo.Precio AFTER DELETE AS
            BEGIN
                ROLLBACK TRANSACTION;
                THROW 50001, N'Precio no se borra', 1;
            END;
            """);

        var path = TempPath();

        try
        {
            await ExportAsync(source, path);

            var before = await StateAsync(destination);

            // Pedido is left out so the only thing that fails is the one this is about: its
            // archived rows point at Precio rows the refusal keeps out.
            var failed = await Assert.ThrowsAnyAsync<Exception>(() => ImportAsync(path, destination, exclude: ["dbo.Pedido"]));

            // The server's reason, not the driver's complaint about a transaction that had
            // already ended.
            Assert.Contains("no se borra", failed.Message, StringComparison.Ordinal);

            var after = await StateAsync(destination);

            Assert.Equal(before.Precio, after.Precio);
            Assert.Equal(before.PrecioHistory, after.PrecioHistory);
            Assert.Equal(before.Catalog, after.Catalog);
        }
        finally
        {
            Clean(path);
        }
    }

    /// <summary>
    /// The refusal SQL Server makes at the very last statement: a history whose periods
    /// overlap is not adopted (13573), which ends the transaction on the server's side.
    /// The unit says what the refusal means, and every statement before it - versioning
    /// off, the period dropped, both tables replaced, the period added - is undone.
    /// </summary>
    [LiveFact]
    public async Task AHistoryTheServerWillNotAdoptIsRolledBackWhole()
    {
        var (source, destination) = await _server.CreatePairAsync();
        await SeedAsync(source);
        await SeedOwnTimelineAsync(destination);
        var path = TempPath();

        try
        {
            await ExportAsync(source, path);
            await OverlapAHistoryRowAsync(path);

            var before = await StateAsync(destination);

            // Not carried on past, for the reason the previous test gives: what is compared
            // below is what the rollback left, not what a later phase repaired.
            var refused = await Assert.ThrowsAsync<ImportException>(() => ImportAsync(path, destination, exclude: ["dbo.Pedido"]));

            Assert.Contains("overlap", refused.Message, StringComparison.Ordinal);
            Assert.Contains("rolled back", refused.Message, StringComparison.Ordinal);
            Assert.IsType<SqlException>(refused.InnerException);

            var after = await StateAsync(destination);

            Assert.Equal(before.Precio, after.Precio);
            Assert.Equal(before.PrecioHistory, after.PrecioHistory);
            Assert.Equal(before.Catalog, after.Catalog);
        }
        finally
        {
            Clean(path);
        }
    }

    /// <summary>
    /// A table and its history are one timeline. When the exact guard refuses one of them,
    /// the other passes its own guard and is still not published: a history that belongs
    /// to neither the archive's table nor the destination's would be the worst of both.
    /// </summary>
    [LiveFact]
    public async Task WhenTheGuardRefusesAHistoryItsTableIsNotPublishedEither()
    {
        var (source, destination) = await _server.CreatePairAsync();
        await SeedAsync(source);
        await SeedOwnTimelineAsync(destination);
        var path = TempPath();

        try
        {
            await ExportAsync(source, path);

            // One value of the history, and the manifest left as it was: legal JSON, the
            // same number of rows, and only the hash can tell.
            Tamper(path, ArchiveFormat.DataEntry("hist", "PrecioHistory"), text => text.Replace("\"Sku\":\"a\"", "\"Sku\":\"z\"", StringComparison.Ordinal));

            var before = await StateAsync(destination);

            var result = await ImportAsync(path, destination, continueOnError: true, exclude: ["dbo.Pedido"]);

            var history = result.Tables.Single(t => t.Name == "PrecioHistory");
            var precio = result.Tables.Single(t => t.Name == "Precio");

            Assert.True(history.Outcome == ImportTableOutcome.Refused, $"{history.Outcome}: {history.Reason}");
            Assert.True(precio.Outcome == ImportTableOutcome.Refused, $"{precio.Outcome}: {precio.Reason}");
            Assert.Contains("one timeline", precio.Reason!, StringComparison.Ordinal);

            Assert.Equal(ImportTableOutcome.Published, result.Tables.Single(t => t.Name == "Tarifa").Outcome);

            var after = await StateAsync(destination);

            Assert.Equal(before.Precio, after.Precio);
            Assert.Equal(before.PrecioHistory, after.PrecioHistory);
            Assert.Equal(before.Catalog, after.Catalog);
        }
        finally
        {
            Clean(path);
        }
    }

    // ------------------------------------------------------------------ the clock

    /// <summary>
    /// A restore server whose clock is behind the source's, simulated by the one thing it
    /// produces: an archived period that starts after the destination's now. SQL Server
    /// refuses to add such a period (13542). Over an existing destination that refusal
    /// comes inside the transaction, so it is rolled back and says why.
    /// </summary>
    [LiveFact]
    public async Task AClockBehindTheSourcesIsNamedAndTheMigrationIsRolledBack()
    {
        var (source, destination) = await _server.CreatePairAsync();
        await SeedAsync(source);
        await SeedOwnTimelineAsync(destination);
        var path = TempPath();

        try
        {
            await ExportAsync(source, path);
            await StartInTheFutureAsync(path);

            var before = await StateAsync(destination);

            // Not carried on past: the finalize phase would put a dropped period back and
            // hide exactly what this is looking at.
            var refused = await Assert.ThrowsAsync<ImportException>(() => ImportAsync(path, destination, exclude: ["dbo.Pedido"]));

            Assert.Contains("clock", refused.Message, StringComparison.Ordinal);
            Assert.Contains("rolled back", refused.Message, StringComparison.Ordinal);

            var after = await StateAsync(destination);

            Assert.Equal(before.Precio, after.Precio);
            Assert.Equal(before.PrecioHistory, after.PrecioHistory);
            Assert.Equal(before.Catalog, after.Catalog);
        }
        finally
        {
            Clean(path);
        }
    }

    /// <summary>
    /// The same archive into an empty destination: there the period is added by the
    /// finalize phase after the rows are in, and the refusal comes from it - naming the
    /// clock rather than leaving the operator with the rule alone.
    /// </summary>
    [LiveFact]
    public async Task AClockBehindTheSourcesIsNamedOnAFreshRestore()
    {
        var (source, destination) = await _server.CreatePairAsync();
        await SeedAsync(source);
        var path = TempPath();

        try
        {
            await ExportAsync(source, path);
            await StartInTheFutureAsync(path);

            var refused = await Assert.ThrowsAsync<ImportException>(() => ImportAsync(path, destination));

            Assert.Contains("clock", refused.Message, StringComparison.Ordinal);
            Assert.Contains("090_finalize.sql", refused.Message, StringComparison.Ordinal);
        }
        finally
        {
            Clean(path);
        }
    }

    // ------------------------------------------------------------------ selection and consistency

    /// <summary>
    /// A filtered export of a temporal table brings its history with it, because the
    /// history is now a real table of the snapshot for the selection to keep - and says so.
    /// </summary>
    [LiveFact]
    public async Task AFilteredExportOfATemporalTableBringsItsHistory()
    {
        var source = await _server.CreateDatabaseAsync();
        await SeedAsync(source);
        var path = TempPath();

        try
        {
            var exported = await new DatabaseExporter(new ExportOptions
            {
                ConnectionString = source,
                IncludeTables = ["dbo.Precio"]
            }).ExportAsync(path);

            using var archive = await ArchiveReader.OpenAsync(path);

            Assert.Equal(
                ["[dbo].[Precio]", "[hist].[PrecioHistory]"],
                archive.Manifest.Tables.Select(t => t.Identifier).Order(StringComparer.Ordinal).ToArray());

            Assert.Equal(
                await SqlServerFixture.CountAsync(source, "hist.PrecioHistory"),
                (int)archive.Manifest.Table("hist", "PrecioHistory")!.RowCount);

            Assert.Contains(exported.Notices, n => n.Contains("archived with it even though the table filters left it out", StringComparison.Ordinal));
        }
        finally
        {
            Clean(path);
        }
    }

    /// <summary>
    /// Under a single point in time the history is read at the same instant as its table:
    /// an update made after the instant moves a row into the source's history, and neither
    /// the table's rows nor the history's in the archive show it.
    /// </summary>
    [LiveFact]
    public async Task UnderAPointInTimeTheHistoryIsReadAtTheSameInstantAsItsTable()
    {
        var source = await _server.CreateDatabaseAsync();
        await SeedAsync(source);
        var path = TempPath();

        var historyAtTheInstant = await SqlServerFixture.CountAsync(source, "hist.PrecioHistory");

        try
        {
            var result = await new DatabaseExporter(new ExportOptions
            {
                ConnectionString = source,
                Consistent = true,
                OnConsistencyEstablished = (_, _) =>
                    SqlServerFixture.ExecuteAsync(source, "UPDATE dbo.Precio SET Valor = Valor + 7;")
            }).ExportAsync(path);

            Assert.Equal(ArchiveConsistency.Snapshot, result.Consistency);

            using var archive = await ArchiveReader.OpenAsync(path);

            Assert.Equal(historyAtTheInstant, (int)archive.Manifest.Table("hist", "PrecioHistory")!.RowCount);
            Assert.True(
                await SqlServerFixture.CountAsync(source, "hist.PrecioHistory") > historyAtTheInstant,
                "the update after the instant has to have written history, or this proves nothing");
        }
        finally
        {
            Clean(path);
        }
    }

    // ------------------------------------------------------------------ the seed

    /// <summary>The instants the timeline is asked about, as the source's own clock gave them.</summary>
    private sealed record Timeline(DateTime InHistory, DateTime Gap);

    /// <summary>
    /// The schema, then a timeline with real history in it: inserts, an update, an instant
    /// taken, two more updates of one row inside one transaction, a delete, and the instant
    /// after the last change. Each step is separated by a pause long enough for any clock
    /// SQL Server runs on to tick.
    /// </summary>
    private static async Task<Timeline> SeedAsync(string database)
    {
        await SqlServerFixture.ExecuteAsync(database, "CREATE SCHEMA hist;");
        await SqlServerFixture.ExecuteAsync(database, Schema);

        await SqlServerFixture.ExecuteAsync(
            database,
            """
            INSERT INTO dbo.Precio (Sku, Valor) VALUES (N'a', 10), (N'b', 20), (N'c', 30);
            INSERT INTO dbo.Tarifa (Id, Nombre) VALUES (1, N'base'), (2, N'noche');
            INSERT INTO dbo.Pedido (Id, PrecioId) VALUES (1, 1), (2, 2);
            """);

        await PauseAsync(database);

        await SqlServerFixture.ExecuteAsync(
            database,
            """
            UPDATE dbo.Precio SET Valor = Valor + 1 WHERE Id <= 2;
            UPDATE dbo.Tarifa SET Nombre = Nombre + N' v2' WHERE Id = 1;
            """);

        var inHistory = await ServerNowAsync(database);

        await PauseAsync(database);

        // Two updates of one row in one transaction leave a history row whose period is
        // empty - it starts and ends at the same instant - which the consistency check
        // SQL Server runs on adoption has to accept, because SQL Server wrote it.
        await SqlServerFixture.ExecuteAsync(
            database,
            """
            BEGIN TRANSACTION;
                UPDATE dbo.Precio SET Valor = Valor * 2 WHERE Id = 1;
                UPDATE dbo.Precio SET Valor = Valor + 0.5 WHERE Id = 1;
            COMMIT;
            DELETE FROM dbo.Pedido WHERE PrecioId = 3;
            DELETE FROM dbo.Precio WHERE Id = 3;
            UPDATE dbo.Tarifa SET Nombre = N'noche v2' WHERE Id = 2;
            """);

        await PauseAsync(database);

        var gap = await ServerNowAsync(database);

        return new Timeline(inHistory, gap);
    }

    /// <summary>
    /// The same shape on the destination, with a timeline of its own that has nothing in
    /// common with the source's - so that anything of it surviving the restore is visible.
    /// </summary>
    private static async Task SeedOwnTimelineAsync(string database)
    {
        await SqlServerFixture.ExecuteAsync(database, "CREATE SCHEMA hist;");
        await SqlServerFixture.ExecuteAsync(database, Schema);

        await SqlServerFixture.ExecuteAsync(
            database,
            """
            INSERT INTO dbo.Precio (Sku, Valor) VALUES (N'destino', 1);
            INSERT INTO dbo.Tarifa (Id, Nombre) VALUES (9, N'del destino');
            INSERT INTO dbo.Pedido (Id, PrecioId) VALUES (7, 1);
            """);

        await PauseAsync(database);

        await SqlServerFixture.ExecuteAsync(
            database,
            """
            UPDATE dbo.Precio SET Valor = 2;
            UPDATE dbo.Tarifa SET Nombre = N'del destino v2';
            """);
    }

    // ------------------------------------------------------------------ what is asserted

    /// <summary>
    /// <c>AS OF</c> at each instant, and the whole timeline, asked of the server on both
    /// sides and compared as the JSON the server wrote.
    /// </summary>
    private static async Task AssertSameTimelineAsync(
        string source,
        string destination,
        string table,
        string columns,
        string periodStart,
        params DateTime[] instants)
    {
        foreach(var instant in instants)
        {
            var expected = await AsOfAsync(source, table, columns, instant);
            var actual = await AsOfAsync(destination, table, columns, instant);

            Assert.True(expected != "[]", $"{table} AS OF {instant:O} is empty on the source, so the comparison proves nothing");
            Assert.Equal(expected, actual);
        }

        Assert.Equal(await AllAsync(source, table, columns, periodStart), await AllAsync(destination, table, columns, periodStart));
    }

    /// <summary>Every table of the manifest hashes, on the restored database, to what the manifest declares.</summary>
    private static async Task AssertDigestsAsync(string archivePath, string destination, int expectedTables)
    {
        using var archive = await ArchiveReader.OpenAsync(archivePath);
        var manifest = archive.Manifest;
        var snapshot = manifest.Schema!;

        Assert.Equal(expectedTables, manifest.Tables.Count);

        foreach(var entry in manifest.Tables)
        {
            var model = SnapshotSelection.Tables(snapshot).Single(t =>
                string.Equals(t.Schema, entry.Schema, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(t.Name, entry.Name, StringComparison.OrdinalIgnoreCase));

            var digest = await LiveTableDigest.ComputeAsync(
                destination, entry.Schema, entry.Name, ArchiveColumns.For(model, snapshot.Types));

            Assert.True(entry.RowCount == digest.Rows, $"{entry.Identifier}: {entry.RowCount} declared, {digest.Rows} restored");
            Assert.True(entry.RowHash == digest.RowHash, $"{entry.Identifier}: the restored rows do not hash to the manifest's");
        }
    }

    /// <summary>What the archive says about the two tables and their histories, and how its phases put them back.</summary>
    private static async Task AssertArchiveShapeAsync(string archivePath, string source)
    {
        using var archive = await ArchiveReader.OpenAsync(archivePath);
        var manifest = archive.Manifest;

        Assert.Equal(
            ["dbo.Pedido", "dbo.Precio", "dbo.Tarifa", "dbo.TarifaHistory", "hist.PrecioHistory"],
            manifest.Tables.Select(t => $"{t.Schema}.{t.Name}").Order(StringComparer.Ordinal).ToArray());

        // What each leaves out, and nothing more. The period columns are not in these lists
        // any more; the history of Precio leaves nothing out, because the computed column
        // its parent omits is a real one there; and a rowversion is omitted from the history
        // just as from the table, since SQL Server keeps it as a timestamp nobody can write.
        Assert.Equal(new Dictionary<string, string> { ["Grito"] = "computed" }, manifest.Table("dbo", "Precio")!.OmittedColumns);
        Assert.Empty(manifest.Table("hist", "PrecioHistory")!.OmittedColumns);
        Assert.Equal(new Dictionary<string, string> { ["Version"] = "rowversion" }, manifest.Table("dbo", "Tarifa")!.OmittedColumns);
        Assert.Equal(new Dictionary<string, string> { ["Version"] = "rowversion" }, manifest.Table("dbo", "TarifaHistory")!.OmittedColumns);

        Assert.Equal(
            await SqlServerFixture.CountAsync(source, "hist.PrecioHistory"),
            (int)manifest.Table("hist", "PrecioHistory")!.RowCount);

        // The history's shape is the catalog's, not the parent's by rule: Grito is a plain,
        // nullable column there.
        var history = SnapshotSelection.Tables(manifest.Schema!).Single(t => t.Name == "PrecioHistory");
        Assert.False(history.Columns.Single(c => c.Name == "Grito").IsComputed);
        Assert.False(history.Columns.Single(c => c.Name == "Id").IsIdentity);

        // 040 creates the table without its period and the history as an ordinary table;
        // 090 adds the period - HIDDEN with it where it was - before versioning adopts the
        // history.
        var tables = await archive.ReadTextAsync("schema/040_tables.sql");
        var precio = Batch(tables, "CREATE TABLE [dbo].[Precio]");

        Assert.DoesNotContain("PERIOD FOR SYSTEM_TIME", precio, StringComparison.Ordinal);
        Assert.DoesNotContain("GENERATED ALWAYS", precio, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE [hist].[PrecioHistory]", tables, StringComparison.Ordinal);

        var finalize = await archive.ReadTextAsync("schema/090_finalize.sql");

        var period = finalize.IndexOf("ALTER TABLE [dbo].[Precio] ADD PERIOD FOR SYSTEM_TIME", StringComparison.Ordinal);
        var versioning = finalize.IndexOf("ALTER TABLE [dbo].[Precio] SET (SYSTEM_VERSIONING = ON (HISTORY_TABLE = [hist].[PrecioHistory]))", StringComparison.Ordinal);

        Assert.True(period >= 0 && versioning > period, "090 has to add the period before it turns versioning on");
        Assert.Contains("ALTER TABLE [dbo].[Tarifa] ALTER COLUMN [Desde] ADD HIDDEN;", finalize, StringComparison.Ordinal);
    }

    /// <summary>The restored tables are versioned against the right histories, and hidden columns came back hidden.</summary>
    private static async Task AssertTemporalStateAsync(string destination)
    {
        Assert.Equal(
            "PrecioHistory|TarifaHistory",
            await ScalarStringAsync(
                destination,
                """
                SELECT STRING_AGG(OBJECT_NAME(history_table_id), '|') WITHIN GROUP (ORDER BY name)
                FROM sys.tables WHERE temporal_type = 2;
                """));

        Assert.Equal(
            "Desde:1:1|Hasta:1:2",
            await ScalarStringAsync(
                destination,
                """
                SELECT STRING_AGG(CONCAT(name, ':', CONVERT(int, is_hidden), ':', generated_always_type), '|')
                       WITHIN GROUP (ORDER BY column_id)
                FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.Tarifa') AND generated_always_type <> 0;
                """));
    }

    /// <summary>Everything about the destination's Precio that a failed restore must not have moved.</summary>
    private sealed record TimelineState(string Precio, string PrecioHistory, string Catalog);

    private static async Task<TimelineState> StateAsync(string database) => new(
        await AllAsync(database, "dbo.Precio", PrecioColumns, "ValidFrom"),
        await ScalarStringAsync(
            database,
            "SELECT (SELECT Id, Sku, Valor, Grito, ValidFrom, ValidTo FROM hist.PrecioHistory ORDER BY Id, ValidFrom, ValidTo FOR JSON PATH, INCLUDE_NULL_VALUES);")
            ?? "[]",
        await ScalarStringAsync(
            database,
            """
            SELECT CONCAT(
                (SELECT STRING_AGG(CONCAT(t.name, ':', t.temporal_type, ':', OBJECT_NAME(t.history_table_id)), '|')
                        WITHIN GROUP (ORDER BY t.name)
                 FROM sys.tables AS t WHERE t.temporal_type <> 0),
                ' / ',
                (SELECT STRING_AGG(CONCAT(OBJECT_NAME(c.object_id), '.', c.name, ':', CONVERT(int, c.is_hidden), ':', c.generated_always_type), '|')
                        WITHIN GROUP (ORDER BY OBJECT_NAME(c.object_id), c.column_id)
                 FROM sys.columns AS c
                 WHERE c.object_id IN (OBJECT_ID(N'dbo.Precio'), OBJECT_ID(N'dbo.Tarifa'))),
                ' / ',
                (SELECT COUNT(*) FROM sys.periods));
            """)
            ?? string.Empty);

    // ------------------------------------------------------------------ the server's answers

    private static async Task<string> AsOfAsync(string database, string table, string columns, DateTime instant)
    {
        await using var connection = new SqlConnection(database);
        await connection.OpenAsync();

        await using var command = new SqlCommand(
            $"SELECT (SELECT {columns} FROM {table} FOR SYSTEM_TIME AS OF @at ORDER BY Id FOR JSON PATH, INCLUDE_NULL_VALUES);",
            connection);

        command.Parameters.Add(new SqlParameter("@at", SqlDbType.DateTime2) { Scale = 7, Value = instant });

        return await command.ExecuteScalarAsync() as string ?? "[]";
    }

    private static async Task<string> AllAsync(string database, string table, string columns, string periodStart) =>
        await ScalarStringAsync(
            database,
            $"SELECT (SELECT {columns} FROM {table} FOR SYSTEM_TIME ALL ORDER BY Id, {periodStart} FOR JSON PATH, INCLUDE_NULL_VALUES);")
        ?? "[]";

    /// <summary>The server's own clock. Never this machine's: see the class comment.</summary>
    private static async Task<DateTime> ServerNowAsync(string database) =>
        (DateTime)(await SqlServerFixture.ScalarAsync(database, "SELECT SYSUTCDATETIME();"))!;

    /// <summary>Lets the server's clock move on by more than any clock it runs on ticks by.</summary>
    private static Task PauseAsync(string database) =>
        SqlServerFixture.ExecuteAsync(database, "WAITFOR DELAY '00:00:00.050';");

    /// <summary>Waits, on the server, until its clock is strictly past the given instant.</summary>
    private static async Task WaitPastAsync(string database, DateTime instant)
    {
        while(await ServerNowAsync(database) <= instant)
            await PauseAsync(database);

        await PauseAsync(database);
    }

    // ------------------------------------------------------------------ the archive, doctored

    /// <summary>
    /// Moves the start of one current row of Precio a long way into the future and seals the
    /// archive again - row hash and file hash - so that it passes the exact guard and reaches
    /// the server exactly as an archive taken from a source with a fast clock would.
    /// </summary>
    private static Task StartInTheFutureAsync(string archivePath) =>
        ResealAsync(archivePath, "dbo", "Precio", text =>
            new Regex("\"ValidFrom\":\"[^\"]+\"").Replace(text, "\"ValidFrom\":\"2999-01-01T00:00:00\"", 1));

    /// <summary>
    /// Stretches one of Precio's history rows back over the one before it, for the same
    /// key, and seals the archive again: a history SQL Server will not adopt (13573), in an
    /// archive the exact guard lets through.
    /// </summary>
    private static Task OverlapAHistoryRowAsync(string archivePath) =>
        ResealAsync(archivePath, "hist", "PrecioHistory", text =>
        {
            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();

            // The two earliest history rows of Id 1 that are not the empty period two
            // updates in one transaction leave behind.
            var spans = lines
                .Select((line, index) => (Line: line, Index: index))
                .Where(x => x.Line.StartsWith("{\"Id\":1,", StringComparison.Ordinal))
                .Select(x => (x.Index, From: Value(x.Line, "ValidFrom"), To: Value(x.Line, "ValidTo")))
                .Where(x => x.From != x.To)
                .OrderBy(x => x.From, StringComparer.Ordinal)
                .Take(2)
                .ToList();

            Assert.Equal(2, spans.Count);

            // The second now starts where the first does, and so overlaps all of it.
            lines[spans[1].Index] = lines[spans[1].Index].Replace(
                $"\"ValidFrom\":\"{spans[1].From}\"", $"\"ValidFrom\":\"{spans[0].From}\"", StringComparison.Ordinal);

            return string.Join('\n', lines) + "\n";
        });

    private static string Value(string line, string column) =>
        Regex.Match(line, $"\"{column}\":\"(?<v>[^\"]+)\"").Groups["v"].Value;

    /// <summary>Rewrites one entry and leaves the manifest alone - which is what a corrupted file is.</summary>
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

        using var writer = new StreamWriter(zip.CreateEntry(entryName).Open(), new UTF8Encoding(false));
        writer.Write(edited);
    }

    private static async Task ResealAsync(string archivePath, string schema, string table, Func<string, string> edit)
    {
        ArchiveManifest manifest;

        using(var archive = await ArchiveReader.OpenAsync(archivePath))
            manifest = archive.Manifest;

        var entry = manifest.Table(schema, table)!;
        var dataEntry = entry.DataFiles.Single();

        using var zip = ZipFile.Open(archivePath, ZipArchiveMode.Update);

        var data = zip.GetEntry(dataEntry)!;
        string text;

        using(var reader = new StreamReader(data.Open(), new UTF8Encoding(false)))
            text = reader.ReadToEnd();

        var edited = edit(text);
        Assert.NotEqual(text, edited);

        var hash = new RowHash.Accumulator();

        foreach(var line in edited.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            hash.AddRow(Encoding.UTF8.GetBytes(line));

        var bytes = new UTF8Encoding(false).GetBytes(edited);

        entry.RowHash = hash.Value;
        entry.FileHashes[dataEntry] = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));

        data.Delete();

        await using(var stream = zip.CreateEntry(dataEntry).Open())
            await stream.WriteAsync(bytes);

        zip.GetEntry(ArchiveFormat.ManifestEntry)!.Delete();

        await using(var stream = zip.CreateEntry(ArchiveFormat.ManifestEntry).Open())
            await stream.WriteAsync(ArchiveFormat.Utf8.GetBytes(ManifestSerializer.Serialize(manifest)));
    }

    // ------------------------------------------------------------------ plumbing

    private static Task<ExportResult> ExportAsync(string source, string path) =>
        new DatabaseExporter(new ExportOptions { ConnectionString = source }).ExportAsync(path);

    private static Task<ImportResult> ImportAsync(
        string archivePath,
        string destination,
        bool continueOnError = false,
        string[]? exclude = null,
        ImportMode mode = ImportMode.Migrate) =>
        new DatabaseImporter(new ImportOptions
        {
            ConnectionString = destination,
            Mode = mode,
            ContinueOnError = continueOnError,
            ExcludeTables = exclude ?? []
        }).ImportAsync(archivePath);

    private static Task<VerifyReport> VerifyAsync(string path, string connectionString) =>
        new ArchiveVerifier(new VerifyOptions { ConnectionString = connectionString }).VerifyAsync(path);

    /// <summary>The one batch of a phase file that starts with the given text.</summary>
    private static string Batch(string phase, string start)
    {
        var at = phase.IndexOf(start, StringComparison.Ordinal);
        Assert.True(at >= 0, $"no batch starting '{start}'");

        var end = phase.IndexOf("\nGO", at, StringComparison.Ordinal);
        return end < 0 ? phase[at..] : phase[at..end];
    }

    private static async Task<string?> ScalarStringAsync(string connectionString, string sql) =>
        await SqlServerFixture.ScalarAsync(connectionString, sql) as string;

    private static string Explain(ImportResult result) =>
        string.Join(
            Environment.NewLine,
            result.Tables
                .Where(t => t.Outcome is ImportTableOutcome.Refused or ImportTableOutcome.Failed)
                .Select(t => $"{t.Identifier}: {t.Reason}")
                .Concat(result.Notices));

    private static string Explain(VerifyReport report)
    {
        var lines = new List<string> { $"{report.Archive} against {report.Database ?? "nothing"}:" };

        if(report.Schema is { } schema)
        {
            lines.Add("  only in the archive: " + string.Join(", ", schema.OnlyInArchive));
            lines.Add("  only in the database: " + string.Join(", ", schema.OnlyInDatabase));
            lines.Add("  differing: " + string.Join(", ", schema.Differing));
        }

        foreach(var table in report.Tables.Where(t => t.Outcome != TableOutcome.Matches))
            lines.Add($"  {table.Identifier}: {table.Outcome} - {string.Join(" ", table.Differences)}");

        lines.AddRange(report.Notices.Select(n => "  notice: " + n));

        return string.Join(Environment.NewLine, lines);
    }

    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"sqlarchive-temporal-{Guid.NewGuid():N}.sqlarchive");

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
        }
    }
}
