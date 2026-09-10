using System.ComponentModel;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Spectre.Console;
using Spectre.Console.Cli;
using SqlArchive.Core.Format;
using SqlArchive.Core.Verify;

namespace SqlArchive.Cli.Commands;

/// <summary>
/// Answers whether two things match.
/// <para>
/// One manifest answers three questions with the same code: is the archive intact, does
/// a restored database match it, and has a live database drifted from it. The third is
/// the audit use, and it is this same command pointed somewhere else.
/// </para>
/// <para>
/// Finding differences is not failing. The exit code says which happened -
/// <see cref="ExitCodes.Differences"/> for a comparison that ran and found drift,
/// <see cref="ExitCodes.Failed"/> for one that could not be made - because a script that
/// cannot tell those apart will eventually act on the wrong one.
/// </para>
/// </summary>
public sealed class VerifyCommand : AsyncCommand<VerifyCommand.Settings>
{
    /// <summary>How much of a 64-character hash the table shows. --json writes them whole.</summary>
    private const int HashPreview = 8;

    private readonly IAnsiConsole _console;

    /// <summary>
    /// The console this renders through, handed over by Spectre rather than reached for
    /// statically.
    /// </summary>
    /// <remarks>
    /// <c>inspect</c> writes to the static <see cref="AnsiConsole"/> and its tests swap
    /// that static out while they run. A second command doing the same would race the
    /// first: xunit runs different test classes at the same time, and one of those tests
    /// asserts on the <i>whole</i> of what was captured. Taking the console as a
    /// dependency is what lets this one be driven through the real entry point without
    /// two suites writing into each other.
    /// </remarks>
    public VerifyCommand(IAnsiConsole console)
    {
        ArgumentNullException.ThrowIfNull(console);
        _console = console;
    }

    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<ARCHIVE>")]
        [Description("The archive to verify, and the side of the comparison that is a file.")]
        public string Archive { get; init; } = string.Empty;

        [CommandOption("-a|--against <CONNECTION>")]
        [Description(
            "Compare the archive against a live database: the schema by diff, and per table the row count and " +
            "the content hash. Without it, only the integrity of the archive itself is checked - the file hashes " +
            "in the manifest against the entries - and no server is touched at all.")]
        public string? Against { get; init; }

        [CommandOption("--table <GLOB>")]
        [Description("Verify only tables matching this glob, as schema.table. Repeatable.")]
        public string[] Tables { get; init; } = [];

        [CommandOption("--exclude <GLOB>")]
        [Description("Leave out tables matching this glob, as schema.table. Repeatable. Applied after --table.")]
        public string[] Exclude { get; init; } = [];

        [CommandOption("--schema-only")]
        [Description("Compare the schema and stop. No table is read on either side.")]
        public bool SchemaOnly { get; init; }

        [CommandOption("--json <FILE>")]
        [Description(
            "Write the verdict as JSON as well, for a build to read. The readable report still goes to the " +
            "console. A build branching on the exit code gets three answers rather than two: 0 when the two " +
            "sides match, 3 when the comparison ran and found differences, and 1 when it could not be made at " +
            "all. Finding drift is not failing, and a job that treats a corrupt archive and an unreachable " +
            "server as the same event will eventually act on the wrong one.")]
        public string? Json { get; init; }

        [CommandOption("--maxdop <N>")]
        [Description("How many tables to read at once. Default: the processor count.")]
        public int? MaxDop { get; init; }

        [CommandOption("--timeout <SECONDS>")]
        [Description("Command timeout for reading a table. 0, the default, is no limit: verify reads whole tables and a clock is the wrong way to notice a slow one.")]
        public int? Timeout { get; init; }

        public override ValidationResult Validate()
        {
            if(string.IsNullOrWhiteSpace(Archive))
                return ValidationResult.Error("Name the archive to verify.");

            if(SchemaOnly && string.IsNullOrWhiteSpace(Against))
                return ValidationResult.Error(
                    "--schema-only compares the schema in the archive against a database, so it needs --against. " +
                    "With no --against there is nothing to compare a schema to, only file hashes to check.");

            if(MaxDop is <= 0)
                return ValidationResult.Error("--maxdop has to be at least 1.");

            if(Timeout is < 0)
                return ValidationResult.Error("--timeout cannot be negative. Zero means no limit.");

            if(!File.Exists(Archive))
                return ValidationResult.Error($"There is no file at '{Archive}'.");

            return ValidationResult.Success();
        }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        VerifyReport report;

        try
        {
            report = await RunAsync(settings).ConfigureAwait(false);
        }
        catch(SqlException failed)
        {
            // The database side could not be reached or could not be read. That is a run
            // that did not happen, not a comparison that found something, so it does not
            // get the exit code that means drift.
            _console.MarkupLine($"[red]{failed.Message.EscapeMarkup()}[/]");
            return ExitCodes.Failed;
        }
        catch(ArgumentException malformed)
        {
            // A typo in --against. The driver's own message names the position it gave up
            // at, which is more use than the stack trace under it, and a stack trace here
            // would say the tool broke when what happened is that it was asked wrongly.
            _console.MarkupLine(
                $"[red]--against is not a connection string:[/] {malformed.Message.EscapeMarkup()}");

            _console.MarkupLine(
                "[dim]One looks like this:  " +
                "Server=SQL2022;Database=Ventas;Integrated Security=true;TrustServerCertificate=true[/]");

            return ExitCodes.Failed;
        }

        if(settings.Json is { Length: > 0 } json)
            await VerifyJson.WriteAsync(report, json, CancellationToken.None).ConfigureAwait(false);

        Render(report, settings.Json);

        return report.HasDifferences ? ExitCodes.Differences : ExitCodes.Ok;
    }

    /// <summary>
    /// The work, with a spinner over it where there is somebody watching.
    /// </summary>
    /// <remarks>
    /// Only where the console is interactive. A status line redirected into a log file
    /// is one line per entry, and on a large archive that is thousands of lines of
    /// progress wrapped around the report somebody actually wanted.
    /// </remarks>
    private Task<VerifyReport> RunAsync(Settings settings)
    {
        if(!_console.Profile.Capabilities.Interactive)
            return new ArchiveVerifier(Options(settings, null)).VerifyAsync(settings.Archive, CancellationToken.None);

        return _console.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync(
                "Reading the archive",
                context =>
                {
                    var progress = new Progress<VerifyProgress>(p => context.Status(Escape(Describe(p))));
                    return new ArchiveVerifier(Options(settings, progress)).VerifyAsync(settings.Archive, CancellationToken.None);
                });
    }

    private static VerifyOptions Options(Settings settings, IProgress<VerifyProgress>? progress) => new()
    {
        ConnectionString = settings.Against,
        IncludeTables = settings.Tables,
        ExcludeTables = settings.Exclude,
        SchemaOnly = settings.SchemaOnly,
        Parallelism = settings.MaxDop ?? Environment.ProcessorCount,
        CommandTimeoutSeconds = settings.Timeout ?? 0,
        Progress = progress
    };

    private static string Describe(VerifyProgress progress) => progress.Phase switch
    {
        "integrity" => progress.Total == 0
            ? "Checking the archive"
            : $"Checking the archive - {Fraction(progress)}",

        "schema" => "Reading the database's schema",

        _ => progress.Table is { Length: > 0 } table
            ? $"{table} - {Fraction(progress)}"
            : "Reading the tables"
    };

    private static string Fraction(VerifyProgress progress) =>
        $"{progress.Done.ToString("N0", CultureInfo.InvariantCulture)} of " +
        $"{progress.Total.ToString("N0", CultureInfo.InvariantCulture)}";

    private void Render(VerifyReport report, string? jsonPath)
    {
        _console.WriteLine();
        _console.Write(new Rule($"[bold]{Escape(Path.GetFileName(report.Archive))}[/]").LeftJustified());
        _console.WriteLine();

        RenderHeader(report);
        RenderIntegrity(report.Integrity);
        RenderSchema(report.Schema);
        RenderTables(report);
        RenderNotices(report.Notices);
        RenderVerdict(report);

        if(jsonPath is { Length: > 0 })
            _console.MarkupLine($"[dim]The same verdict, as JSON, is in {Escape(jsonPath)}.[/]");
    }

    private void RenderHeader(VerifyReport report)
    {
        var grid = new Grid()
            .AddColumn(new GridColumn().PadRight(3).NoWrap())
            .AddColumn();

        Row(grid, "Archive", Escape(report.Archive));

        Row(grid, "Compared with", report.Database is { Length: > 0 } database
            ? Escape(database)
            : "[dim]nothing - the archive was checked against itself and no server was touched[/]");

        Row(grid, "Took", Escape(report.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)) + " s");

        _console.Write(grid);
        _console.WriteLine();
    }

    private void RenderIntegrity(IntegrityVerdict integrity)
    {
        var parts = new List<string>
        {
            $"{Plural(integrity.Intact, "entry", "entries")} intact"
        };

        if(integrity.Corrupt > 0)
            parts.Add($"[red]{Plural(integrity.Corrupt, "corrupt entry", "corrupt entries")}[/]");

        if(integrity.Missing > 0)
            parts.Add($"[red]{Plural(integrity.Missing, "entry", "entries")} declared and absent[/]");

        if(integrity.Undeclared > 0)
        {
            parts.Add(integrity.UndeclaredCounts
                ? $"[red]{Plural(integrity.Undeclared, "entry", "entries")} the manifest does not account for[/]"
                : $"[yellow]{Plural(integrity.Undeclared, "entry", "entries")} this build does not recognise[/]");
        }

        if(integrity.Unhashed > 0)
            parts.Add($"[yellow]{Plural(integrity.Unhashed, "entry", "entries")} with no hash to check[/]");

        if(integrity.NotChecked > 0)
            parts.Add($"[dim]{Plural(integrity.NotChecked, "entry", "entries")} not read[/]");

        _console.MarkupLine($"[bold]Integrity[/]  {string.Join(", ", parts)}");

        var wrong = integrity.Entries
            .Where(e => e.State is EntryState.Corrupt or EntryState.Missing or EntryState.Undeclared)
            .ToArray();

        foreach(var entry in wrong)
            _console.Write(new Padder(new Markup(EntryLine(entry)), new Padding(11, 0, 0, 0)));

        _console.WriteLine();
    }

    private static string EntryLine(EntryVerdict entry) => entry.State switch
    {
        EntryState.Corrupt =>
            $"[red]{Escape(entry.Entry)}[/] holds different bytes from the ones the manifest describes " +
            $"[dim](declared {Escape(Short(entry.Declared))}, read {Escape(Short(entry.Actual))})[/]",

        EntryState.Missing =>
            $"[red]{Escape(entry.Entry)}[/] is declared by the manifest and is not in the archive",

        _ =>
            $"[yellow]{Escape(entry.Entry)}[/] is in the archive and the manifest does not account for it"
    };

    private void RenderSchema(SchemaVerdict? schema)
    {
        if(schema is null)
            return;

        if(schema.Matches)
        {
            _console.MarkupLine("[bold]Schema[/]  [green]the two describe the same objects[/]");
        }
        else
        {
            _console.MarkupLine("[bold]Schema[/]");

            List(schema.OnlyInArchive, "only in the archive");
            List(schema.OnlyInDatabase, "only in the database");
            List(schema.Differing, "different on the two sides");

        }

        // Through a Padder rather than eight spaces in the string: a sentence this long
        // wraps, and a wrapped continuation that starts back at column zero reads as a
        // different line rather than as the rest of this one.
        foreach(var ignored in schema.Ignored)
            _console.Write(new Padder(new Markup($"[dim]not compared: {Escape(ignored)}[/]"), new Padding(8, 0, 0, 0)));

        _console.WriteLine();

        void List(IReadOnlyList<string> objects, string what)
        {
            if(objects.Count == 0)
                return;

            _console.MarkupLine($"        [yellow]{Plural(objects.Count, "object")} {Escape(what)}:[/] " +
                                Escape(string.Join(", ", objects.Order(StringComparer.OrdinalIgnoreCase))));
        }
    }

    private void RenderTables(VerifyReport report)
    {
        if(report.Tables.Count == 0)
            return;

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey35);

        table.AddColumn(new TableColumn("Table").NoWrap());
        table.AddColumn(new TableColumn("Verdict").NoWrap());
        table.AddColumn(new TableColumn("Rows").RightAligned().NoWrap());
        table.AddColumn(new TableColumn("Content").NoWrap());

        foreach(var verdict in report.Tables)
        {
            table.AddRow(
                // [dbo].[Customer] is markup to Spectre and a table name to everyone
                // else. Escaping is not optional anywhere in this file.
                Escape(verdict.Identifier),
                Verdict(verdict.Outcome),
                Rows(verdict),
                Content(verdict));
        }

        _console.Write(table);

        var summary = new List<string> { $"[green]{Plural(report.Matching, "table")} match[/]" };

        if(report.Differing > 0)
            summary.Add($"[red]{Plural(report.Differing, "table")} differ[/]");

        if(report.Unverifiable > 0)
            summary.Add($"[yellow]{Plural(report.Unverifiable, "table")} the manifest cannot answer for[/]");

        _console.MarkupLine(string.Join(", ", summary));

        RenderDetail(report);

        _console.WriteLine();
    }

    /// <summary>
    /// What differs, in sentences, under the table rather than inside it.
    /// <para>
    /// A column narrow enough to fit beside four others is too narrow to hold a
    /// sentence: at eighty columns it wraps every third word and the reader is left
    /// reassembling it. The table says which tables and how much; this says what.
    /// </para>
    /// </summary>
    private void RenderDetail(VerifyReport report)
    {
        var interesting = report.Tables
            .Where(t => t.Differences.Count > 0 || t.Limitation is { Length: > 0 })
            .ToArray();

        if(interesting.Length == 0)
            return;

        _console.WriteLine();

        var grid = new Grid()
            .AddColumn(new GridColumn().PadRight(2).NoWrap())
            .AddColumn();

        foreach(var verdict in interesting)
        {
            var first = true;

            foreach(var line in verdict.Differences)
            {
                grid.AddRow(first ? Escape(verdict.Identifier) : string.Empty, Escape(line));
                first = false;
            }

            if(verdict.Limitation is { Length: > 0 } limitation)
            {
                grid.AddRow(first ? Escape(verdict.Identifier) : string.Empty, $"[dim]{Escape(limitation)}[/]");
                first = false;
            }
        }

        _console.Write(grid);
    }

    /// <summary>
    /// The row counts. One number when the two sides agree, and an arrow between them
    /// when they do not - which is the thing being looked for, so it is what the column
    /// is shaped around.
    /// </summary>
    private static string Rows(TableVerdict verdict)
    {
        var archive = Count(verdict.ArchiveRows);
        var database = Count(verdict.DatabaseRows);

        if(verdict.ArchiveRows is null && verdict.DatabaseRows is null)
            return "[dim]-[/]";

        return verdict.ArchiveRows == verdict.DatabaseRows
            ? archive
            : $"{archive} [red]->[/] {database}";
    }

    /// <summary>
    /// The content hashes, the same way: one when they agree, both when they do not. A
    /// row count cannot see an UPDATE and this column is where one shows up.
    /// </summary>
    private static string Content(TableVerdict verdict)
    {
        if(!verdict.ContentCompared)
            return "[dim]-[/]";

        var archive = Escape(Short(verdict.ArchiveHash));
        var database = Escape(Short(verdict.DatabaseHash));

        return string.Equals(verdict.ArchiveHash, verdict.DatabaseHash, StringComparison.OrdinalIgnoreCase)
            ? $"[dim]{archive}[/]"
            : $"[dim]{archive}[/] [red]->[/] [dim]{database}[/]";
    }

    private static string Count(long? rows) =>
        rows is null ? "[dim]-[/]" : rows.Value.ToString("N0", CultureInfo.InvariantCulture);

    private static string Verdict(TableOutcome outcome) => outcome switch
    {
        TableOutcome.Matches => "[green]matches[/]",
        TableOutcome.NotCompared => "[dim]not compared[/]",
        TableOutcome.NotVerifiable => "[yellow]cannot say[/]",
        TableOutcome.MissingFromDatabase => "[red]missing[/]",
        TableOutcome.OnlyInDatabase => "[yellow]extra[/]",
        TableOutcome.SchemaDiffers => "[red]schema[/]",
        TableOutcome.RowCountDiffers => "[red]row count[/]",
        TableOutcome.ContentDiffers => "[red]content[/]",
        _ => "[red]unreadable[/]"
    };

    private void RenderNotices(IReadOnlyList<string> notices)
    {
        foreach(var notice in notices)
            _console.MarkupLine($"[dim]{Escape(notice)}[/]");

        if(notices.Count > 0)
            _console.WriteLine();
    }

    /// <summary>
    /// The one line somebody reads. It says which of the two answers this was, because
    /// "found differences" and "could not compare" leave by different doors and the exit
    /// code is what a script branches on.
    /// </summary>
    private void RenderVerdict(VerifyReport report)
    {
        if(!report.HasDifferences)
        {
            _console.MarkupLine(report.Database is { Length: > 0 } matched
                ? $"[green]No differences. {Escape(matched)} is what this archive says it is.[/]"
                : "[green]No differences. Every entry hashes to what the manifest declares.[/]");

            return;
        }

        _console.MarkupLine(
            $"[red]Differences found.[/] [dim]This run did what it was asked and the two sides do not match, so it " +
            $"returns {ExitCodes.Differences.ToString(CultureInfo.InvariantCulture)} rather than " +
            $"{ExitCodes.Failed.ToString(CultureInfo.InvariantCulture)}, which is what a run that could not make " +
            "the comparison returns.[/]");
    }

    private static void Row(Grid grid, string label, string value) =>
        grid.AddRow($"[dim]{Escape(label)}[/]", value);

    private static string Short(string? hash)
    {
        if(string.IsNullOrEmpty(hash))
            return "-";

        var body = hash.StartsWith(ArchiveFormat.HashPrefix, StringComparison.Ordinal)
            ? hash[ArchiveFormat.HashPrefix.Length..]
            : hash;

        return body.Length <= HashPreview ? body : body[..HashPreview] + "...";
    }

    private static string Plural(int count, string singular, string? plural = null) =>
        $"{count.ToString("N0", CultureInfo.InvariantCulture)} {(count == 1 ? singular : plural ?? singular + "s")}";

    /// <summary>
    /// Everything that came out of an archive or a database goes through here before it
    /// is printed. A table called [Customer] is markup to Spectre, and the sibling
    /// repository has a scar from exactly that.
    /// </summary>
    private static string Escape(string? value) => (value ?? string.Empty).EscapeMarkup();
}
