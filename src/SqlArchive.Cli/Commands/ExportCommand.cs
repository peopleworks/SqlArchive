using System.ComponentModel;
using System.Data.Common;
using System.Globalization;
using Spectre.Console;
using Spectre.Console.Cli;
using SqlArchive.Core.Export;
using SqlArchive.Core.Format;

namespace SqlArchive.Cli.Commands;

/// <summary>
/// Reads a database into an archive, through <c>SqlArchive.Core.Export.DatabaseExporter</c>
/// - work package 2.2, built and tested against a real server. This file is the wiring:
/// turning what an operator typed into an <see cref="ExportOptions"/>, driving a progress
/// display while the engine runs, and reporting what the manifest ended up saying.
/// </summary>
public sealed class ExportCommand : AsyncCommand<ExportCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-s|--source <CONNECTION>")]
        [Description("Connection string of the database to archive.")]
        public string Source { get; init; } = string.Empty;

        [CommandOption("-o|--out <FILE>")]
        [Description("The archive to write. The conventional extension is .sqlarchive; it is not enforced.")]
        public string Out { get; init; } = string.Empty;

        [CommandOption("--table <GLOB>")]
        [Description("Archive only tables matching this glob, as schema.table. Repeatable. Default: every table.")]
        public string[] Tables { get; init; } = [];

        [CommandOption("--exclude <GLOB>")]
        [Description("Leave out tables matching this glob, as schema.table. Repeatable. Applied after --table.")]
        public string[] Exclude { get; init; } = [];

        [CommandOption("--where <TABLE=PREDICATE>")]
        [Description(
            "Archive only the rows of one table that match a predicate, as dbo.Order=Total>0. Repeatable. " +
            "The predicate is written into the manifest as that table's rowFilter, because an archive that is " +
            "partial and does not say so makes a later verify report differences that are not differences.")]
        public string[] Where { get; init; } = [];

        [CommandOption("--schema-only")]
        [Description(
            "Archive the schema and none of the rows. Every table is marked dataSkipped in the manifest, so a " +
            "verify against the archive knows the rows are absent on purpose.")]
        public bool SchemaOnly { get; init; }

        [CommandOption("--consistent")]
        [Description(
            "Read every table at one point in time. Tries a database snapshot first, then a single connection " +
            "under SNAPSHOT isolation. If neither is available it REFUSES and says which permission or setting " +
            "is missing - it does not quietly fall back to reading table by table, because an archive that was " +
            "asked to be consistent and silently is not is worse than a failed export. The mode that was used " +
            "is recorded in the manifest.")]
        public bool Consistent { get; init; }

        [CommandOption("--maxdop <N>")]
        [Description(
            "How many units are read at once - a table read whole is one unit, and each range of a table that " +
            "was split into ranges is one more, so a single large table can use every bit of this and not just " +
            "one slot of it. Default: the processor count, capped at 8. A database snapshot keeps this " +
            "parallelism; SNAPSHOT isolation costs it, because it reads through a single connection.")]
        public int? MaxDop { get; init; }

        [CommandOption("--range-size <ROWS>")]
        [Description(
            "Split a large table into range files of about this many rows, which is what lets it be read in " +
            "parallel. Only possible where there is a numeric or date key to partition by; a table without one " +
            "is read whole whatever this says.")]
        public long? RangeSize { get; init; }

        [CommandOption("--spool <DIR>")]
        [Description("Where the partial export is spooled. Default: beside --out.")]
        public string? Spool { get; init; }

        [CommandOption("--resume")]
        [Description(
            "Carry on an export that was interrupted, doing the tables that are missing. The spool carries a " +
            "fingerprint of the connection, the filters and the format version; resuming with different ones is " +
            "refused rather than mixed into one archive that came from two different runs.")]
        public bool Resume { get; init; }

        public override ValidationResult Validate()
        {
            if(string.IsNullOrWhiteSpace(Source))
                return ValidationResult.Error("--source is required: there is nothing to archive without a database to read.");

            if(string.IsNullOrWhiteSpace(Out))
                return ValidationResult.Error("--out is required: name the archive to write.");

            if(SchemaOnly && Where.Length > 0)
                return ValidationResult.Error(
                    "--schema-only and --where cannot go together: --where filters rows, and --schema-only archives none.");

            if(RangeSize is <= 0)
                return ValidationResult.Error("--range-size has to be a positive number of rows.");

            if(MaxDop is <= 0)
                return ValidationResult.Error("--maxdop has to be at least 1.");

            foreach(var where in Where)
            {
                if(!TryParseWhere(where, out _, out _, out var error))
                    return ValidationResult.Error(error!);
            }

            return ValidationResult.Success();
        }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        ExportResult? result = null;

        try
        {
            await AnsiConsole.Progress()
                .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn(), new ElapsedTimeColumn())
                .StartAsync(async ctx =>
                {
                    var task = ctx.AddTask("Reading the schema");
                    task.IsIndeterminate = true;

                    var progress = new Progress<ExportProgress>(p =>
                    {
                        task.Description = Describe(p);

                        if(p.UnitsTotal > 0)
                        {
                            task.IsIndeterminate = false;
                            task.MaxValue = p.UnitsTotal;
                            task.Value = p.UnitsDone;
                        }
                    });

                    var options = ToExportOptions(settings, progress);

                    result = await new DatabaseExporter(options)
                        .ExportAsync(settings.Out, CancellationToken.None)
                        .ConfigureAwait(false);

                    task.Value = task.MaxValue;
                    task.StopTask();
                }).ConfigureAwait(false);
        }
        catch(ExportException failure)
        {
            // ExportConsistencyException (a --consistent that could not be honoured) and
            // ExportResumeException (a spool from a run with different parameters) both
            // land here: the engine refusing on purpose rather than a bug, and the
            // message is already written for the operator holding the terminal. A stack
            // trace on top of it would bury the one sentence that tells them what to do.
            AnsiConsole.MarkupLine($"[red]{Escape(failure.Message)}[/]");
            return ExitCodes.Failed;
        }
        catch(DbException failure)
        {
            AnsiConsole.MarkupLine($"[red]Could not read '{Escape(settings.Source)}': {Escape(failure.Message)}[/]");
            return ExitCodes.Failed;
        }

        Render(result!);
        return ExitCodes.Ok;
    }

    // El motor cuenta *unidades* en Parallelism - un rango de una tabla partida es una
    // unidad, no la tabla entera - así que --maxdop se pasa tal cual a Parallelism. La
    // alternativa, traducir "N tablas a la vez" contando tablas y no rangos, es
    // precisamente lo que le quitaría a una tabla grande partida en rangos el
    // paralelismo que --range-size existe para darle: una tabla de dieciséis rangos
    // leída con --maxdop 8 se leería en dos oleadas de ocho unidades en vez de,
    // erróneamente, una tabla a la vez. La ayuda de --maxdop queda dicha en esos
    // términos y no en "tablas a la vez".
    //
    // El comentario que estaba aquí traía dos nombres que no son los del código:
    // ExportOptions (no "ExportRequest") es el tipo que recibe DatabaseExporter, y su
    // propiedad para --consistent se llama Consistent (no "Consistency" - Consistency
    // es de solo lectura y la decide ConsistencySession, no quien construye las
    // opciones).
    internal static ExportOptions ToExportOptions(Settings settings, IProgress<ExportProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var defaults = new ExportOptions { ConnectionString = settings.Source };

        return new ExportOptions
        {
            ConnectionString = settings.Source,
            IncludeTables = settings.Tables,
            ExcludeTables = settings.Exclude,
            ExcludeData = settings.SchemaOnly ? ["*"] : [],
            RowFilters = ParseWhereClauses(settings.Where),
            Consistent = settings.Consistent,
            WorkingDirectory = settings.Spool,
            Resumable = settings.Resume,
            Progress = progress,
            Parallelism = settings.MaxDop ?? defaults.Parallelism,
            RowsPerRange = settings.RangeSize ?? defaults.RowsPerRange
        };
    }

    /// <summary>Every <c>--where</c> entry, parsed. Assumes <see cref="Settings.Validate"/> already checked them.</summary>
    internal static List<KeyValuePair<string, string>> ParseWhereClauses(IEnumerable<string> where)
    {
        var filters = new List<KeyValuePair<string, string>>();

        foreach(var entry in where)
        {
            if(!TryParseWhere(entry, out var table, out var predicate, out var error))
                throw new InvalidOperationException($"Unreachable if Validate ran first: {error}");

            filters.Add(new KeyValuePair<string, string>(table, predicate));
        }

        return filters;
    }

    /// <summary>
    /// Splits <c>TABLE=PREDICATE</c> on its <b>first</b> '=' and not any later one, because
    /// a predicate is most often itself an equality - <c>dbo.Order=Status='Active'</c> has
    /// to keep <c>Status='Active'</c> whole.
    /// </summary>
    internal static bool TryParseWhere(string spec, out string table, out string predicate, out string? error)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var separator = spec.IndexOf('=', StringComparison.Ordinal);

        if(separator <= 0)
        {
            table = string.Empty;
            predicate = string.Empty;
            error = $"--where '{spec}' has to be TABLE=PREDICATE - a table, then '=', then a predicate.";
            return false;
        }

        table = spec[..separator].Trim();
        predicate = spec[(separator + 1)..].Trim();

        if(predicate.Length == 0)
        {
            error = $"--where '{spec}' names no predicate after the '='.";
            return false;
        }

        error = null;
        return true;
    }

    private static string Describe(ExportProgress progress)
    {
        var text = progress.Table is null ? progress.Phase : $"{progress.Phase} {progress.Table}";
        return Escape(text);
    }

    private static void Render(ExportResult result)
    {
        var manifest = result.Manifest;

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule($"[bold]{Escape(Path.GetFileName(result.Path))}[/]").LeftJustified());
        AnsiConsole.WriteLine();

        var grid = new Grid()
            .AddColumn(new GridColumn().PadRight(3).NoWrap())
            .AddColumn();

        grid.AddRow("[dim]Tables[/]", Plural(result.Tables, "table"));
        grid.AddRow("[dim]Rows[/]", result.Rows.ToString("N0", CultureInfo.InvariantCulture));
        grid.AddRow("[dim]Consistency[/]", Escape(ConsistencyName(result.Consistency)));
        grid.AddRow("[dim]File[/]", $"{Escape(Size(result.Bytes))} at {Escape(result.Path)}");
        grid.AddRow("[dim]Elapsed[/]", Escape(result.Elapsed.ToString("g", CultureInfo.InvariantCulture)));

        AnsiConsole.Write(grid);
        AnsiConsole.WriteLine();

        var skipped = manifest.Tables.Count(t => t.DataSkipped);
        var filtered = manifest.Tables.Count(t => !string.IsNullOrEmpty(t.RowFilter));

        if(skipped > 0)
            AnsiConsole.MarkupLine($"[dim]{Plural(skipped, "table")} archived with no rows, on purpose.[/]");

        if(filtered > 0)
            AnsiConsole.MarkupLine($"[dim]{Plural(filtered, "table")} carrying a row filter.[/]");

        if(skipped > 0 || filtered > 0)
            AnsiConsole.WriteLine();

        foreach(var notice in result.Notices)
            AnsiConsole.MarkupLine($"[yellow]-[/] {Escape(notice)}");

        if(result.Notices.Count > 0)
            AnsiConsole.WriteLine();
    }

    private static string ConsistencyName(ArchiveConsistency consistency) => consistency switch
    {
        ArchiveConsistency.Snapshot => "snapshot - every table read from the same instant",
        ArchiveConsistency.SnapshotIsolation => "snapshot isolation - every table read from the same instant, through one connection",
        _ => "per-table - each table read on its own, no shared instant"
    };

    private static string Plural(int count, string singular) =>
        $"{count.ToString("N0", CultureInfo.InvariantCulture)} {(count == 1 ? singular : singular + "s")}";

    private static string Size(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;

        while(value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes.ToString("N0", CultureInfo.InvariantCulture)} B"
            : string.Create(CultureInfo.InvariantCulture, $"{value:0.#} {units[unit]}");
    }

    /// <summary>
    /// Everything printed through Spectre goes through here. A table or server called
    /// [PROD] is markup to Spectre and a name to everyone else.
    /// </summary>
    private static string Escape(string? value) => (value ?? string.Empty).EscapeMarkup();
}
