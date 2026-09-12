using System.ComponentModel;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Spectre.Console;
using Spectre.Console.Cli;
using SqlArchive.Core.Import;

namespace SqlArchive.Cli.Commands;

/// <summary>
/// Puts an archive back into a database.
/// <para>
/// The default mode is the one the whole design hangs off: restoring over a database that
/// already exists is a migration with a diff, not a drop. The schema is compared and
/// altered where that preserves rows, and each table is published through staging and a
/// swap, so a failure halfway leaves the destination as it was.
/// </para>
/// </summary>
public sealed class ImportCommand : AsyncCommand<ImportCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<ARCHIVE>")]
        [Description("The archive to restore.")]
        public string Archive { get; init; } = string.Empty;

        [CommandOption("-d|--destination <CONNECTION>")]
        [Description("Connection string of the database to restore into. It does not have to be empty, and it does not have to be new.")]
        public string Destination { get; init; } = string.Empty;

        [CommandOption("--schema-only")]
        [Description("Run the schema phases and stop. No rows are read out of the archive.")]
        public bool SchemaOnly { get; init; }

        [CommandOption("--data-only")]
        [Description("Skip the schema and publish the rows into a destination that already has the right shape. Refused where the shape does not match.")]
        public bool DataOnly { get; init; }

        [CommandOption("--table <GLOB>")]
        [Description(
            "Publish the rows of only the tables matching this glob, as schema.table. Repeatable. It selects " +
            "ROWS and not schema: the archive's schema phases are its own files, run whole, so a table left out " +
            "here is still created - and left empty. The summary says which and why.")]
        public string[] Tables { get; init; } = [];

        [CommandOption("--exclude <GLOB>")]
        [Description("Leave the rows of tables matching this glob unpublished, as schema.table. Repeatable. Applied after --table.")]
        public string[] Exclude { get; init; } = [];

        [CommandOption("--dry-run")]
        [Description("Say what would be altered and what would be published, and write nothing. The schema diff is real; the destination is not touched.")]
        public bool DryRun { get; init; }

        [CommandOption("--continue-on-error")]
        [Description(
            "Carry on with the remaining tables when one fails. Each table is published atomically by itself, so " +
            "the ones that succeeded stay published and the ones that failed are left untouched - not half loaded.")]
        public bool ContinueOnError { get; init; }

        [CommandOption("--maxdop <N>")]
        [Description("How many tables to stage at once. Default: the processor count.")]
        public int? MaxDop { get; init; }

        [CommandOption("--resume")]
        [Description("Carry on an interrupted restore by the tables that are missing, against the same fingerprint the export writes.")]
        public bool Resume { get; init; }

        [CommandOption("--work-dir <DIR>")]
        [Description(
            "Where the journal that makes --resume possible is kept. Default: beside the archive. It holds no " +
            "rows - the rows are already in the archive - only which tables are done, so a resume can pick up " +
            "without reading them again.")]
        public string? WorkDir { get; init; }

        public override ValidationResult Validate()
        {
            if(string.IsNullOrWhiteSpace(Archive))
                return ValidationResult.Error("Name the archive to restore.");

            if(string.IsNullOrWhiteSpace(Destination))
                return ValidationResult.Error("--destination is required: name the database to restore into.");

            // The two are answers to different questions, and asking both at once has no
            // meaning: one runs the schema and no rows, the other runs the rows and no
            // schema. Leaving both off is the third mode - the migration with a diff.
            if(SchemaOnly && DataOnly)
                return ValidationResult.Error(
                    "--schema-only and --data-only are opposites. Leave both off to restore as a migration: the " +
                    "schema is diffed and altered where that preserves rows, then the data is published.");

            if(MaxDop is <= 0)
                return ValidationResult.Error("--maxdop has to be at least 1.");

            return ValidationResult.Success();
        }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if(!File.Exists(settings.Archive))
        {
            AnsiConsole.MarkupLine($"[red]There is no file at '{settings.Archive.EscapeMarkup()}'.[/]");
            return ExitCodes.Failed;
        }

        var reporter = new Reporter();

        var options = new ImportOptions
        {
            ConnectionString = settings.Destination,
            Progress = reporter,
            Mode = settings.SchemaOnly ? ImportMode.SchemaOnly
                 : settings.DataOnly ? ImportMode.DataOnly
                 : ImportMode.Migrate,
            IncludeTables = settings.Tables,
            ExcludeTables = settings.Exclude,
            DryRun = settings.DryRun,
            ContinueOnError = settings.ContinueOnError,
            Resumable = settings.Resume,
            WorkingDirectory = settings.WorkDir,
            Parallelism = settings.MaxDop ?? Math.Min(Environment.ProcessorCount, 8)
        };

        try
        {
            var result = await RunAsync(settings.Archive, options, reporter).ConfigureAwait(false);

            Render(result, settings.Destination);

            // A refused table is not an exception - the design says one table refused is
            // not a restore abandoned - but it is not success either, and a script has to
            // be able to tell without reading the page.
            return result.Complete ? ExitCodes.Ok : ExitCodes.Failed;
        }
        catch(ImportException ex)
        {
            // These say what is wrong in a sentence written for whoever is holding the
            // file or the database. A stack trace on top of that helps nobody.
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[red]{ex.Message.EscapeMarkup()}[/]");
            AnsiConsole.WriteLine();
            return ExitCodes.Failed;
        }
        catch(SqlException ex)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[red]{ex.Message.EscapeMarkup()}[/]");
            AnsiConsole.WriteLine();
            return ExitCodes.Failed;
        }
    }

    /// <summary>Runs the restore behind a progress bar that says which table is moving.</summary>
    private static async Task<ImportResult> RunAsync(string archive, ImportOptions options, Reporter reporter)
    {
        ImportResult? result = null;

        await AnsiConsole.Progress()
            .Columns(
                new TaskDescriptionColumn { Alignment = Justify.Left },
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn(),
                new ElapsedTimeColumn())
            .StartAsync(async context =>
            {
                var task = context.AddTask("[dim]opening the archive[/]", autoStart: true, maxValue: 1);

                reporter.Sink = progress =>
                {
                    // The total only exists once the tables are planned, and a task whose
                    // maximum is one until then reads as a bar that jumps.
                    task.MaxValue = Math.Max(1, progress.UnitsTotal);
                    task.Value = progress.UnitsDone;
                    task.Description = Describe(progress);
                };

                result = await new DatabaseImporter(options).ImportAsync(archive).ConfigureAwait(false);

                task.Value = task.MaxValue;
                task.StopTask();
            }).ConfigureAwait(false);

        return result!;
    }

    private static string Describe(ImportProgress progress) => progress.Phase switch
    {
        "schema" => "[dim]running the schema[/]",
        "finalize" => "[dim]indexes, keys and finalize[/]",
        "data" when progress.Table is { } table =>
            $"[dim]publishing[/] {table.EscapeMarkup()} [dim]({progress.Rows.ToString("N0", CultureInfo.InvariantCulture)} rows)[/]",
        _ => "[dim]opening the archive[/]"
    };

    private static void Render(ImportResult result, string destination)
    {
        AnsiConsole.WriteLine();

        var database = Database(destination);

        AnsiConsole.Write(new Rule(
            result.DryRun
                ? $"[bold]dry run[/] [dim]- what restoring into[/] {database.EscapeMarkup()} [dim]would do[/]"
                : $"[bold]{database.EscapeMarkup()}[/]").LeftJustified());

        AnsiConsole.WriteLine();

        RenderHeader(result);
        RenderTables(result);
        RenderNotices(result);
        RenderVerdict(result);
    }

    private static void RenderHeader(ImportResult result)
    {
        var grid = new Grid()
            .AddColumn(new GridColumn().PadRight(3).NoWrap())
            .AddColumn();

        Row(grid, "Archive", $"{Path.GetFileName(result.Path).EscapeMarkup()} [dim]- {result.Manifest.Source.Database.EscapeMarkup()} on {result.Manifest.Source.Server.EscapeMarkup()}[/]");
        Row(grid, "Mode", Mode(result.Mode));
        Row(grid, "Schema", $"{Plural(result.SchemaBatches, "statement")} {(result.DryRun ? "to run" : "run")}");
        Row(grid, "Rows", result.Rows.ToString("N0", CultureInfo.InvariantCulture));
        Row(grid, "Elapsed", result.Elapsed.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture));

        AnsiConsole.Write(grid);
        AnsiConsole.WriteLine();
    }

    private static void RenderTables(ImportResult result)
    {
        if(result.Tables.Count == 0)
            return;

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Table")
            .AddColumn(new TableColumn("Rows").RightAligned())
            .AddColumn("How")
            .AddColumn("");

        foreach(var entry in result.Tables)
        {
            table.AddRow(
                entry.Identifier.EscapeMarkup(),
                entry.Rows.ToString("N0", CultureInfo.InvariantCulture),
                (entry.Publication ?? string.Empty).EscapeMarkup(),
                Outcome(entry));
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    private static void RenderNotices(ImportResult result)
    {
        foreach(var notice in result.Notices)
            AnsiConsole.MarkupLine($"[dim]-[/] {notice.EscapeMarkup()}");

        if(result.Notices.Count > 0)
            AnsiConsole.WriteLine();
    }

    /// <summary>
    /// The one line somebody reads at three in the morning. A refusal is spelled out
    /// rather than left to be counted off the table above it.
    /// </summary>
    private static void RenderVerdict(ImportResult result)
    {
        if(result.DryRun)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]Nothing was written.[/] {Plural(result.Tables.Count(t => t.Outcome == ImportTableOutcome.WouldPublish), "table")} " +
                "would be published; the schema comparison above was made against the destination as it is now.");

            AnsiConsole.WriteLine();
            return;
        }

        if(result.Complete)
        {
            AnsiConsole.MarkupLine(
                $"[green]Restored.[/] {Plural(result.Published, "table")} published, " +
                $"{Plural(result.Skipped, "table")} not - each with its reason above.");

            AnsiConsole.WriteLine();
            return;
        }

        AnsiConsole.MarkupLine(
            $"[red]{Plural(result.Refused, "table")} was not published[/], and the destination holds exactly what " +
            "it held before for each of them. Everything else in the list is restored: a table is published in " +
            "one transaction of its own, so a refusal is one table and never a half.");

        AnsiConsole.WriteLine();
    }

    private static string Outcome(ImportTableResult table) => table.Outcome switch
    {
        ImportTableOutcome.Published => "[green]published[/]",
        ImportTableOutcome.WouldPublish => "[yellow]would be published[/]",
        ImportTableOutcome.Refused => $"[red]refused[/] [dim]- {(table.Reason ?? string.Empty).EscapeMarkup()}[/]",
        ImportTableOutcome.Failed => $"[red]failed[/] [dim]- {(table.Reason ?? string.Empty).EscapeMarkup()}[/]",
        _ => $"[dim]skipped - {(table.Reason ?? string.Empty).EscapeMarkup()}[/]"
    };

    private static string Mode(ImportMode mode) => mode switch
    {
        ImportMode.SchemaOnly => "schema only [dim]- the phases, and not a row[/]",
        ImportMode.DataOnly => "data only [dim]- the rows, into a shape that was already there[/]",
        // Not "migration": the mode does not know the route. An empty destination gets the
        // archive's own phases and one with tables gets a diff, and the first notice below
        // says which of the two this run took - the heading claiming a migration above a
        // notice saying the phases ran was one line contradicting the next.
        _ => "schema and rows [dim]- the notes below say which route: the archive's own phases into a database with no tables, or a diff over one that has them[/]"
    };

    /// <summary>The destination's database name, for the heading. Never the whole connection string, which carries a password.</summary>
    private static string Database(string connectionString) =>
        ConnectionText.Describe(connectionString, "the destination");

    private static void Row(Grid grid, string label, string value) =>
        grid.AddRow($"[dim]{label}[/]", value);

    private static string Plural(int count, string noun) =>
        count == 1 ? $"1 {noun}" : $"{count.ToString("N0", CultureInfo.InvariantCulture)} {noun}s";

    /// <summary>
    /// <see cref="Progress{T}"/> would post every report through the synchronisation
    /// context, which a console does not have, so the callbacks would arrive on pool
    /// threads in whatever order they were scheduled - and a progress bar that goes
    /// backwards is worse than none.
    /// </summary>
    private sealed class Reporter : IProgress<ImportProgress>
    {
        /// <summary>Where the reports go, set once the progress bar's task exists.</summary>
        public Action<ImportProgress>? Sink { get; set; }

        public void Report(ImportProgress value) => Sink?.Invoke(value);
    }
}
