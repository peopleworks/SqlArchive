using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace SqlArchive.Cli.Commands;

/// <summary>
/// Puts an archive back into a database. <b>Work package 2.3; not implemented here.</b>
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
        [Description("Restore only tables matching this glob, as schema.table. Repeatable.")]
        public string[] Tables { get; init; } = [];

        [CommandOption("--exclude <GLOB>")]
        [Description("Leave out tables matching this glob, as schema.table. Repeatable. Applied after --table.")]
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

    public override Task<int> ExecuteAsync(CommandContext context, Settings settings) =>
        throw new NotBuiltYetException(
            "import",
            "2.3",
            "Restoring - the three modes, the migration with a diff, and the exact row guard that refuses to " +
            "publish a table whose count and hash do not match the manifest - depends on 2.2 being able to " +
            "write an archive first.");
}
