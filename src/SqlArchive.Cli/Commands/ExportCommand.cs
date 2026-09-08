using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace SqlArchive.Cli.Commands;

/// <summary>
/// Reads a database into an archive. <b>Work package 2.2; not implemented here.</b>
/// <para>
/// What lives in this file is the surface: the options, their help, and the validation
/// that can be done before a connection is opened. The engine hangs off
/// <c>SqlArchive.Core/Export/</c> when 2.2 lands, and the only change here is the body of
/// <see cref="ExecuteAsync"/>.
/// </para>
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
        [Description("How many tables to read at once. Default: the processor count. A database snapshot keeps this; SNAPSHOT isolation costs it.")]
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

            return ValidationResult.Success();
        }
    }

    // El motor ya existe: SqlArchive.Core.Export, entregado por WP 2.2 y probado contra un
    // servidor real. Lo que falta es este cable, y el mapeo es directo:
    //
    //     --source        -> ExportRequest.ConnectionString
    //     --out           -> el fichero que abre ArchiveWriter
    //     --table         -> IncludeTables          --exclude   -> ExcludeTables
    //     --where T=PRED  -> RowFilters             --consistent-> Consistency
    //     --schema-only   -> ExcludeData = ["*"]    --range-size-> RowsPerRange
    //     --spool         -> WorkingDirectory       --resume    -> Resumable
    //
    // Un matiz que no se ve en la tabla y que costaria una tarde descubrir: el --maxdop de
    // este comando dice "tablas a la vez", y el Parallelism del motor cuenta *unidades*
    // -- rangos de tabla. Son lo mismo solo mientras ninguna tabla se parta, y contarlo
    // por tablas es justo lo que impide que una tabla grande use el paralelismo.
    public override Task<int> ExecuteAsync(CommandContext context, Settings settings) =>
        throw new NotBuiltYetException(
            "export",
            "2.2",
            "The engine that reads a database into an archive is built and tested - the filters, the ranges, " +
            "the resumable spool, the consistency modes and the hashes. What is missing is the wiring between " +
            "this command and it, and the mapping is written in a comment beside this line.");
}
