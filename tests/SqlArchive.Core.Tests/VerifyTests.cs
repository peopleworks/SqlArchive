using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Spectre.Console;
using SqlArchive.Cli;
using SqlArchive.Core.Format;
using SqlArchive.Core.Verify;
using SqlSchemaDiff.Models;

namespace SqlArchive.Core.Tests;

/// <summary>
/// Everything verify decides without a server: whether the archive is intact, whether
/// the manifest's account of it is true, what the verdict comes to, and what the JSON
/// says.
/// <para>
/// The archives here are built by hand rather than exported, which is the point: this is
/// the question that still has an answer when the database the archive came from has
/// been gone for years, and a test of it that needed a database would be testing
/// something else.
/// </para>
/// <para>
/// The collection is the one xunit builds for <c>CliTests</c>, which is what its
/// generated name spells. Both classes drive the same entry point, and
/// <c>CliTests</c> swaps the static console while it runs and asserts on the whole of
/// what it captured; sharing <see cref="ConsoleCollection"/> is what stops the two from
/// writing into each other. <c>verify</c> itself renders through the console Spectre hands it rather than
/// the static one, so only the error paths, which go through the exception handler,
/// still depend on this.
/// </para>
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class VerifyTests : IDisposable
{
    private static readonly ArchiveColumn[] Columns = [new("Id", "int"), new("Name", "nvarchar")];

    private readonly string _directory =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"sqlarchive-verify-{Guid.NewGuid():N}")).FullName;

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

    // ---------------------------------------------------------------- is it intact?

    [Fact]
    public async Task AnIntactArchiveVerifiesWithNoServerAnywhereNearIt()
    {
        var path = await ArchiveAsync();

        var report = await new ArchiveVerifier(new VerifyOptions()).VerifyAsync(path);

        Assert.False(report.HasDifferences);
        Assert.True(report.Integrity.Matches);
        Assert.Equal(0, report.Integrity.Corrupt);
        Assert.Equal(0, report.Integrity.Missing);
        Assert.Equal(0, report.Integrity.Undeclared);

        // Four entries: two schema phases, the README and one table's rows. Asserted so
        // that an archive whose entries stopped being hashed could not pass this.
        Assert.Equal(4, report.Integrity.Intact);

        // Nothing was compared against a database, and the report says so rather than
        // implying a comparison that did not happen.
        Assert.Null(report.Database);
        Assert.Null(report.Schema);
        Assert.Empty(report.Tables);
        Assert.Contains(report.Notices, n => n.Contains("touched no server", StringComparison.Ordinal));
    }

    /// <summary>
    /// The archival failure: the bytes moved and the manifest did not. Caught with no
    /// server involved, and the archive is named along with the entry.
    /// </summary>
    [Fact]
    public async Task ACorruptedEntryIsCaughtAndNamedWithNoServerInvolved()
    {
        var path = await ArchiveAsync();

        Corrupt(path, ArchiveFormat.DataEntry("dbo", "Customer"));

        var report = await new ArchiveVerifier(new VerifyOptions()).VerifyAsync(path);

        Assert.True(report.HasDifferences);
        Assert.False(report.Integrity.Matches);
        Assert.Equal(1, report.Integrity.Corrupt);

        var entry = report.Integrity.Entries.Single(e => e.State == EntryState.Corrupt);

        Assert.Equal("data/dbo.Customer.jsonl", entry.Entry);
        Assert.NotEqual(entry.Declared, entry.Actual);
        Assert.StartsWith(ArchiveFormat.HashPrefix, entry.Actual, StringComparison.Ordinal);

        // The archive is named too: a nightly job verifying forty of them has to be told
        // which one moved.
        Assert.Equal(path, report.Archive);
    }

    /// <summary>
    /// The other half of the integrity question, and the one a manifest that only agreed
    /// with itself would never answer: an entry declared and not there.
    /// </summary>
    [Fact]
    public async Task AnEntryTheManifestDeclaresAndTheArchiveDoesNotHoldIsMissing()
    {
        var path = await ArchiveAsync(manifest =>
            manifest.Files["schema/999_never_written.sql"] = "sha256:" + new string('0', 64));

        var report = await new ArchiveVerifier(new VerifyOptions()).VerifyAsync(path);

        Assert.True(report.HasDifferences);
        Assert.Equal(1, report.Integrity.Missing);
        Assert.Equal(
            "schema/999_never_written.sql",
            report.Integrity.Entries.Single(e => e.State == EntryState.Missing).Entry);
    }

    [Fact]
    public async Task AnEntryTheManifestDoesNotAccountForIsADifference()
    {
        var path = await ArchiveAsync();

        Add(path, "schema/500_someone_added_this.sql", "SELECT 1;");

        var report = await new ArchiveVerifier(new VerifyOptions()).VerifyAsync(path);

        Assert.True(report.HasDifferences);
        Assert.Equal(1, report.Integrity.Undeclared);
        Assert.True(report.Integrity.UndeclaredCounts);
    }

    /// <summary>
    /// The same entry in an archive from a later version of the format is reported and
    /// not held against it. A newer writer is allowed to put things in an archive that
    /// this build has never heard of, and refusing to read the file over that would
    /// defeat the one promise an archive makes.
    /// </summary>
    [Fact]
    public async Task AnUnrecognisedEntryInANewerFormatIsReportedAndNotCountedAgainstIt()
    {
        var path = await ArchiveAsync(manifest => manifest.FormatVersion = 2);

        Add(path, "provenance/signature.bin", "not a format this build knows");

        var report = await new ArchiveVerifier(new VerifyOptions()).VerifyAsync(path);

        Assert.Equal(1, report.Integrity.Undeclared);
        Assert.False(report.Integrity.UndeclaredCounts);
        Assert.True(report.Integrity.Matches);
        Assert.False(report.HasDifferences);
        Assert.Contains(report.Notices, n => n.Contains("format version 2", StringComparison.Ordinal));
    }

    /// <summary>
    /// An entry the manifest names and records no hash for cannot be checked, and saying
    /// so is not the same as saying it is wrong. Every entry of a dbdumper archive is
    /// like this.
    /// </summary>
    [Fact]
    public async Task AnEntryWithNoDeclaredHashCannotBeCheckedAndIsNotADifference()
    {
        var path = await ArchiveAsync(manifest =>
            manifest.Table("dbo", "Customer")!.FileHashes.Clear());

        var report = await new ArchiveVerifier(new VerifyOptions()).VerifyAsync(path);

        Assert.Equal(1, report.Integrity.Unhashed);
        Assert.True(report.Integrity.Matches);
        Assert.False(report.HasDifferences);
    }

    /// <summary>
    /// <c>--schema-only</c> reads no table on either side, and decompressing a table's
    /// rows to hash them is reading it. On a hundred-gigabyte archive that is the whole
    /// cost of the command.
    /// </summary>
    [Fact]
    public async Task SchemaOnlyLeavesTheDataEntriesUnread()
    {
        var path = await ArchiveAsync();

        Corrupt(path, ArchiveFormat.DataEntry("dbo", "Customer"));

        var report = await new ArchiveVerifier(new VerifyOptions { SchemaOnly = true }).VerifyAsync(path);

        Assert.Equal(1, report.Integrity.NotChecked);
        Assert.Equal(0, report.Integrity.Corrupt);
        Assert.Equal(3, report.Integrity.Intact);
    }

    // ---------------------------------------------------------------- the JSON

    /// <summary>
    /// The JSON is the same object the readable report is rendered from, so the two can
    /// never disagree, and its enums are names rather than numbers so that a pipeline
    /// grepping for a verdict keeps working when a value is added in the middle.
    /// </summary>
    [Fact]
    public async Task TheJsonSaysWhatTheReportSaysAndSpellsItsVerdictsOut()
    {
        var path = await ArchiveAsync();

        Corrupt(path, ArchiveFormat.DataEntry("dbo", "Customer"));

        var report = await new ArchiveVerifier(new VerifyOptions()).VerifyAsync(path);

        using var document = JsonDocument.Parse(VerifyJson.Serialize(report));
        var root = document.RootElement;

        Assert.True(root.GetProperty("hasDifferences").GetBoolean());
        Assert.Equal(path, root.GetProperty("archive").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("database").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("schema").ValueKind);

        var integrity = root.GetProperty("integrity");

        Assert.Equal(1, integrity.GetProperty("corrupt").GetInt32());
        Assert.False(integrity.GetProperty("matches").GetBoolean());

        var corrupt = integrity.GetProperty("entries")
            .EnumerateArray()
            .Single(e => e.GetProperty("state").GetString() == "corrupt");

        Assert.Equal("data/dbo.Customer.jsonl", corrupt.GetProperty("entry").GetString());
    }

    /// <summary>
    /// Every outcome the enum can take is written as the name a build would look for.
    /// A number here would silently change meaning the day a value is inserted.
    /// </summary>
    [Fact]
    public void EveryTableOutcomeHasACamelCasedNameInTheJson()
    {
        var report = new VerifyReport
        {
            Archive = "x.sqlarchive",
            Tables = Enum.GetValues<TableOutcome>()
                .Select(o => new TableVerdict { Schema = "dbo", Name = o.ToString(), Outcome = o })
                .ToArray()
        };

        using var document = JsonDocument.Parse(VerifyJson.Serialize(report));

        var outcomes = document.RootElement.GetProperty("tables")
            .EnumerateArray()
            .Select(t => t.GetProperty("outcome").GetString())
            .ToArray();

        Assert.Contains("contentDiffers", outcomes);
        Assert.Contains("rowCountDiffers", outcomes);
        Assert.Contains("missingFromDatabase", outcomes);
        Assert.All(outcomes, o => Assert.False(string.IsNullOrEmpty(o)));
        Assert.All(outcomes, o => Assert.True(char.IsLower(o![0]), o));
    }

    /// <summary>
    /// A table that is neither a match nor a difference: the manifest cannot answer for
    /// it. Reported as its own thing, because counting it either way would be a lie in
    /// one direction or the other.
    /// </summary>
    [Fact]
    public void ATableTheManifestCannotAnswerForIsNeitherAMatchNorADifference()
    {
        var report = new VerifyReport
        {
            Archive = "x.sqlarchive",
            Tables =
            [
                new TableVerdict { Schema = "dbo", Name = "A", Outcome = TableOutcome.Matches },
                new TableVerdict { Schema = "audit", Name = "Log", Outcome = TableOutcome.NotVerifiable, DataSkipped = true },
                new TableVerdict { Schema = "dbo", Name = "B", Outcome = TableOutcome.NotCompared }
            ]
        };

        Assert.False(report.HasDifferences);
        Assert.Equal(1, report.Matching);
        Assert.Equal(1, report.Unverifiable);
        Assert.Equal(0, report.Differing);
    }

    // ---------------------------------------------------------------- through the CLI

    /// <summary>
    /// The verb runs. It used to refuse, naming its work package, and a test that only
    /// checked the exit code would not notice the difference between running and
    /// refusing - so this checks the code that means "ran and found nothing".
    /// </summary>
    [Fact]
    public async Task AnIntactArchiveExitsZero()
    {
        var path = await ArchiveAsync();

        var (code, output) = Run("verify", path);

        Assert.Equal(ExitCodes.Ok, code);
        Assert.Contains("No differences", output, StringComparison.Ordinal);
        Assert.DoesNotContain("is not built yet", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A comparison that ran and found differences returns 3, not 1. A nightly job that
    /// could not tell those apart would eventually treat an unreachable server as drift,
    /// or drift as an outage.
    /// <para>
    /// This also renders the whole report to the console on the way past, over an
    /// archive whose tables are called <c>[dbo].[Customer]</c> - which is markup to
    /// Spectre. A renderer that forgot to escape one of them would throw, and the
    /// exception handler would return 1 instead of the 3 asserted here.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AnArchiveThatHasMovedExitsThreeAndNotOne()
    {
        var path = await ArchiveAsync();

        Corrupt(path, ArchiveFormat.DataEntry("dbo", "Customer"));

        var (code, output) = Run("verify", path);

        Assert.Equal(ExitCodes.Differences, code);

        // The entry is named, and so is the archive.
        Assert.Contains("data/dbo.Customer.jsonl", Flatten(output), StringComparison.Ordinal);
        Assert.Contains(Path.GetFileName(path), Flatten(output), StringComparison.Ordinal);
        Assert.Contains("Differences found", Flatten(output), StringComparison.Ordinal);
    }

    /// <summary>A run that could not be made at all is the other answer, and it is 1.</summary>
    [Fact]
    public void AnArchiveThatIsNotThereExitsOne()
    {
        Assert.Equal(ExitCodes.Failed, Run("verify", Path.Combine(_directory, "nothing.sqlarchive")).Code);
    }

    [Fact]
    public async Task SomethingThatIsNotAnArchiveExitsOne()
    {
        var path = Path.Combine(_directory, "not-an-archive.sqlarchive");
        await File.WriteAllTextAsync(path, "this is not a zip");

        Assert.Equal(ExitCodes.Failed, Run("verify", path).Code);
    }

    /// <summary>
    /// A typo in <c>--against</c> is the tool being asked wrongly, not the tool breaking,
    /// and it says so in a sentence rather than in a stack trace.
    /// </summary>
    [Fact]
    public async Task AConnectionStringThatIsNotOneIsRefusedInWords()
    {
        var path = await ArchiveAsync();
        var (code, output) = Run("verify", path, "--against", "this is not a connection string");

        Assert.Equal(ExitCodes.Failed, code);
        Assert.Contains("--against is not a connection string", Flatten(output), StringComparison.Ordinal);
        Assert.DoesNotContain("at Microsoft.Data.SqlClient", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>--json</c> writes the verdict where a build can read it, and the file says the
    /// same thing the exit code did.
    /// </summary>
    [Fact]
    public async Task TheJsonFileIsWrittenBesideTheReadableReport()
    {
        var path = await ArchiveAsync();
        var json = Path.Combine(_directory, "verdict.json");

        Corrupt(path, ArchiveFormat.DataEntry("dbo", "Customer"));

        var (code, output) = Run("verify", path, "--json", json);

        Assert.Equal(ExitCodes.Differences, code);

        // The readable report still goes to the console, which is the half of --json
        // that is easy to lose.
        Assert.Contains("Integrity", output, StringComparison.Ordinal);
        Assert.True(File.Exists(json));

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(json));

        Assert.True(document.RootElement.GetProperty("hasDifferences").GetBoolean());
        Assert.Equal(1, document.RootElement.GetProperty("integrity").GetProperty("corrupt").GetInt32());
    }

    // ---------------------------------------------------------------- the fixtures

    /// <summary>
    /// The CLI through its own entry point, with a console of this suite's own rather
    /// than the process-wide one. Wide on purpose: the assertions are about what the
    /// tool says, not about where a terminal happens to wrap it.
    /// </summary>
    private static (int Code, string Output) Run(params string[] args)
    {
        var writer = new StringWriter();

        var recorder = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No,
            Out = new AnsiConsoleOutput(writer)
        });

        recorder.Profile.Width = 200;

        return (Program.Run(args, recorder), writer.ToString());
    }

    /// <summary>A wrapped sentence is still the same sentence.</summary>
    private static string Flatten(string output) =>
        string.Join(' ', output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()))
            .Replace("  ", " ", StringComparison.Ordinal)
            .Replace("  ", " ", StringComparison.Ordinal);

    /// <summary>
    /// An archive built by hand: two schema phases, a README, one table with rows, one
    /// table whose rows were deliberately not archived, and a manifest that accounts for
    /// every entry exactly once.
    /// </summary>
    private async Task<string> ArchiveAsync(Action<ArchiveManifest>? amend = null)
    {
        var path = Path.Combine(_directory, $"archive-{Guid.NewGuid():N}.sqlarchive");

        var manifest = new ArchiveManifest
        {
            Tool = new ArchiveTool { Version = "0.1.0" },
            CreatedAt = new DateTimeOffset(2026, 9, 7, 22, 30, 0, TimeSpan.Zero),
            Source = new ArchiveSource { Server = "SQL2022", Database = "Ventas" },
            Schema = new DatabaseSnapshot
            {
                DatabaseName = "Ventas",
                Objects =
                [
                    new DbSchemaObject { Type = DbObjectType.Table, Schema = "dbo", Name = "Customer" }
                ]
            }
        };

        var writer = ArchiveWriter.Create(path);

        await using(writer)
        {
            await writer.AddTextAsync("schema/010_schemas.sql", "CREATE SCHEMA sales;\n");
            await writer.AddTextAsync("schema/040_tables.sql", "CREATE TABLE dbo.Customer (Id int, Name nvarchar(100));\n");

            manifest.Tables.Add(await TableAsync(writer, "dbo", "Customer", 3));
            manifest.Tables.Add(new ArchiveTableEntry { Schema = "audit", Name = "Log", DataSkipped = true });

            await writer.AddTextAsync(ArchiveFormat.ReadmeEntry, ArchiveReadme.Compose(manifest));

            foreach(var (name, hash) in writer.EntryHashes)
            {
                var owner = manifest.Tables.FirstOrDefault(t => t.DataFiles.Contains(name));

                if(owner is null)
                    manifest.Files[name] = hash;
                else
                    owner.FileHashes[name] = hash;
            }

            amend?.Invoke(manifest);

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

    /// <summary>
    /// Rewrites one entry's bytes and leaves the manifest saying what it used to hash
    /// to. Bit rot, a bad copy, a tape read back years later - the failure the file
    /// hashes exist for.
    /// </summary>
    private static void Corrupt(string path, string entryName)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Update);

        var entry = zip.GetEntry(entryName) ?? throw new InvalidOperationException($"No entry '{entryName}'.");

        using var stream = entry.Open();
        stream.SetLength(0);

        var bytes = Encoding.UTF8.GetBytes("{\"Id\":0,\"Name\":\"tampered\"}\n");
        stream.Write(bytes, 0, bytes.Length);
    }

    /// <summary>Puts an entry into the archive that the manifest knows nothing about.</summary>
    private static void Add(string path, string entryName, string text)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Update);
        var entry = zip.CreateEntry(entryName);

        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, ArchiveFormat.Utf8);

        writer.Write(text);
    }
}
