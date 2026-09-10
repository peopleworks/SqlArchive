using Spectre.Console;
using Spectre.Console.Cli;
using SqlArchive.Cli.Commands;
using SqlArchive.Core.Format;

namespace SqlArchive.IntegrationTests;

/// <summary>
/// The <c>export</c> verb itself, run against a real server - not
/// <c>SqlArchive.Core.Export.DatabaseExporter</c> directly, which is what
/// <c>ExportLiveTests</c> already covers, but the command an operator actually types:
/// parsed by Spectre, validated by <see cref="ExportCommand.Settings"/>, and only then
/// handed to the engine.
/// <para>
/// <c>SqlArchive.Cli</c>'s <c>Program</c> is internal and this project has no
/// <c>InternalsVisibleTo</c> into it - that belongs to <c>SqlArchive.Core.Tests</c>,
/// where <c>CliTests</c> lives. So the verb is driven here through a
/// <c>CommandApp&lt;ExportCommand&gt;</c> built in this file: the same parser, the same
/// <c>Settings.Validate()</c>, the same <see cref="ExportCommand.ExecuteAsync"/> an
/// operator's own invocation would run, just without the other three verbs registered
/// beside it.
/// </para>
/// <para>
/// Every assertion that matters is checked against the server, the same idiom
/// <c>ExportLiveTests</c> uses: a manifest that only agrees with itself proves nothing.
/// </para>
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class ExportCliLiveTests
{
    // SqlArchive.Cli.ExitCodes is internal, and this project has no
    // InternalsVisibleTo into it - see the class doc comment. These are its
    // Ok and Failed values, restated rather than imported.
    private const int ExitOk = 0;
    private const int ExitFailed = 1;

    private readonly SqlServerFixture _server;

    public ExportCliLiveTests(SqlServerFixture server) => _server = server;

    /// <summary>Two tables and a few rows - enough for a filter or an exclusion to have something to prove.</summary>
    private const string Schema = """
        CREATE TABLE dbo.Widget (
            Id     int           IDENTITY(1,1) NOT NULL CONSTRAINT PK_Widget PRIMARY KEY,
            Name   nvarchar(50)  NOT NULL,
            Status nvarchar(10)  NOT NULL
        );

        CREATE TABLE dbo.Note (
            Id      int            IDENTITY(1,1) NOT NULL CONSTRAINT PK_Note PRIMARY KEY,
            Message nvarchar(100)  NOT NULL
        );
        """;

    [LiveFact]
    public async Task AWholeDatabaseExportsWithTheCountsTheServerHas()
    {
        var source = await SeedAsync();
        var path = ExportLiveTests.TempPath();

        try
        {
            var (code, _) = await RunAsync("--source", source, "--out", path);

            Assert.Equal(ExitOk, code);

            using var archive = await ArchiveReader.OpenAsync(path);
            var manifest = archive.Manifest;

            Assert.Equal(2, manifest.Tables.Count);

            foreach(var table in manifest.Tables)
            {
                var counted = await SqlServerFixture.ScalarAsync(
                    source, $"SELECT COUNT_BIG(*) FROM [{table.Schema}].[{table.Name}];");

                Assert.Equal(Convert.ToInt64(counted), table.RowCount);
            }
        }
        finally
        {
            ExportLiveTests.Clean(path);
        }
    }

    /// <summary>
    /// --table really restricts the archive to the glob, on the server's own table list
    /// and not on what the export happens to have iterated over.
    /// </summary>
    [LiveFact]
    public async Task ATableFilterReallyRestrictsTheArchive()
    {
        var source = await SeedAsync();
        var path = ExportLiveTests.TempPath();

        try
        {
            var (code, _) = await RunAsync("--source", source, "--out", path, "--table", "dbo.Widget");

            Assert.Equal(ExitOk, code);

            using var archive = await ArchiveReader.OpenAsync(path);

            Assert.Single(archive.Manifest.Tables);
            Assert.Equal("Widget", archive.Manifest.Tables[0].Name);
            Assert.Null(archive.Manifest.Table("dbo", "Note"));
        }
        finally
        {
            ExportLiveTests.Clean(path);
        }
    }

    /// <summary>--schema-only really leaves the rows out, and the manifest says so rather than reporting zero rows silently.</summary>
    [LiveFact]
    public async Task SchemaOnlyReallyLeavesTheDataOut()
    {
        var source = await SeedAsync();
        var path = ExportLiveTests.TempPath();

        try
        {
            var (code, _) = await RunAsync("--source", source, "--out", path, "--schema-only");

            Assert.Equal(ExitOk, code);

            using var archive = await ArchiveReader.OpenAsync(path);

            Assert.NotEmpty(archive.Manifest.Tables);

            foreach(var table in archive.Manifest.Tables)
            {
                Assert.True(table.DataSkipped);
                Assert.Empty(table.DataFiles);
                Assert.Null(table.RowHash);
            }

            Assert.DoesNotContain(archive.Entries, e => e.Name.StartsWith("data/", StringComparison.Ordinal));

            // The schema itself is still there - --schema-only is not --exclude.
            Assert.Contains("[Widget]", await archive.ReadTextAsync("schema/040_tables.sql"), StringComparison.Ordinal);
        }
        finally
        {
            ExportLiveTests.Clean(path);
        }
    }

    /// <summary>
    /// A predicate containing its own '=' - the common case, since most predicates are an
    /// equality - lands in the manifest's rowFilter whole, and the row count it produces
    /// is checked against the same predicate run straight against the server.
    /// </summary>
    [LiveFact]
    public async Task AWhereClauseWithItsOwnEqualsSignLandsInTheRowFilterWhole()
    {
        var source = await SeedAsync();
        var path = ExportLiveTests.TempPath();

        try
        {
            var (code, _) = await RunAsync(
                "--source", source, "--out", path, "--where", "dbo.Widget=Status='Active'");

            Assert.Equal(ExitOk, code);

            using var archive = await ArchiveReader.OpenAsync(path);
            var widget = archive.Manifest.Table("dbo", "Widget")!;

            var expected = Convert.ToInt64(
                await SqlServerFixture.ScalarAsync(source, "SELECT COUNT_BIG(*) FROM dbo.Widget WHERE Status='Active';"));

            Assert.Equal("Status='Active'", widget.RowFilter);
            Assert.Equal(expected, widget.RowCount);
            Assert.True(expected > 0 && expected < 12, "the filter has to actually remove some rows and keep some, or this proves nothing");

            // Untouched by a filter keyed to the other table.
            Assert.Null(archive.Manifest.Table("dbo", "Note")!.RowFilter);
        }
        finally
        {
            ExportLiveTests.Clean(path);
        }
    }

    /// <summary>
    /// --consistent reaches <c>ConsistencySession</c> rather than being dropped on the
    /// floor: the manifest ends up with a mode other than the table-by-table default.
    /// This server grants CREATE DATABASE to the login the fixture uses (it creates and
    /// drops scratch databases itself), so the database-snapshot path is the one expected
    /// to succeed; either non-default mode is accepted so the test is not tied to which
    /// of the two the engine reaches.
    /// </summary>
    [LiveFact]
    public async Task ConsistentReachesTheEngineAndChangesTheModeInTheManifest()
    {
        var source = await SeedAsync();
        var path = ExportLiveTests.TempPath();

        try
        {
            var (code, output) = await RunAsync("--source", source, "--out", path, "--consistent");

            Assert.True(code == ExitOk, $"expected a consistent export to succeed on this server; output was:{Environment.NewLine}{output}");

            using var archive = await ArchiveReader.OpenAsync(path);

            Assert.NotEqual(ArchiveConsistency.PerTable, archive.Manifest.Consistency);
        }
        finally
        {
            ExportLiveTests.Clean(path);
        }
    }

    /// <summary>Without --consistent the manifest is the table-by-table default - the contrast case for the test above.</summary>
    [LiveFact]
    public async Task WithoutConsistentTheModeIsPerTable()
    {
        var source = await SeedAsync();
        var path = ExportLiveTests.TempPath();

        try
        {
            var (code, _) = await RunAsync("--source", source, "--out", path);

            Assert.Equal(ExitOk, code);

            using var archive = await ArchiveReader.OpenAsync(path);
            Assert.Equal(ArchiveConsistency.PerTable, archive.Manifest.Consistency);
        }
        finally
        {
            ExportLiveTests.Clean(path);
        }
    }

    /// <summary>
    /// --spool names where the working directory actually goes, and --resume
    /// (<see cref="SqlArchive.Core.Export.ExportOptions.Resumable"/>) is what keeps it
    /// there after a failure instead of it being cleaned up.
    /// <para>
    /// The failure is forced deterministically: a table with only a <c>rowversion</c>
    /// column - a physical column, so the CREATE TABLE itself is legal, but one SQL
    /// Server assigns itself and the archive cannot carry - has nothing left to write,
    /// so <c>DatabaseExporter</c> refuses before a row is read. That happens after the
    /// spool's fingerprint is written and before anything else, which is exactly enough
    /// to prove both flags did something: the
    /// fingerprint lands at the path <c>--spool</c> named, and it is still there
    /// afterwards only because <c>--resume</c> asked for it to be kept.
    /// </para>
    /// </summary>
    [LiveFact]
    public async Task SpoolNamesWhereTheWorkGoesAndResumeKeepsItAfterAFailure()
    {
        var source = await _server.CreateDatabaseAsync();

        await SqlServerFixture.ExecuteAsync(source, """
            CREATE TABLE dbo.NothingToCarry (
                Version rowversion NOT NULL
            );
            """);

        var path = ExportLiveTests.TempPath();
        var spool = Path.Combine(Path.GetTempPath(), $"sqlarchive-cli-spool-{Guid.NewGuid():N}");

        try
        {
            var (code, output) = await RunAsync(
                "--source", source, "--out", path, "--spool", spool, "--resume");

            Assert.Equal(ExitFailed, code);
            Assert.Contains("has no column the archive can carry", output, StringComparison.Ordinal);

            // Not a stack trace: the operator gets the engine's own sentence and nothing
            // that only makes sense with the source open.
            Assert.DoesNotContain("at SqlArchive", output, StringComparison.Ordinal);

            Assert.True(Directory.Exists(spool), "--spool has to be where the working directory actually went");
            Assert.True(
                File.Exists(Path.Combine(spool, "fingerprint.json")),
                "the fingerprint is the one thing DatabaseExporter always writes before it can fail, so its absence would mean --spool never reached WorkingDirectory");
        }
        finally
        {
            ExportLiveTests.Clean(path);

            if(Directory.Exists(spool))
                Directory.Delete(spool, recursive: true);
        }
    }

    /// <summary>The contrast case: the same forced failure, without --resume, leaves no spool behind at all.</summary>
    [LiveFact]
    public async Task WithoutResumeAFailedRunLeavesNoSpoolBehind()
    {
        var source = await _server.CreateDatabaseAsync();

        await SqlServerFixture.ExecuteAsync(source, """
            CREATE TABLE dbo.NothingToCarry (
                Version rowversion NOT NULL
            );
            """);

        var path = ExportLiveTests.TempPath();
        var spool = Path.Combine(Path.GetTempPath(), $"sqlarchive-cli-spool-{Guid.NewGuid():N}");

        try
        {
            var (code, _) = await RunAsync("--source", source, "--out", path, "--spool", spool);

            Assert.Equal(ExitFailed, code);
            Assert.False(Directory.Exists(spool), "with no --resume the spool is not kept after a failed export");
        }
        finally
        {
            ExportLiveTests.Clean(path);

            if(Directory.Exists(spool))
                Directory.Delete(spool, recursive: true);
        }
    }

    private async Task<string> SeedAsync()
    {
        var source = await _server.CreateDatabaseAsync();

        await SqlServerFixture.ExecuteAsync(source, Schema);

        await SqlServerFixture.ExecuteAsync(source, """
            INSERT INTO dbo.Widget (Name, Status) VALUES
                (N'w1', N'Active'), (N'w2', N'Active'), (N'w3', N'Retired'),
                (N'w4', N'Active'), (N'w5', N'Retired');

            INSERT INTO dbo.Note (Message) VALUES (N'n1'), (N'n2'), (N'n3');
            """);

        return source;
    }

    /// <summary>
    /// Runs <c>export</c> through the same parser and the same <c>Settings.Validate()</c>
    /// an operator's invocation goes through, with the console recorded instead of
    /// written to a terminal - the same technique <c>CliTests.Run</c> uses in
    /// <c>SqlArchive.Core.Tests</c>, reimplemented here because that method is private to
    /// a file this work package does not own.
    /// </summary>
    private static async Task<(int Code, string Output)> RunAsync(params string[] args)
    {
        var writer = new StringWriter();
        var previous = AnsiConsole.Console;

        try
        {
            var recorder = AnsiConsole.Create(new AnsiConsoleSettings
            {
                Ansi = AnsiSupport.No,
                ColorSystem = ColorSystemSupport.NoColors,
                Interactive = InteractionSupport.No,
                Out = new AnsiConsoleOutput(writer)
            });

            recorder.Profile.Width = 200;
            AnsiConsole.Console = recorder;

            var app = new CommandApp<ExportCommand>();
            app.Configure(config => config.ConfigureConsole(recorder));

            var code = await app.RunAsync(args);
            return (code, writer.ToString());
        }
        finally
        {
            AnsiConsole.Console = previous;
        }
    }
}
