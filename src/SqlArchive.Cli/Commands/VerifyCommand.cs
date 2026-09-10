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
    private const int HashPreview = 12;

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

        var options = new VerifyOptions
        {
            ConnectionString = settings.Against,
            IncludeTables = settings.Tables,
            ExcludeTables = settings.Exclude,
            SchemaOnly = settings.SchemaOnly,
            Parallelism = settings.MaxDop ?? Environment.ProcessorCount,
            CommandTimeoutSeconds = settings.Timeout ?? 0
        };

        VerifyReport report;

        try
        {
            report = await new ArchiveVerifier(options)
                .VerifyAsync(settings.Archive, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch(SqlException failed)
        {
            // The database side could not be reached or could not be read. That is a run
            // that did not happen, not a comparison that found something, so it does not
            // get the exit code that means drift.
            AnsiConsole.MarkupLine($"[red]{failed.Message.EscapeMarkup()}[/]");
            return ExitCodes.Failed;
        }

        if(settings.Json is { Length: > 0 } json)
            await VerifyJson.WriteAsync(report, json, CancellationToken.None).ConfigureAwait(false);

        Render(report, settings.Json);

        return report.HasDifferences ? ExitCodes.Differences : ExitCodes.Ok;
    }

    private static void Render(VerifyReport report, string? jsonPath)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule($"[bold]{Escape(Path.GetFileName(report.Archive))}[/]").LeftJustified());
        AnsiConsole.WriteLine();

        RenderHeader(report);
        RenderIntegrity(report.Integrity);
        RenderSchema(report.Schema);
        RenderTables(report);
        RenderNotices(report.Notices);
        RenderVerdict(report);

        if(jsonPath is { Length: > 0 })
            AnsiConsole.MarkupLine($"[dim]The same verdict, as JSON, is in {Escape(jsonPath)}.[/]");
    }

    private static void RenderHeader(VerifyReport report)
    {
        var grid = new Grid()
            .AddColumn(new GridColumn().PadRight(3).NoWrap())
            .AddColumn();

        Row(grid, "Archive", Escape(report.Archive));

        Row(grid, "Compared with", report.Database is { Length: > 0 } database
            ? Escape(database)
            : "[dim]nothing - the archive was checked against itself and no server was touched[/]");

        Row(grid, "Took", Escape(report.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)) + " s");

        AnsiConsole.Write(grid);
        AnsiConsole.WriteLine();
    }

    private static void RenderIntegrity(IntegrityVerdict integrity)
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

        AnsiConsole.MarkupLine($"[bold]Integrity[/]  {string.Join(", ", parts)}");

        var wrong = integrity.Entries
            .Where(e => e.State is EntryState.Corrupt or EntryState.Missing or EntryState.Undeclared)
            .ToArray();

        foreach(var entry in wrong)
            AnsiConsole.MarkupLine($"           {EntryLine(entry)}");

        AnsiConsole.WriteLine();
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

    private static void RenderSchema(SchemaVerdict? schema)
    {
        if(schema is null)
            return;

        if(schema.Matches)
        {
            AnsiConsole.MarkupLine("[bold]Schema[/]  [green]the two describe the same objects[/]");
        }
        else
        {
            AnsiConsole.MarkupLine("[bold]Schema[/]");

            List(schema.OnlyInArchive, "only in the archive");
            List(schema.OnlyInDatabase, "only in the database");
            List(schema.Differing, "different on the two sides");
        }

        foreach(var ignored in schema.Ignored)
            AnsiConsole.MarkupLine($"        [dim]not compared: {Escape(ignored)}[/]");

        AnsiConsole.WriteLine();

        static void List(IReadOnlyList<string> objects, string what)
        {
            if(objects.Count == 0)
                return;

            AnsiConsole.MarkupLine($"        [yellow]{Plural(objects.Count, "object")} {Escape(what)}:[/] " +
                                   Escape(string.Join(", ", objects.Order(StringComparer.OrdinalIgnoreCase))));
        }
    }

    private static void RenderTables(VerifyReport report)
    {
        if(report.Tables.Count == 0)
            return;

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey35);

        table.AddColumn("Table");
        table.AddColumn("Verdict");
        table.AddColumn(new TableColumn("Archive").RightAligned());
        table.AddColumn(new TableColumn("Database").RightAligned());
        table.AddColumn("What differs");

        foreach(var verdict in report.Tables)
        {
            table.AddRow(
                // [dbo].[Customer] is markup to Spectre and a table name to everyone
                // else. Escaping is not optional anywhere in this file.
                Escape(verdict.Identifier),
                Verdict(verdict.Outcome),
                Side(verdict.ArchiveRows, verdict.ArchiveHash),
                Side(verdict.DatabaseRows, verdict.DatabaseHash),
                Detail(verdict));
        }

        AnsiConsole.Write(table);

        var summary = new List<string> { $"[green]{Plural(report.Matching, "table")} match[/]" };

        if(report.Differing > 0)
            summary.Add($"[red]{Plural(report.Differing, "table")} differ[/]");

        if(report.Unverifiable > 0)
            summary.Add($"[yellow]{Plural(report.Unverifiable, "table")} the manifest cannot answer for[/]");

        AnsiConsole.MarkupLine(string.Join(", ", summary));
        AnsiConsole.WriteLine();
    }

    /// <summary>One side of a table's comparison: how many rows, and what they hash to.</summary>
    private static string Side(long? rows, string? hash)
    {
        if(rows is null)
            return "[dim]-[/]";

        var count = rows.Value.ToString("N0", CultureInfo.InvariantCulture);

        return hash is { Length: > 0 }
            ? $"{count} [dim]{Escape(Short(hash))}[/]"
            : count;
    }

    private static string Detail(TableVerdict verdict)
    {
        var lines = verdict.Differences.Select(Escape).ToList();

        if(verdict.Limitation is { Length: > 0 } limitation)
            lines.Add($"[dim]{Escape(limitation)}[/]");

        return string.Join(Environment.NewLine, lines);
    }

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

    private static void RenderNotices(IReadOnlyList<string> notices)
    {
        foreach(var notice in notices)
            AnsiConsole.MarkupLine($"[dim]{Escape(notice)}[/]");

        if(notices.Count > 0)
            AnsiConsole.WriteLine();
    }

    /// <summary>
    /// The one line somebody reads. It says which of the two answers this was, because
    /// "found differences" and "could not compare" leave by different doors and the exit
    /// code is what a script branches on.
    /// </summary>
    private static void RenderVerdict(VerifyReport report)
    {
        if(!report.HasDifferences)
        {
            AnsiConsole.MarkupLine(report.Database is { Length: > 0 } matched
                ? $"[green]No differences. {Escape(matched)} is what this archive says it is.[/]"
                : "[green]No differences. Every entry hashes to what the manifest declares.[/]");

            return;
        }

        AnsiConsole.MarkupLine(
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
