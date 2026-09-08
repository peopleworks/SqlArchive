using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace SqlArchive.Cli.Commands;

/// <summary>
/// Answers whether two things match. <b>Work package 2.4; not implemented here.</b>
/// <para>
/// One manifest answers three questions with the same code: is the archive intact, does a
/// restored database match it, and has a live database drifted from it. The third is the
/// audit use, and it is this same command pointed somewhere else.
/// </para>
/// </summary>
public sealed class VerifyCommand : AsyncCommand<VerifyCommand.Settings>
{
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
        [Description("Write the verdict as JSON as well, for a build to read. The readable report still goes to the console.")]
        public string? Json { get; init; }

        [CommandOption("--maxdop <N>")]
        [Description("How many tables to read at once. Default: the processor count.")]
        public int? MaxDop { get; init; }

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

            return ValidationResult.Success();
        }
    }

    public override Task<int> ExecuteAsync(CommandContext context, Settings settings) =>
        throw new NotBuiltYetException(
            "verify",
            "2.4",
            "The verdict - which is the point of the tool, because it sees an UPDATE that a row count cannot - " +
            "needs an archive that 2.2 wrote before it has anything to verify.");
}
