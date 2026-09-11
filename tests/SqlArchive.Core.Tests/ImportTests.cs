using System.Data.SqlTypes;
using SqlArchive.Core.Format;
using SqlArchive.Core.Import;

namespace SqlArchive.Core.Tests;

/// <summary>
/// Everything a restore decides before it opens a connection: which half of the schema
/// runs when, whether a destination's shape will take the rows, which foreign keys have
/// to come off and what puts them back, whether a journal belongs to this run, and how a
/// row out of the archive becomes the value a bulk copy sends.
/// <para>
/// The parts that need a server are in <c>ImportLiveTests</c> and there is no overlap:
/// nothing here pretends to know what SQL Server does, and nothing there re-tests a
/// decision that can be made without it.
/// </para>
/// </summary>
public sealed class ImportTests
{
    private static readonly ArchiveColumn[] Columns =
    [
        new("Id", "int"),
        new("Name", "nvarchar"),
        new("Balance", "decimal", precision: 19, scale: 4),
        new("Big", "bigint")
    ];

    // ---------------------------------------------------------------- the phases

    /// <summary>
    /// The split at the data is what the numeric prefixes are for: everything up to
    /// <c>040_tables</c> is shape the rows need, and from <c>050_indexes</c> on is work
    /// that would otherwise be done once per row.
    /// </summary>
    [Fact]
    public async Task ThePhasesSplitWhereTheDataGoes()
    {
        var path = await ArchiveAsync(
            "schema/010_schemas.sql", "schema/020_types.sql", "schema/030_sequences.sql",
            "schema/035_synonyms.sql", "schema/040_tables.sql", "schema/050_indexes.sql",
            "schema/060_checks.sql", "schema/070_foreignkeys.sql", "schema/080_modules.sql",
            "schema/085_triggers.sql", "schema/090_finalize.sql");

        try
        {
            using var archive = await ArchiveReader.OpenAsync(path);
            var (before, after) = SchemaScript.Phases(archive);

            Assert.Equal(
                ["schema/010_schemas.sql", "schema/020_types.sql", "schema/030_sequences.sql",
                 "schema/035_synonyms.sql", "schema/040_tables.sql"],
                before.ToArray());

            Assert.Equal(
                ["schema/050_indexes.sql", "schema/060_checks.sql", "schema/070_foreignkeys.sql",
                 "schema/080_modules.sql", "schema/085_triggers.sql", "schema/090_finalize.sql"],
                after.ToArray());

            // A migration runs only this one after its data: the diff has already made
            // every other phase's work unnecessary, and cannot express this one.
            Assert.Equal(["schema/090_finalize.sql"], SchemaScript.Finalize(archive).ToArray());
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A phase this build has never heard of runs after the data. Late is the reading
    /// that can only be too cautious; early would put statements in front of the rows
    /// they are about.
    /// </summary>
    [Fact]
    public async Task APhaseWithNoNumberRunsAfterTheData()
    {
        var path = await ArchiveAsync("schema/040_tables.sql", "schema/999_new.sql", "schema/whatever.sql");

        try
        {
            using var archive = await ArchiveReader.OpenAsync(path);
            var (before, after) = SchemaScript.Phases(archive);

            Assert.Equal(["schema/040_tables.sql"], before.ToArray());
            Assert.Equal(["schema/999_new.sql", "schema/whatever.sql"], after.ToArray());
            Assert.Equal(["schema/999_new.sql", "schema/whatever.sql"], SchemaScript.Finalize(archive).ToArray());
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// <c>GO</c> is a client convention, not T-SQL, and every phase file opens with a
    /// comment block. A splitter that matched lines would cut that block in half.
    /// </summary>
    [Fact]
    public async Task APhaseIsSplitOnItsGoSeparatorsAndNotOnTheWordGo()
    {
        var path = await ArchiveAsync(("schema/040_tables.sql",
            """
            -- 040_tables.sql - tables
            SET ANSI_NULLS ON;
            GO
            CREATE TABLE [dbo].[T] ([GOAL] int NOT NULL, [Note] nvarchar(10) NULL DEFAULT (N'
            GO
            '));
            GO
            """));

        try
        {
            using var archive = await ArchiveReader.OpenAsync(path);
            var batches = await SchemaScript.ReadAsync(archive, ["schema/040_tables.sql"], CancellationToken.None);

            Assert.Equal(2, batches.Count);
            Assert.Contains("SET ANSI_NULLS ON;", batches[0].Sql, StringComparison.Ordinal);
            Assert.Contains("CREATE TABLE", batches[1].Sql, StringComparison.Ordinal);

            // The GO inside the string literal is still in the batch.
            Assert.Contains("GOAL", batches[1].Sql, StringComparison.Ordinal);
            Assert.Equal("schema/040_tables.sql, batch 2", batches[1].Describe);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---------------------------------------------------------------- the shape

    [Fact]
    public void AShapeThatMatchesIsAccepted() =>
        DestinationShape.Check(
            "[dbo].[Customer]",
            Columns,
            [Column("Id", identity: true), Column("Name"), Column("Balance"), Column("Big"), Column("Shout", computed: true)]);

    [Fact]
    public void AColumnTheDestinationDoesNotHaveIsRefusedByName()
    {
        var error = Assert.Throws<ImportShapeException>(() =>
            DestinationShape.Check("[dbo].[Customer]", Columns, [Column("Id"), Column("Name"), Column("Balance")]));

        Assert.Contains("[dbo].[Customer]", error.Message, StringComparison.Ordinal);
        Assert.Contains("'Big'", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The archive leaves out computed columns, rowversions and a ledger table's GENERATED
    /// ALWAYS columns, because SQL Server assigns all three. A destination where one of the
    /// carried columns has become one of those is a destination that is not the table that
    /// was archived.
    /// </summary>
    /// <remarks>
    /// 7 is <c>AS_TRANSACTION_ID_START</c>. Until WP 2.6 the GENERATED ALWAYS case here was
    /// a period column, and that is now the one kind a restore does write - see the next test.
    /// </remarks>
    [Theory]
    [InlineData(true, (byte)0, false, "computed")]
    [InlineData(false, (byte)7, false, "GENERATED ALWAYS")]
    [InlineData(false, (byte)0, true, "rowversion")]
    public void AColumnTheServerAssignsItselfIsRefusedByName(bool computed, byte generatedAlways, bool rowVersion, string expected)
    {
        var error = Assert.Throws<ImportShapeException>(() =>
            DestinationShape.Check(
                "[dbo].[Customer]",
                Columns,
                [Column("Id"), Column("Name", computed: computed, generatedAlways: generatedAlways, rowVersion: rowVersion),
                 Column("Balance"), Column("Big")]));

        Assert.Contains("[dbo].[Customer].[Name]", error.Message, StringComparison.Ordinal);
        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A period column refuses a plain INSERT and is carried all the same, because the
    /// restore takes the period off, writes the rows' own values and puts it back in one
    /// transaction. The shape check has to let it through, or no temporal table could be
    /// restored over one that already exists.
    /// </summary>
    [Theory]
    [InlineData(ArchiveColumns.PeriodStart)]
    [InlineData(ArchiveColumns.PeriodEnd)]
    public void APeriodColumnTheArchiveCarriesIsAccepted(byte periodColumn) =>
        DestinationShape.Check(
            "[dbo].[Precio]",
            [new ArchiveColumn("Id", "int"), new ArchiveColumn("ValidFrom", "datetime2", scale: 7)],
            [Column("Id"), Column("ValidFrom", nullable: false, generatedAlways: periodColumn)]);

    /// <summary>
    /// And one the archive does not carry is left for the server to fill, whatever its
    /// nullability - which is what it does while the period exists.
    /// </summary>
    [Fact]
    public void APeriodColumnTheArchiveDoesNotCarryIsTheServers() =>
        DestinationShape.Check(
            "[dbo].[Precio]",
            [new ArchiveColumn("Id", "int")],
            [Column("Id"), Column("ValidFrom", nullable: false, generatedAlways: ArchiveColumns.PeriodStart)]);

    /// <summary>
    /// A column the destination requires and the archive has nothing for would fail on
    /// row one with error 515, naming a staging table nobody has heard of. It is worth
    /// saying here instead.
    /// </summary>
    [Fact]
    public void AColumnTheDestinationRequiresAndTheArchiveDoesNotCarryIsRefusedByName()
    {
        var error = Assert.Throws<ImportShapeException>(() =>
            DestinationShape.Check(
                "[dbo].[Customer]",
                Columns,
                [Column("Id"), Column("Name"), Column("Balance"), Column("Big"), Column("Nueva", nullable: false)]));

        Assert.Contains("[dbo].[Customer].[Nueva]", error.Message, StringComparison.Ordinal);
        Assert.Contains("NOT NULL with no default", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void AnExtraColumnTheDestinationCanFillItselfIsFine(bool nullable, bool hasDefault) =>
        DestinationShape.Check(
            "[dbo].[Customer]",
            Columns,
            [Column("Id"), Column("Name"), Column("Balance"), Column("Big"),
             Column("Nueva", nullable: nullable, hasDefault: hasDefault)]);

    [Fact]
    public void ATableThatIsNotThereAtAllSaysSo()
    {
        var error = Assert.Throws<ImportShapeException>(() =>
            DestinationShape.Check("[dbo].[Customer]", Columns, []));

        Assert.Contains("is not in the destination", error.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- the fence

    [Theory]
    [InlineData("dbo.Order", true)]     // the table that carries the key is published
    [InlineData("dbo.Customer", true)]  // the table it points at is published
    [InlineData("dbo.Other", false)]
    public void AKeyComesOffWhenEitherOfItsTablesIsBeingReplaced(string published, bool expected) =>
        Assert.Equal(
            expected,
            ForeignKeyFence.Touches(Key(), new HashSet<string>(StringComparer.OrdinalIgnoreCase) { published }));

    /// <summary>
    /// A key that was already off stays off and is never recorded. Putting back something
    /// that was not there is the one way this could damage a destination it was meant to
    /// leave alone.
    /// </summary>
    [Fact]
    public void AKeyThatWasAlreadyOffIsLeftAlone() =>
        Assert.False(ForeignKeyFence.Touches(
            Key() with { IsDisabled = true },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "dbo.Customer" }));

    [Fact]
    public void LoweringAKeyNamesItsOwnTable() =>
        Assert.Equal("ALTER TABLE [dbo].[Order] NOCHECK CONSTRAINT [FK_Order_Customer];", Key().Lower);

    /// <summary>
    /// A key that was trusted is validated on the way back; one that was already untrusted
    /// goes back untrusted, because a restore that silently improved the destination would
    /// be a restore nobody can predict.
    /// </summary>
    [Theory]
    [InlineData(true, false, "WITH CHECK CHECK CONSTRAINT")]
    [InlineData(true, true, "WITH NOCHECK CHECK CONSTRAINT")]
    [InlineData(false, false, "WITH NOCHECK CHECK CONSTRAINT")]
    [InlineData(false, true, "WITH NOCHECK CHECK CONSTRAINT")]
    public void RaisingAKeyPutsBackTheStateItWasIn(bool revalidate, bool wasUntrusted, string expected) =>
        Assert.Equal(
            $"ALTER TABLE [dbo].[Order] {expected} [FK_Order_Customer];",
            (Key() with { IsNotTrusted = wasUntrusted }).Raise(revalidate));

    [Fact]
    public void AConstraintNameWithABracketInItIsQuoted() =>
        Assert.Contains("[a]]b]", (Key() with { Name = "a]b" }).Lower, StringComparison.Ordinal);

    // ---------------------------------------------------------------- the journal

    [Fact]
    public void AJournalRemembersWhatWasFinishedAndWhichRouteWasTaken()
    {
        var directory = TempDirectory();

        try
        {
            var journal = ImportJournal.Open(directory, Fingerprint(), resumable: false);

            Assert.False(journal.Resumed);
            Assert.Null(journal.Route);

            journal.Route = "phases";
            journal.Complete(ImportJournal.TableUnit("dbo", "Customer"), "50");

            var resumed = ImportJournal.Open(directory, Fingerprint(), resumable: true);

            Assert.True(resumed.Resumed);
            Assert.Equal("phases", resumed.Route);
            Assert.True(resumed.Done(ImportJournal.TableUnit("dbo", "Customer")));
            Assert.False(resumed.Done(ImportJournal.TableUnit("dbo", "Order")));
        }
        finally
        {
            Delete(directory);
        }
    }

    /// <summary>
    /// The marker file names go through the archive format's own escape, which is
    /// injective - so a table called <c>a.b</c> and a table <c>b</c> in schema <c>a</c>
    /// cannot mark each other done.
    /// </summary>
    [Fact]
    public void TwoTablesThatWouldCollideOnDiskDoNot()
    {
        var directory = TempDirectory();

        try
        {
            var journal = ImportJournal.Open(directory, Fingerprint(), resumable: false);

            journal.Complete(ImportJournal.TableUnit("dbo", "a.b"), string.Empty);

            Assert.True(journal.Done(ImportJournal.TableUnit("dbo", "a.b")));
            Assert.False(journal.Done(ImportJournal.TableUnit("dbo.a", "b")));
        }
        finally
        {
            Delete(directory);
        }
    }

    [Fact]
    public void AJournalLeftByADifferentRestoreIsRefusedRatherThanMixed()
    {
        var directory = TempDirectory();

        try
        {
            ImportJournal.Open(directory, Fingerprint(), resumable: false);

            var error = Assert.Throws<ImportResumeException>(() =>
                ImportJournal.Open(directory, Fingerprint(database: "Otra"), resumable: true));

            Assert.Contains("[Otra]", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Delete(directory);
        }
    }

    /// <summary>
    /// The working directory is something a caller names, and opening it without --resume
    /// deletes it. One with things in it that no restore put there is refused rather than
    /// emptied.
    /// </summary>
    [Fact]
    public void ADirectoryThatIsNotAJournalIsRefusedRatherThanDeleted()
    {
        var directory = TempDirectory();

        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "las-fotos-de-la-boda.txt"), "no");

            var error = Assert.Throws<ImportException>(() => ImportJournal.Open(directory, Fingerprint(), resumable: false));

            Assert.Contains("not empty", error.Message, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(directory, "las-fotos-de-la-boda.txt")));
        }
        finally
        {
            Delete(directory);
        }
    }

    // ---------------------------------------------------------------- the fingerprint

    [Fact]
    public void AFingerprintOfTheSameRestoreMatchesItself() =>
        Assert.Null(Fingerprint().Difference(Fingerprint()));

    /// <summary>
    /// The archive is fingerprinted by its content and not by its path: two archives of
    /// the same database an hour apart have the same file name as often as not, and
    /// resuming one restore into the other is exactly the mixture this refuses.
    /// </summary>
    [Fact]
    public void AnArchiveWithADifferentRowHashIsADifferentArchive()
    {
        var difference = Fingerprint().Difference(Fingerprint(rowHash: new string('b', 64)));

        Assert.NotNull(difference);
        Assert.Contains("different archive", difference, StringComparison.Ordinal);
    }

    [Fact]
    public void ARestoreOfADifferentPartOfTheArchiveIsNotAResume()
    {
        var difference = Fingerprint().Difference(Fingerprint(mode: ImportMode.DataOnly));

        Assert.NotNull(difference);
        Assert.Contains("mode or the table filters", difference, StringComparison.Ordinal);
    }

    [Fact]
    public void ADifferentBuildIsNotAllowedToFinishAnotherOnesRestore()
    {
        var difference = Fingerprint().Difference(Fingerprint(tool: "9.9.9"));

        Assert.NotNull(difference);
        Assert.Contains("9.9.9", difference, StringComparison.Ordinal);
    }

    /// <summary>
    /// A rotated password does not throw away a journal that is still about the same
    /// database; pointing at another server does.
    /// </summary>
    [Fact]
    public void OnlyTheIdentityOfTheConnectionCounts()
    {
        var one = ImportFingerprint.Of(Manifest(), Options("Server=a;Database=V;User ID=u;Password=old"), "1.0.0");
        var two = ImportFingerprint.Of(Manifest(), Options("Server=a;Database=V;User ID=u;Password=new"), "1.0.0");
        var far = ImportFingerprint.Of(Manifest(), Options("Server=b;Database=V;User ID=u;Password=old"), "1.0.0");

        Assert.Null(one.Difference(two));
        Assert.NotNull(one.Difference(far));

        // And the connection string itself is not in the fingerprint, which sits on disk.
        Assert.DoesNotContain("old", System.Text.Json.JsonSerializer.Serialize(one), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- the reader

    /// <summary>
    /// The decoder reads every integer as a <c>long</c>, because that is what the format
    /// says. A bulk copy is entitled to refuse a <c>long</c> at an <c>int</c> column, so
    /// the narrowing happens here, where the destination's type is known.
    /// </summary>
    [Fact]
    public async Task RowsComeOutAsTheClrTypesTheDestinationColumnsTake()
    {
        var path = await DataArchiveAsync();

        try
        {
            using var archive = await ArchiveReader.OpenAsync(path);
            var table = archive.Manifest.Tables[0];

            var rows = archive.OpenTable(table, Columns);

            await using(rows)
            {
                using var reader = new ArchiveDataReader(rows, Columns);

                Assert.Equal(4, reader.FieldCount);
                Assert.Equal(typeof(int), reader.GetFieldType(0));
                Assert.Equal(typeof(string), reader.GetFieldType(1));
                Assert.Equal(typeof(SqlDecimal), reader.GetFieldType(2));
                Assert.Equal(typeof(long), reader.GetFieldType(3));

                Assert.Equal(2, reader.GetOrdinal("Balance"));
                Assert.Equal(2, reader.GetOrdinal("BALANCE"));
                Assert.Throws<IndexOutOfRangeException>(() => reader.GetOrdinal("Nope"));

                Assert.True(await reader.ReadAsync(CancellationToken.None));

                Assert.Equal(1, reader.GetValue(0));
                Assert.IsType<int>(reader.GetValue(0));
                Assert.Equal("Acme", reader.GetValue(1));
                Assert.Equal(new SqlDecimal(1250.0000m), reader.GetValue(2));
                Assert.Equal(9007199254740993L, reader.GetValue(3));

                Assert.True(await reader.ReadAsync(CancellationToken.None));

                // A null is DBNull on the way out, which is what a bulk copy expects, and
                // never the column's default.
                Assert.True(reader.IsDBNull(1));
                Assert.Equal(DBNull.Value, reader.GetValue(1));

                Assert.False(await reader.ReadAsync(CancellationToken.None));

                // And the reader has been counting and hashing all along, which is the
                // first half of the guard.
                Assert.Equal(2, reader.Rows);
                Assert.Equal(table.RowHash, reader.RowHash);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A table cannot have two columns of one name, so a manifest that says it does is
    /// one that disagrees with itself - and taking the first would load one of the two
    /// into both.
    /// </summary>
    [Fact]
    public async Task AColumnListWithTheSameNameTwiceIsRefused()
    {
        var path = await DataArchiveAsync();

        try
        {
            using var archive = await ArchiveReader.OpenAsync(path);
            var rows = archive.OpenTable(archive.Manifest.Tables[0], Columns);

            await using(rows)
            {
                var error = Assert.Throws<ImportException>(() =>
                    new ArchiveDataReader(rows, [Columns[0], Columns[1], Columns[1], Columns[3]]));

                Assert.Contains("'Name' twice", error.Message, StringComparison.Ordinal);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---------------------------------------------------------------- the verdict

    [Fact]
    public void ARefusedTableMakesTheRestoreIncompleteAndASkippedOneDoesNot()
    {
        Assert.False(Result(ImportTableOutcome.Published, ImportTableOutcome.Refused).Complete);
        Assert.False(Result(ImportTableOutcome.Published, ImportTableOutcome.Failed).Complete);
        Assert.True(Result(ImportTableOutcome.Published, ImportTableOutcome.Skipped).Complete);
        Assert.True(Result(ImportTableOutcome.WouldPublish).Complete);

        var mixed = Result(
            ImportTableOutcome.Published, ImportTableOutcome.Published,
            ImportTableOutcome.Refused, ImportTableOutcome.Skipped);

        Assert.Equal(2, mixed.Published);
        Assert.Equal(1, mixed.Refused);
        Assert.Equal(1, mixed.Skipped);
    }

    [Fact]
    public void ARestoreWithNowhereToRestoreToIsRefusedAtTheConstructor() =>
        Assert.Throws<ArgumentException>(() => new DatabaseImporter(new ImportOptions { ConnectionString = string.Empty }));

    // ---------------------------------------------------------------- helpers

    private static DestinationColumn Column(
        string name,
        bool identity = false,
        bool computed = false,
        byte generatedAlways = 0,
        bool rowVersion = false,
        bool nullable = true,
        bool hasDefault = false) =>
        new(name, identity, computed, generatedAlways, rowVersion, nullable, hasDefault);

    private static DestinationForeignKey Key() =>
        new("FK_Order_Customer", "dbo", "Order", "dbo", "Customer", IsDisabled: false, IsNotTrusted: false);

    private static ImportOptions Options(string connectionString, ImportMode mode = ImportMode.Migrate) =>
        new() { ConnectionString = connectionString, Mode = mode };

    private static ArchiveManifest Manifest(string? rowHash = null) => new()
    {
        Source = new ArchiveSource { Server = "SQL2022", Database = "Ventas" },
        CreatedAt = new DateTimeOffset(2026, 9, 7, 22, 30, 0, TimeSpan.Zero),
        Tables =
        [
            new ArchiveTableEntry
            {
                Schema = "dbo",
                Name = "Customer",
                RowCount = 41203,
                RowHash = rowHash ?? new string('a', 64)
            }
        ]
    };

    private static ImportFingerprint Fingerprint(
        string database = "VentasCopia",
        string? rowHash = null,
        ImportMode mode = ImportMode.Migrate,
        string tool = "1.0.0") =>
        ImportFingerprint.Of(
            Manifest(rowHash),
            Options($"Server=SQL2022;Database={database};Integrated Security=true", mode),
            tool);

    private static ImportResult Result(params ImportTableOutcome[] outcomes) =>
        new(
            "x.sqlarchive",
            ImportMode.Migrate,
            DryRun: outcomes.Contains(ImportTableOutcome.WouldPublish),
            Rows: 0,
            SchemaBatches: 0,
            Elapsed: TimeSpan.Zero,
            Tables: outcomes.Select((o, i) => new ImportTableResult("dbo", $"T{i}", o, 0)).ToList(),
            Notices: [],
            Manifest: Manifest());

    /// <summary>An archive holding nothing but the named schema entries.</summary>
    private static Task<string> ArchiveAsync(params string[] entries) =>
        ArchiveAsync(entries.Select(e => (e, "SELECT 1;")).ToArray());

    private static async Task<string> ArchiveAsync(params (string Name, string Text)[] entries)
    {
        var path = TempPath();
        var writer = ArchiveWriter.Create(path);

        await using(writer)
        {
            foreach(var (name, text) in entries)
                await writer.AddTextAsync(name, text);

            await writer.WriteManifestAsync(new ArchiveManifest());
        }

        return path;
    }

    /// <summary>An archive with one table of two rows, the second of which has a null in it.</summary>
    private static async Task<string> DataArchiveAsync()
    {
        var path = TempPath();
        var entry = ArchiveFormat.DataEntry("dbo", "Customer");
        var writer = ArchiveWriter.Create(path);
        string hash;

        await using(writer)
        {
            var stream = writer.CreateEntry(entry);

            await using(stream)
            {
                var rows = new JsonlRowWriter(stream, Columns);

                await using(rows)
                {
                    await rows.WriteAsync(new object?[] { 1, "Acme", new SqlDecimal(1250.0000m), 9007199254740993L });
                    await rows.WriteAsync(new object?[] { 2, null, new SqlDecimal(-3.5000m), 0L });
                }

                hash = rows.Hash.Value;
            }

            var manifest = new ArchiveManifest
            {
                Tables =
                [
                    new ArchiveTableEntry
                    {
                        Schema = "dbo",
                        Name = "Customer",
                        RowCount = 2,
                        RowHash = hash,
                        DataFiles = [entry]
                    }
                ]
            };

            await writer.WriteManifestAsync(manifest);
        }

        return path;
    }

    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"sqlarchive-import-{Guid.NewGuid():N}.sqlarchive");

    private static string TempDirectory() =>
        Path.Combine(Path.GetTempPath(), $"sqlarchive-journal-{Guid.NewGuid():N}");

    private static void Delete(string directory)
    {
        if(Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}
