using System.IO.Compression;
using System.Text;
using Spectre.Console;
using SqlArchive.Cli;
using SqlArchive.Core.Format;
using SqlSchemaDiff.Models;

namespace SqlArchive.Core.Tests;

/// <summary>
/// The CLI, driven through the same entry point the operator types into: the same parser,
/// the same flags, the same exit codes.
/// <para>
/// One class on purpose. The console is a static, these tests swap it for a recorder, and
/// xunit runs the tests of one class one at a time.
/// </para>
/// </summary>
public sealed class CliTests : IDisposable
{
    private static readonly ArchiveColumn[] Columns = [new("Id", "int"), new("Name", "nvarchar")];

    private readonly string _directory =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"sqlarchive-cli-{Guid.NewGuid():N}")).FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch(IOException)
        {
            // A temporary directory that outlives the run is not a failed test.
        }
    }

    // ---------------------------------------------------------------- the surface

    [Fact]
    public void TheVersionIsTheOneInTheProjectFile()
    {
        var (code, output) = Run("--version");

        Assert.Equal(0, code);
        Assert.Contains("0.1.0", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every command starts. It sounds obvious and it is not: in the sibling repository a
    /// command died with "Could not find color or style 'IdCliente'" because an example
    /// printed square brackets, which Spectre reads as a style tag.
    /// </summary>
    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("inspect", "--help")]
    [InlineData("export", "--help")]
    [InlineData("import", "--help")]
    [InlineData("verify", "--help")]
    public void EveryVerbPrintsItsHelp(params string[] args)
    {
        var (code, output) = Run(args);

        Assert.Equal(0, code);
        Assert.NotEmpty(output.Trim());
    }

    [Fact]
    public void TheRootHelpListsTheFourVerbs()
    {
        var (code, output) = Run("--help");

        Assert.Equal(0, code);

        foreach(var verb in new[] { "inspect", "export", "import", "verify" })
            Assert.Contains(verb, output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The help says which of the four do not work yet, because a list of commands that
    /// does not say so is a promise the build cannot keep.
    /// </summary>
    [Fact]
    public void TheRootHelpSaysWhichVerbsAreNotBuilt()
    {
        var (_, output) = Run("--help");
        var flattened = Flatten(output);

        Assert.Contains("Not built yet - work package 2.2", flattened, StringComparison.Ordinal);
        Assert.Contains("Not built yet - work package 2.3", flattened, StringComparison.Ordinal);
        Assert.Contains("Not built yet - work package 2.4", flattened, StringComparison.Ordinal);
    }

    /// <summary>
    /// Without strict parsing an option nobody declared is collected as a leftover and
    /// the command runs anyway, so a typo in a flag runs the command without it.
    /// </summary>
    [Fact]
    public async Task AnOptionThisToolDoesNotKnowIsAnError()
    {
        var path = await ArchiveAsync();
        var (code, output) = Run("inspect", path, "--verbose");

        Assert.Equal(ExitCodes.Failed, code);
        Assert.Contains("Unknown option", output, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- the verbs that are not built

    [Theory]
    [InlineData("2.2", "export", "--source", "Server=x;Database=y", "--out", "z.sqlarchive")]
    [InlineData("2.3", "import", "z.sqlarchive", "--destination", "Server=x;Database=y")]
    [InlineData("2.4", "verify", "z.sqlarchive")]
    public void AVerbThatIsNotBuiltRefusesAndNamesItsWorkPackage(string workPackage, params string[] args)
    {
        var (code, output) = Run(args);
        var flattened = Flatten(output);

        // Not zero. A command that takes arguments and reports success without doing
        // anything is worse than one that is not there.
        Assert.Equal(ExitCodes.NotBuiltYet, code);

        Assert.Contains("is not built yet", flattened, StringComparison.Ordinal);
        Assert.Contains($"work package {workPackage}", flattened, StringComparison.Ordinal);
        Assert.Contains("Nothing was read and nothing was written", flattened, StringComparison.Ordinal);
        Assert.Contains("sqlarchive inspect", flattened, StringComparison.Ordinal);
    }

    /// <summary>
    /// "Not built yet" and "you asked for something that cannot be" are different answers,
    /// and the validation runs first so that the second one is the one you get.
    /// </summary>
    [Theory]
    [InlineData("export", "--out", "z.sqlarchive")]
    [InlineData("export", "--source", "Server=x", "--out", "z.sqlarchive", "--schema-only", "--where", "dbo.T=1=1")]
    [InlineData("import", "z.sqlarchive", "--destination", "Server=x", "--schema-only", "--data-only")]
    [InlineData("verify", "z.sqlarchive", "--schema-only")]
    [InlineData("export", "--source", "Server=x", "--out", "z.sqlarchive", "--maxdop", "0")]
    public void AnImpossibleCombinationIsRefusedBeforeTheVerbEvenTries(params string[] args)
    {
        var (code, output) = Run(args);

        Assert.Equal(ExitCodes.Failed, code);
        Assert.DoesNotContain("is not built yet", Flatten(output), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- inspect

    [Fact]
    public async Task InspectReadsWhatTheArchiveSaysAboutItself()
    {
        var path = await ArchiveAsync();
        var (code, output) = Run("inspect", path);
        var flattened = Flatten(output);

        Assert.Equal(ExitCodes.Ok, code);
        Assert.Contains("sqlarchive, version 1", flattened, StringComparison.Ordinal);
        Assert.Contains("SQL2022", flattened, StringComparison.Ordinal);
        Assert.Contains("Ventas", flattened, StringComparison.Ordinal);
        Assert.Contains("[dbo].[Customer]", flattened, StringComparison.Ordinal);
        Assert.Contains("snapshot", flattened, StringComparison.Ordinal);
        Assert.Contains("every table is the same instant", flattened, StringComparison.Ordinal);
    }

    /// <summary>
    /// A table is named <c>[dbo].[Customer]</c> everywhere in this codebase, and that is
    /// markup to the console library. This is the test the sibling repository did not have.
    /// </summary>
    [Fact]
    public async Task InspectSurvivesIdentifiersThatLookLikeConsoleMarkup()
    {
        var path = await ArchiveAsync(oddTableName: "Order [2026]");
        var (code, output) = Run("inspect", path, "--entries");

        Assert.Equal(ExitCodes.Ok, code);
        Assert.Contains("[dbo].[Order [2026]]", Flatten(output), StringComparison.Ordinal);
    }

    [Fact]
    public async Task InspectSaysWhichTablesMakeTheArchivePartial()
    {
        var path = await ArchiveAsync();
        var (_, output) = Run("inspect", path);
        var flattened = Flatten(output);

        Assert.Contains("this archive is partial", flattened, StringComparison.Ordinal);
        Assert.Contains("carrying a row filter", flattened, StringComparison.Ordinal);
        Assert.Contains("archived with no rows at all", flattened, StringComparison.Ordinal);
        Assert.Contains("Total > 0", flattened, StringComparison.Ordinal);
        Assert.Contains("schema only, rows not archived", flattened, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InspectSaysWhichColumnsTheArchiveDoesNotCarry()
    {
        var path = await ArchiveAsync();
        var (_, output) = Run("inspect", path);
        var flattened = Flatten(output);

        Assert.Contains("Columns the archive does not carry", flattened, StringComparison.Ordinal);
        Assert.Contains("rowversion", flattened, StringComparison.Ordinal);
        Assert.Contains("computed", flattened, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InspectListsTheEntriesWithoutUnpackingThem()
    {
        var path = await ArchiveAsync();
        var (code, output) = Run("inspect", path, "--entries");
        var flattened = Flatten(output);

        Assert.Equal(ExitCodes.Ok, code);
        Assert.Contains("schema/040_tables.sql", flattened, StringComparison.Ordinal);
        Assert.Contains("data/dbo.Customer.jsonl", flattened, StringComparison.Ordinal);
        Assert.Contains("manifest.json", flattened, StringComparison.Ordinal);
        Assert.Contains("The manifest accounts for every entry exactly once", flattened, StringComparison.Ordinal);
    }

    /// <summary>
    /// --json hands over the manifest as the file holds it. Re-serializing it through this
    /// build would quietly drop whatever this build does not understand, which is exactly
    /// what someone reading a newer archive needs to see.
    /// </summary>
    [Fact]
    public async Task InspectPrintsTheManifestAsTheArchiveHoldsIt()
    {
        var path = await ArchiveAsync();

        using var zip = ZipFile.OpenRead(path);
        using var stream = zip.GetEntry(ArchiveFormat.ManifestEntry)!.Open();
        using var reader = new StreamReader(stream, ArchiveFormat.Utf8);

        var expected = await reader.ReadToEndAsync();
        var (code, output) = Run("inspect", path, "--json");

        Assert.Equal(ExitCodes.Ok, code);
        Assert.Equal(expected, output);
    }

    [Fact]
    public async Task InspectDescribesAnArchiveFromAVersionItCannotRestore()
    {
        var path = await ArchiveAsync(formatVersion: 3);
        var (code, output) = Run("inspect", path);
        var flattened = Flatten(output);

        // Describing it is allowed on purpose: an archive exists to be opened by whoever
        // is there when the source is not.
        Assert.Equal(ExitCodes.Ok, code);
        Assert.Contains("format version 3", flattened, StringComparison.Ordinal);
        Assert.Contains("This build reads version 1", flattened, StringComparison.Ordinal);
        Assert.Contains("[dbo].[Customer]", flattened, StringComparison.Ordinal);
    }

    [Fact]
    public void InspectShowsThePropertiesItDoesNotUnderstand()
    {
        var path = Path.Combine(_directory, "future.sqlarchive");

        RawArchive(path, """
            {
              "format": "sqlarchive",
              "formatVersion": 1,
              "tool": { "name": "SqlArchive", "version": "9.9.9" },
              "createdAt": "2026-09-07T22:30:00+00:00",
              "source": { "server": "SQL2022", "database": "Ventas" },
              "consistency": "per-table",
              "tables": [],
              "files": {},
              "encryption": { "algorithm": "aes-256-gcm" },
              "signedBy": "someone"
            }
            """);

        var (code, output) = Run("inspect", path);
        var flattened = Flatten(output);

        Assert.Equal(ExitCodes.Ok, code);
        Assert.Contains("properties this build does not know", flattened, StringComparison.Ordinal);
        Assert.Contains("encryption", flattened, StringComparison.Ordinal);
        Assert.Contains("signedBy", flattened, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- inspect, on somebody else's archive

    [Fact]
    public void InspectReadsADbDumperArchiveAndSaysWhatItCannotTellYou()
    {
        var path = Path.Combine(_directory, "Ventas.dbdump.zip");
        RawArchive(path, DbDumperManifest);

        var (code, output) = Run("inspect", path);
        var flattened = Flatten(output);

        Assert.Equal(ExitCodes.Ok, code);

        // Whose file this is, said plainly. The mapping onto our model necessarily stamps
        // our format name on it, and the header must not repeat that back as fact.
        Assert.Contains("dbdumper, version 1", flattened, StringComparison.Ordinal);
        Assert.DoesNotContain("sqlarchive, version 1", flattened, StringComparison.Ordinal);

        Assert.Contains("carries no per-table row hash", flattened, StringComparison.Ordinal);
        Assert.Contains("compare the schema and the row counts and nothing further", flattened, StringComparison.Ordinal);

        // And it still reads: the tables, their counts, and the schema behind them.
        Assert.Contains("[sales].[Order]", flattened, StringComparison.Ordinal);
        Assert.Contains("4,711", flattened, StringComparison.Ordinal);
    }

    /// <summary>An archive that was unpacked, or a dumper that writes a folder instead of a zip.</summary>
    [Fact]
    public async Task InspectReadsAManifestOnItsOwn()
    {
        var archive = await ArchiveAsync();
        var loose = Path.Combine(_directory, "manifest.json");

        using(var zip = ZipFile.OpenRead(archive))
            zip.GetEntry(ArchiveFormat.ManifestEntry)!.ExtractToFile(loose);

        var (code, output) = Run("inspect", loose);
        var flattened = Flatten(output);

        Assert.Equal(ExitCodes.Ok, code);
        Assert.Contains("[dbo].[Customer]", flattened, StringComparison.Ordinal);
        Assert.Contains("there are no entries to list", flattened, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- inspect, refusing

    [Fact]
    public void InspectSaysSoWhenThereIsNothingAtThePath()
    {
        var (code, output) = Run("inspect", Path.Combine(_directory, "nothing.sqlarchive"));

        Assert.Equal(ExitCodes.Failed, code);
        Assert.Contains("nothing.sqlarchive", Flatten(output), StringComparison.Ordinal);
    }

    [Fact]
    public async Task InspectSaysWhyAFileIsNeitherFormat()
    {
        var path = Path.Combine(_directory, "notanarchive.txt");
        await File.WriteAllTextAsync(path, "hello");

        var (code, output) = Run("inspect", path);
        var flattened = Flatten(output);

        Assert.Equal(ExitCodes.Failed, code);
        Assert.Contains("not a SqlArchive manifest", flattened, StringComparison.Ordinal);
        Assert.Contains("not one of dbdumper's either", flattened, StringComparison.Ordinal);
    }

    [Fact]
    public void InspectSaysSoWhenAZipHasNoManifest()
    {
        var path = Path.Combine(_directory, "plain.zip");

        using(var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using var stream = zip.CreateEntry("hello.txt").Open();
            stream.Write("hello"u8);
        }

        var (code, output) = Run("inspect", path);

        Assert.Equal(ExitCodes.Failed, code);
        Assert.Contains(ArchiveFormat.ManifestEntry, Flatten(output), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// Runs the CLI through its own entry point, with the console swapped for something
    /// that can be read back. Wide on purpose: the assertions are about what the tool
    /// says, not about where a terminal happens to wrap it.
    /// </summary>
    private static (int Code, string Output) Run(params string[] args)
    {
        var writer = new StringWriter();
        var console = AnsiConsole.Console;
        var standardOut = Console.Out;

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

            // --json goes straight to the console's writer rather than through a renderer,
            // so it has to be captured separately.
            Console.SetOut(writer);

            // The recorder goes in twice on purpose: the commands render through the
            // static console, and Spectre renders help and parse errors through the one
            // the app was configured with.
            return (Program.Run(args, recorder), writer.ToString());
        }
        finally
        {
            Console.SetOut(standardOut);
            AnsiConsole.Console = console;
        }
    }

    /// <summary>
    /// The console wraps at whatever width it has, and a wrapped sentence is still the
    /// same sentence. Assertions are made against this rather than against the layout.
    /// </summary>
    private static string Flatten(string output) =>
        string.Join(' ', output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()))
            .Replace("  ", " ", StringComparison.Ordinal)
            .Replace("  ", " ", StringComparison.Ordinal);

    /// <summary>
    /// An archive with the three shapes inspect has to describe: a plain table, a table
    /// that was filtered and drops columns, and a table whose rows were deliberately left
    /// out.
    /// </summary>
    private async Task<string> ArchiveAsync(string oddTableName = "Order", int formatVersion = 1)
    {
        var path = Path.Combine(_directory, $"archive-{Guid.NewGuid():N}.sqlarchive");

        var manifest = new ArchiveManifest
        {
            FormatVersion = formatVersion,
            Tool = new ArchiveTool { Version = "0.1.0" },
            CreatedAt = new DateTimeOffset(2026, 9, 7, 22, 30, 0, TimeSpan.Zero),
            Source = new ArchiveSource
            {
                Server = "SQL2022",
                Database = "Ventas",
                Edition = "Developer Edition (64-bit)",
                ProductVersion = "16.0.4165.4",
                Collation = "SQL_Latin1_General_CP1_CI_AS"
            },
            Consistency = ArchiveConsistency.Snapshot,
            Schema = new DatabaseSnapshot
            {
                DatabaseName = "Ventas",
                Schemas = ["sales", "audit"],
                Objects =
                [
                    new DbSchemaObject { Type = DbObjectType.Table, Schema = "dbo", Name = "Customer" },
                    new DbSchemaObject { Type = DbObjectType.View, Schema = "dbo", Name = "V" }
                ]
            }
        };

        var writer = ArchiveWriter.Create(path);

        await using(writer)
        {
            await writer.AddTextAsync("schema/010_schemas.sql", "CREATE SCHEMA sales;\n");
            await writer.AddTextAsync("schema/040_tables.sql", "CREATE TABLE dbo.Customer (Id int, Name nvarchar(100));\n");

            manifest.Tables.Add(await TableAsync(writer, "dbo", "Customer", 3));

            var filtered = await TableAsync(writer, "dbo", oddTableName, 2);
            filtered.RowFilter = "Total > 0";
            filtered.OmittedColumns["Version"] = "rowversion";
            filtered.OmittedColumns["Line"] = "computed";
            manifest.Tables.Add(filtered);

            manifest.Tables.Add(new ArchiveTableEntry { Schema = "audit", Name = "Log", DataSkipped = true });

            await writer.AddTextAsync(ArchiveFormat.ReadmeEntry, ArchiveReadme.Compose(manifest));

            foreach(var entry in writer.EntryHashes)
            {
                var owner = manifest.Tables.FirstOrDefault(t => t.DataFiles.Contains(entry.Key));

                if(owner is null)
                    manifest.Files[entry.Key] = entry.Value;
                else
                    owner.FileHashes[entry.Key] = entry.Value;
            }

            await writer.WriteManifestAsync(manifest);
        }

        return path;
    }

    private static async Task<ArchiveTableEntry> TableAsync(ArchiveWriter writer, string schema, string name, int rows)
    {
        var hash = new RowHash.Accumulator();
        var entryName = ArchiveFormat.DataEntry(schema, name);
        var stream = writer.CreateEntry(entryName);

        await using(stream)
        {
            var rowWriter = new JsonlRowWriter(stream, Columns, hash);

            await using(rowWriter)
            {
                for(var i = 0; i < rows; i++)
                    await rowWriter.WriteAsync([(long)i, $"Böhm {i}"]);
            }
        }

        var entry = new ArchiveTableEntry { Schema = schema, Name = name, RowCount = rows, RowHash = hash.Value };
        entry.DataFiles.Add(entryName);

        return entry;
    }

    /// <summary>A zip with a manifest this build did not write, for the cases that are about the manifest itself.</summary>
    private static void RawArchive(string path, string manifest)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);

        Add(ArchiveFormat.ManifestEntry, manifest);
        Add("data/sales.Order.part000.jsonl", "[1,2]\n");
        Add("data/dbo.Customer.jsonl", "[1]\n");

        void Add(string name, string text)
        {
            using var stream = zip.CreateEntry(name).Open();
            stream.Write(Encoding.UTF8.GetBytes(text));
        }
    }

    /// <summary>dbdumper's manifest, version 1, with the property names it writes.</summary>
    private const string DbDumperManifest = """
        {
          "formatVersion": 1,
          "tool": "dbdumper 0.9.0",
          "createdAt": "2026-08-27T09:14:22Z",
          "source": {
            "server": "SQL2019",
            "database": "Ventas",
            "serverVersion": "15.0.4335.1",
            "edition": "Standard Edition",
            "collation": "SQL_Latin1_General_CP1_CI_AS"
          },
          "database": {
            "collation": "SQL_Latin1_General_CP1_CI_AS",
            "schemas": [ { "name": "dbo" }, { "name": "sales" } ],
            "tables": [
              {
                "schema": "sales",
                "name": "Order",
                "columns": [
                  { "name": "Id", "ordinal": 1, "typeName": "int", "maxLength": 4 },
                  { "name": "Line", "ordinal": 2, "typeName": "int", "isComputed": true, "computedDefinition": "([Id]*(2))" }
                ],
                "dataFiles": ["data/sales.Order.part000.jsonl"],
                "rowCount": 4711
              },
              {
                "schema": "dbo",
                "name": "Customer",
                "columns": [ { "name": "Id", "ordinal": 1, "typeName": "int", "maxLength": 4 } ],
                "dataFile": "data/dbo.Customer.jsonl",
                "rowCount": 12
              }
            ],
            "modules": [
              { "schema": "dbo", "name": "V", "kind": "view", "definition": "CREATE VIEW dbo.V AS SELECT 1 AS x" }
            ]
          }
        }
        """;
}
