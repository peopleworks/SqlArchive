using System.ComponentModel;
using System.Globalization;
using System.IO.Compression;
using Spectre.Console;
using Spectre.Console.Cli;
using SqlArchive.Core.Format;

namespace SqlArchive.Cli.Commands;

/// <summary>
/// Says what an archive says about itself, without unpacking a byte of it.
/// <para>
/// A zip keeps its directory at the end, so listing the entries and reading the manifest
/// cost one seek and a few kilobytes however large the archive is. That is what makes this
/// instant on a hundred gigabytes, and it is why <c>inspect</c> is the verb that answers
/// "what is this file?" rather than <c>verify</c>, which has to read everything.
/// </para>
/// <para>
/// It reads dbdumper's archives too, and says out loud what they cannot tell you: with no
/// per-table row hash, a verify against one compares the schema and the row counts and
/// stops there.
/// </para>
/// </summary>
public sealed class InspectCommand : AsyncCommand<InspectCommand.Settings>
{
    /// <summary>How much of a 64-character hash the table shows. --json prints them whole.</summary>
    private const int HashPreview = 16;

    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<ARCHIVE>")]
        [Description(
            "The archive to read: a .sqlarchive file, an archive dbdumper wrote, a directory holding a " +
            "manifest.json, or a manifest.json by itself.")]
        public string Archive { get; init; } = string.Empty;

        [CommandOption("-e|--entries")]
        [Description("List every entry with its packed and unpacked size, and whether the manifest accounts for it.")]
        public bool Entries { get; init; }

        [CommandOption("--json")]
        [Description("Write the manifest to standard output exactly as it is in the archive, and nothing else. This is the way to get the hashes in full.")]
        public bool Json { get; init; }

        public override ValidationResult Validate()
        {
            if(string.IsNullOrWhiteSpace(Archive))
                return ValidationResult.Error("Name the archive to inspect.");

            if(!File.Exists(Archive) && !Directory.Exists(Archive))
                return ValidationResult.Error($"There is no file or directory at '{Archive}'.");

            return ValidationResult.Success();
        }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        using var target = await Target.OpenAsync(settings.Archive, CancellationToken.None).ConfigureAwait(false);

        if(settings.Json)
        {
            // Straight to the console's own writer rather than through Spectre: this is
            // meant to be piped into jq, and a renderer that wraps lines and reads square
            // brackets as markup would ruin it.
            Console.Out.Write(target.RawManifest);
            Console.Out.Flush();
            return ExitCodes.Ok;
        }

        Render(target, settings.Entries);
        return ExitCodes.Ok;
    }

    private static void Render(Target target, bool listEntries)
    {
        var manifest = target.Manifest;

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule($"[bold]{Escape(target.DisplayName)}[/]").LeftJustified());
        AnsiConsole.WriteLine();

        RenderWarnings(target);
        RenderHeader(target);
        RenderSchema(manifest);
        RenderTables(manifest);
        RenderOmittedColumns(manifest);
        RenderEntries(target, listEntries);
    }

    /// <summary>
    /// Everything that changes what the rest of the page means, before the rest of the
    /// page. A partial archive read as a complete one is the failure this whole format
    /// was shaped to avoid.
    /// </summary>
    private static void RenderWarnings(Target target)
    {
        var manifest = target.Manifest;

        if(target.IsDbDumper)
        {
            Warn(
                "dbdumper archive",
                "This was written by dbdumper, not by SqlArchive, and its manifest carries no per-table row hash. " +
                "Everything below comes from mapping it onto a schema snapshot, which is enough to diff and to " +
                "restore as a migration. A verify against it can compare the schema and the row counts and " +
                "nothing further - it cannot see an UPDATE, because there is no hash to compare. " +
                "SqlArchive does not write this format for that reason.");
        }

        if(manifest.RequiresNewerReader)
        {
            Warn(
                $"format version {manifest.FormatVersion.ToString(CultureInfo.InvariantCulture)}",
                $"This build reads version {ArchiveFormat.CurrentVersion.ToString(CultureInfo.InvariantCulture)}. " +
                "Describing the archive is deliberately still allowed - an archive exists to be opened by whoever " +
                "is there when the source is not - so what follows is what this build can still make out. " +
                "Restoring data from it is a different matter and is refused.");
        }

        if(manifest.Extensions is { Count: > 0 })
        {
            var names = string.Join(", ", manifest.Extensions.Keys.Order(StringComparer.Ordinal));

            Warn(
                "properties this build does not know",
                $"The manifest carries {manifest.Extensions.Count.ToString(CultureInfo.InvariantCulture)} " +
                $"top-level properties that mean nothing to this version: {names}. They are kept rather than " +
                "dropped, so nothing is lost by reading the file here.");
        }

        var skipped = manifest.Tables.Count(t => t.DataSkipped);
        var filtered = manifest.Tables.Count(t => !string.IsNullOrEmpty(t.RowFilter));
        var partial = new List<string>();

        if(filtered > 0)
            partial.Add($"{Plural(filtered, "table")} carrying a row filter");

        if(skipped > 0)
            partial.Add($"{Plural(skipped, "table")} archived with no rows at all");

        if(partial.Count > 0)
        {
            Warn(
                "this archive is partial",
                $"Some of what it describes it does not contain: {string.Join(" and ", partial)}. " +
                $"The manifest records {(partial.Count == 1 ? "that" : "both")}, so a verify knows the missing " +
                "rows are absent on purpose and does not report them as drift, and a restore will not put back " +
                "what was never taken.");
        }
    }

    private static void RenderHeader(Target target)
    {
        var manifest = target.Manifest;
        var source = manifest.Source;

        var grid = new Grid()
            .AddColumn(new GridColumn().PadRight(3).NoWrap())
            .AddColumn();

        // For one of dbdumper's, the manifest on this side is a mapping and its Format
        // and FormatVersion are ours, not the file's. Saying "sqlarchive, version 1" over
        // a file that is neither would be the one kind of lie this tool must not tell.
        Row(grid, "Format", target.Original is { } original
            ? $"dbdumper, version {original.FormatVersion.ToString(CultureInfo.InvariantCulture)}"
            : $"{Escape(manifest.Format)}, version {manifest.FormatVersion.ToString(CultureInfo.InvariantCulture)}");

        Row(grid, "Written by", Tool(manifest.Tool));

        Row(grid, "Created", manifest.CreatedAt == default
            ? "[dim]not recorded[/]"
            : Escape(manifest.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)));

        Row(grid, "Consistency", target.IsDbDumper
            ? "per-table [dim]- dbdumper does not record a mode, so this is the reading that claims least[/]"
            : $"{Escape(ConsistencyName(manifest.Consistency))} [dim]- {Escape(ConsistencyMeaning(manifest.Consistency))}[/]");
        Row(grid, "Server", Join(source.Server, source.Edition, source.ProductVersion));
        Row(grid, "Database", Join(source.Database, source.Collation));

        if(target.FileLength is { } length)
            Row(grid, "File", $"{Escape(Size(length))} on disk");

        AnsiConsole.Write(grid);
        AnsiConsole.WriteLine();
    }

    private static void RenderSchema(ArchiveManifest manifest)
    {
        if(manifest.Schema is not { } schema)
        {
            AnsiConsole.MarkupLine("[yellow]The manifest carries no schema snapshot, so there is nothing here to diff against.[/]");
            AnsiConsole.WriteLine();
            return;
        }

        var byType = schema.Objects
            .GroupBy(o => o.Type)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key.ToString(), StringComparer.Ordinal)
            .Select(g => Plural(g.Count(), Words(g.Key.ToString())))
            .ToArray();

        var parts = new List<string> { Plural(schema.Schemas.Count, "schema", "schemas") };

        if(schema.Types.Count > 0)
            parts.Add(Plural(schema.Types.Count, "alias type"));

        AnsiConsole.MarkupLine($"[bold]Schema[/]  [dim]{Escape(string.Join(", ", parts))}[/]");

        AnsiConsole.MarkupLine(byType.Length == 0
            ? "        [dim]no objects[/]"
            : $"        {Escape(string.Join(", ", byType))}");

        AnsiConsole.WriteLine();
    }

    private static void RenderTables(ArchiveManifest manifest)
    {
        if(manifest.Tables.Count == 0)
        {
            AnsiConsole.MarkupLine("[dim]The manifest lists no tables.[/]");
            AnsiConsole.WriteLine();
            return;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey35);

        table.AddColumn("Table");
        table.AddColumn(new TableColumn("Rows").RightAligned());
        table.AddColumn(new TableColumn("Row hash").NoWrap());
        table.AddColumn(new TableColumn("Files").RightAligned());
        table.AddColumn("Notes");

        // On a dbdumper archive not one table has a hash, and the banner above has
        // already said why. Repeating it on every row would bury the note that is
        // specific to a table - the row filter - under one that is not.
        var hashless = manifest.Tables.Count > 0 && manifest.Tables.All(t => string.IsNullOrEmpty(t.RowHash));

        // Which table is whose history is recorded on the parent in the schema, not on the
        // entry; this is where a person reading the list gets it said.
        var parents = manifest.Schema is { } schema
            ? HistoryLink.Parents(schema)
            : new Dictionary<string, SqlSchemaDiff.Models.TableModel>();

        long rows = 0;

        foreach(var entry in manifest.Tables.OrderBy(t => t.Schema, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
        {
            rows += entry.RowCount;

            table.AddRow(
                // Identifier is [dbo].[Customer], which is markup to Spectre and a table
                // name to everyone else. Escaping is not optional anywhere in this file.
                Escape(entry.Identifier),
                entry.DataSkipped ? "[dim]-[/]" : entry.RowCount.ToString("N0", CultureInfo.InvariantCulture),
                HashCell(entry),
                entry.DataFiles.Count.ToString("N0", CultureInfo.InvariantCulture),
                Notes(entry, hashless, parents.GetValueOrDefault($"{entry.Schema}.{entry.Name}")));
        }

        AnsiConsole.Write(table);

        // The footnote about truncated hashes only earns its line when there are hashes.
        var truncated = hashless
            ? string.Empty
            : $" Row hashes are shown to {HashPreview.ToString(CultureInfo.InvariantCulture)} of 64 characters; " +
              "--json prints the manifest in full.";

        AnsiConsole.MarkupLine(
            $"[dim]{Plural(manifest.Tables.Count, "table")}, " +
            $"{rows.ToString("N0", CultureInfo.InvariantCulture)} rows declared.{Escape(truncated)}[/]");

        AnsiConsole.WriteLine();
    }

    /// <summary>
    /// Which columns the archive does not carry, and why. A person reading the JSONL and
    /// counting fewer columns than the CREATE TABLE deserves the answer here rather than
    /// in the source.
    /// </summary>
    private static void RenderOmittedColumns(ArchiveManifest manifest)
    {
        var omitted = manifest.Tables
            .SelectMany(t => t.OmittedColumns.Select(c => (Table: t.Identifier, Column: c.Key, Why: c.Value)))
            .ToArray();

        if(omitted.Length == 0)
            return;

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey35)
            .Title("[bold]Columns the archive does not carry[/]");

        table.AddColumn("Table");
        table.AddColumn("Column");
        table.AddColumn("Why");

        foreach(var (name, column, why) in omitted)
            table.AddRow(Escape(name), Escape(column), Escape(why));

        AnsiConsole.Write(table);

        AnsiConsole.MarkupLine(
            "[dim]The server writes these itself and refuses to be told what they are, so carrying them would " +
            "make a verify after a restore impossible to pass.[/]");

        AnsiConsole.WriteLine();
    }

    private static void RenderEntries(Target target, bool listEntries)
    {
        if(target.Entries.Count == 0)
        {
            AnsiConsole.MarkupLine("[dim]This is a manifest on its own; there are no entries to list.[/]");
            AnsiConsole.WriteLine();
            return;
        }

        var declared = target.Manifest.DeclaredEntries().ToHashSet(StringComparer.Ordinal);

        if(listEntries)
        {
            var table = new Table()
                .Border(TableBorder.Rounded)
                .BorderColor(Color.Grey35);

            table.AddColumn("Entry");
            table.AddColumn(new TableColumn("Unpacked").RightAligned());
            table.AddColumn(new TableColumn("Packed").RightAligned());
            table.AddColumn("Manifest");

            foreach(var entry in target.Entries.OrderBy(e => e.Name, StringComparer.Ordinal))
            {
                var state = entry.Name == ArchiveFormat.ManifestEntry
                    ? "[dim]itself[/]"
                    : declared.Contains(entry.Name) ? "declared" : "[yellow]not declared[/]";

                table.AddRow(
                    Escape(entry.Name),
                    Escape(Size(entry.Length)),
                    entry.CompressedLength is { } packed ? Escape(Size(packed)) : "[dim]-[/]",
                    state);
            }

            AnsiConsole.Write(table);
        }

        var unpacked = target.Entries.Sum(e => e.Length);

        // A directory listing has no packed size, and reporting one as zero would read
        // as a compression ratio of infinity rather than as an absence.
        var packedTotal = target.Entries.Any(e => e.CompressedLength is not null)
            ? $", {Size(target.Entries.Sum(e => e.CompressedLength ?? 0))} packed"
            : string.Empty;

        AnsiConsole.MarkupLine(
            $"[dim]{Plural(target.Entries.Count, "entry", "entries")}, " +
            $"{Escape(Size(unpacked))} unpacked{Escape(packedTotal)}.[/]");

        // Only meaningful for one of ours: dbdumper's manifest has no file hashes at all,
        // so every entry would come back "not declared" and the line would be noise.
        if(!target.IsDbDumper)
        {
            var present = target.Entries
                .Select(e => e.Name)
                .Where(n => n != ArchiveFormat.ManifestEntry)
                .ToHashSet(StringComparer.Ordinal);

            var missing = declared.Except(present).Order(StringComparer.Ordinal).ToArray();
            var extra = present.Except(declared).Order(StringComparer.Ordinal).ToArray();

            if(missing.Length > 0)
                AnsiConsole.MarkupLine($"[red]The manifest declares entries that are not in the archive:[/] {Escape(string.Join(", ", missing))}");

            if(extra.Length > 0)
                AnsiConsole.MarkupLine($"[yellow]The archive holds entries the manifest does not account for:[/] {Escape(string.Join(", ", extra))}");

            if(missing.Length == 0 && extra.Length == 0)
                AnsiConsole.MarkupLine("[dim]The manifest accounts for every entry exactly once. Whether the bytes still hash to what it says is what verify answers.[/]");
        }

        AnsiConsole.WriteLine();
    }

    private static string HashCell(ArchiveTableEntry entry)
    {
        if(entry.DataSkipped)
            return "[dim]no data[/]";

        if(string.IsNullOrEmpty(entry.RowHash))
            return "[yellow]none[/]";

        return entry.RowHash.Length <= HashPreview
            ? Escape(entry.RowHash)
            : Escape(entry.RowHash[..HashPreview]) + "[dim]...[/]";
    }

    private static string Notes(ArchiveTableEntry entry, bool wholeArchiveHasNoHashes, SqlSchemaDiff.Models.TableModel? parent)
    {
        var notes = new List<string>();

        if(parent is not null)
            notes.Add($"[dim]history of[/] {Escape($"[{parent.Schema}].[{parent.Name}]")}");

        if(entry.DataSkipped)
            notes.Add("[yellow]schema only, rows not archived[/]");

        if(!string.IsNullOrEmpty(entry.RowFilter))
            notes.Add($"[yellow]filtered:[/] {Escape(entry.RowFilter)}");

        if(!entry.DataSkipped && !wholeArchiveHasNoHashes && string.IsNullOrEmpty(entry.RowHash))
            notes.Add("[yellow]no row hash: only the count can be verified[/]");

        return notes.Count == 0 ? string.Empty : string.Join("  ", notes);
    }

    /// <summary>
    /// dbdumper writes its tool name and version as one free string, so pairing it with
    /// the name this build put beside it gives "dbdumper dbdumper 0.9.0".
    /// </summary>
    private static string Tool(ArchiveTool tool)
    {
        if(string.IsNullOrWhiteSpace(tool.Version))
            return Escape(tool.Name);

        return tool.Version.Contains(tool.Name, StringComparison.OrdinalIgnoreCase)
            ? Escape(tool.Version)
            : $"{Escape(tool.Name)} {Escape(tool.Version)}";
    }

    /// <summary>
    /// <c>StoredProcedure</c> is how the enum spells it and not how anyone reads it.
    /// </summary>
    private static string Words(string pascalCase)
    {
        var builder = new System.Text.StringBuilder(pascalCase.Length + 4);

        for(var i = 0; i < pascalCase.Length; i++)
        {
            if(i > 0 && char.IsUpper(pascalCase[i]))
                builder.Append(' ');

            builder.Append(char.ToLowerInvariant(pascalCase[i]));
        }

        return builder.ToString();
    }

    private static void Warn(string title, string body)
    {
        var panel = new Panel(new Markup(Escape(body)))
            .Header($" {Escape(title)} ")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Yellow)
            .Expand();

        AnsiConsole.Write(panel);
        AnsiConsole.WriteLine();
    }

    private static void Row(Grid grid, string label, string value) =>
        grid.AddRow($"[dim]{Escape(label)}[/]", string.IsNullOrWhiteSpace(value) ? "[dim]not recorded[/]" : value);

    /// <summary>The parts of a description that are actually there, in one line.</summary>
    private static string Join(params string?[] parts)
    {
        var kept = parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => Escape(p!)).ToArray();
        return kept.Length == 0 ? string.Empty : string.Join(" [dim]/[/] ", kept);
    }

    private static string ConsistencyName(ArchiveConsistency consistency) => consistency switch
    {
        ArchiveConsistency.Snapshot => "snapshot",
        ArchiveConsistency.SnapshotIsolation => "snapshot-isolation",
        _ => "per-table"
    };

    private static string ConsistencyMeaning(ArchiveConsistency consistency) => consistency switch
    {
        ArchiveConsistency.Snapshot =>
            "read from a database snapshot, so every table is the same instant",
        ArchiveConsistency.SnapshotIsolation =>
            "read through one connection under SNAPSHOT isolation, so every table is the same instant",
        _ =>
            "each table read on its own, so two tables need not be the same instant"
    };

    private static string Plural(int count, string singular, string? plural = null) =>
        $"{count.ToString("N0", CultureInfo.InvariantCulture)} {(count == 1 ? singular : plural ?? singular + "s")}";

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
    /// Everything that came out of an archive goes through here before it is printed.
    /// A table called [Customer] or a server called [PROD] is markup to Spectre, and the
    /// sibling repository has a scar from exactly that: a command died with "Could not
    /// find color or style" because it printed a column name in brackets.
    /// </summary>
    private static string Escape(string? value) => (value ?? string.Empty).EscapeMarkup();

    /// <summary>One entry, as a container describes it. Nothing is decompressed to produce this.</summary>
    private sealed record EntryInfo(string Name, long Length, long? CompressedLength);

    /// <summary>
    /// What inspect ended up looking at, after working out which of the four things the
    /// path was.
    /// </summary>
    private sealed class Target : IDisposable
    {
        private ArchiveReader? _reader;

        private Target(
            string displayName,
            long? fileLength,
            ArchiveManifest manifest,
            string rawManifest,
            IReadOnlyList<EntryInfo> entries,
            DbDumperManifest? original,
            ArchiveReader? reader)
        {
            DisplayName = displayName;
            FileLength = fileLength;
            Manifest = manifest;
            RawManifest = rawManifest;
            Entries = entries;
            Original = original;
            _reader = reader;
        }

        public string DisplayName { get; }

        public long? FileLength { get; }

        public ArchiveManifest Manifest { get; }

        /// <summary>The manifest as the file holds it, for --json. Never re-serialized: a round trip through this build would quietly drop whatever it did not understand.</summary>
        public string RawManifest { get; }

        public IReadOnlyList<EntryInfo> Entries { get; }

        /// <summary>
        /// dbdumper's manifest as it was parsed, when that is what this is. Kept because
        /// the mapping onto <see cref="Manifest"/> necessarily stamps our format name and
        /// version on it, and the header has to say whose file this actually is.
        /// </summary>
        public DbDumperManifest? Original { get; }

        public bool IsDbDumper => Original is not null;

        public void Dispose()
        {
            _reader?.Dispose();
            _reader = null;
        }

        public static async Task<Target> OpenAsync(string path, CancellationToken cancellationToken)
        {
            if(Directory.Exists(path))
                return await OpenDirectoryAsync(path, cancellationToken).ConfigureAwait(false);

            return LooksLikeZip(path)
                ? await OpenZipAsync(path, cancellationToken).ConfigureAwait(false)
                : await OpenLooseManifestAsync(path, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// The ordinary case. ArchiveReader is asked first because it is what a restore
        /// will ask, so inspect and restore agree about what opens and what does not.
        /// </summary>
        private static async Task<Target> OpenZipAsync(string path, CancellationToken cancellationToken)
        {
            var info = new FileInfo(path);

            try
            {
                var reader = await ArchiveReader.OpenAsync(path, cancellationToken).ConfigureAwait(false);

                try
                {
                    return new Target(
                        info.Name,
                        info.Length,
                        reader.Manifest,
                        await reader.ReadTextAsync(ArchiveFormat.ManifestEntry, cancellationToken).ConfigureAwait(false),
                        reader.Entries.Select(e => new EntryInfo(e.Name, e.Length, e.CompressedLength)).ToArray(),
                        original: null,
                        reader);
                }
                catch
                {
                    reader.Dispose();
                    throw;
                }
            }
            catch(ArchiveFormatException ours)
            {
                // Not one of ours. It may still be one of dbdumper's, which is worth
                // reading: verify and restore-as-migration work on it, with the one
                // limitation this build then states out loud.
                using var zip = ZipFile.OpenRead(path);

                var entry = zip.GetEntry(ArchiveFormat.ManifestEntry);

                if(entry is null)
                    throw;

                string raw;

                var stream = entry.Open();
                await using(stream.ConfigureAwait(false))
                {
                    using var text = new StreamReader(stream, ArchiveFormat.Utf8);
                    raw = await text.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                }

                var (manifest, original) = DbDumper(raw, ours);

                return new Target(
                    info.Name,
                    info.Length,
                    manifest,
                    raw,
                    zip.Entries.Select(e => new EntryInfo(e.FullName, e.Length, e.CompressedLength)).ToArray(),
                    original,
                    reader: null);
            }
        }

        /// <summary>
        /// An archive that was unpacked, or a dumper that writes a folder rather than a
        /// zip. The listing is the files on disk; there is nothing packed about them.
        /// </summary>
        private static async Task<Target> OpenDirectoryAsync(string path, CancellationToken cancellationToken)
        {
            var manifestPath = Path.Combine(path, ArchiveFormat.ManifestEntry);

            if(!File.Exists(manifestPath))
                throw new ArchiveFormatException(
                    $"'{path}' is a directory with no {ArchiveFormat.ManifestEntry} in it, so there is nothing " +
                    "here that says what the files mean.");

            var raw = await File.ReadAllTextAsync(manifestPath, ArchiveFormat.Utf8, cancellationToken).ConfigureAwait(false);
            var (manifest, original) = Classify(raw);

            var root = Path.GetFullPath(path);

            var entries = Directory
                .EnumerateFiles(path, "*", SearchOption.AllDirectories)
                .Select(f => new EntryInfo(
                    Path.GetRelativePath(root, f).Replace(Path.DirectorySeparatorChar, '/'),
                    new FileInfo(f).Length,
                    null))
                .ToArray();

            return new Target(new DirectoryInfo(root).Name, null, manifest, raw, entries, original, reader: null);
        }

        /// <summary>A manifest handed over on its own, which is all it takes to answer most of what inspect prints.</summary>
        private static async Task<Target> OpenLooseManifestAsync(string path, CancellationToken cancellationToken)
        {
            var raw = await File.ReadAllTextAsync(path, ArchiveFormat.Utf8, cancellationToken).ConfigureAwait(false);
            var (manifest, original) = Classify(raw);
            var info = new FileInfo(path);

            return new Target(info.Name, info.Length, manifest, raw, [], original, reader: null);
        }

        private static (ArchiveManifest Manifest, DbDumperManifest? Original) Classify(string raw)
        {
            try
            {
                return (ManifestSerializer.Deserialize(raw), null);
            }
            catch(ArchiveFormatException ours)
            {
                return DbDumper(raw, ours);
            }
        }

        /// <summary>
        /// Reads it as dbdumper's, and if that fails too, reports why it is neither -
        /// ours first, because that is the format the caller was most likely expecting.
        /// </summary>
        private static (ArchiveManifest Manifest, DbDumperManifest? Original) DbDumper(string raw, ArchiveFormatException ours)
        {
            try
            {
                var original = DbDumperManifestReader.Parse(raw);
                return (DbDumperManifestReader.ToArchiveManifest(original), original);
            }
            catch(ArchiveFormatException theirs)
            {
                throw new ArchiveFormatException(
                    $"This is not a SqlArchive manifest: {ours.Message}{Environment.NewLine}" +
                    $"It is not one of dbdumper's either: {theirs.Message}",
                    ours);
            }
        }

        /// <summary>
        /// The local file header, or the end-of-directory record for an archive with
        /// nothing in it. Cheaper and steadier than opening it and catching the failure,
        /// and it is what decides whether the path is a container or a bare manifest.
        /// </summary>
        private static bool LooksLikeZip(string path)
        {
            Span<byte> head = stackalloc byte[4];

            using var file = File.OpenRead(path);

            return file.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) == head.Length &&
                   head[0] == 'P' && head[1] == 'K' &&
                   (head[2] is 0x03 or 0x05 or 0x07);
        }
    }
}
