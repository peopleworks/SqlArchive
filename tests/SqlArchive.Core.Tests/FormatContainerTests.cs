using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using SqlArchive.Core.Format;
using SqlSchemaDiff.Models;

namespace SqlArchive.Core.Tests;

/// <summary>
/// The zip: writing it, listing it without unpacking it, reading one entry at a time, and
/// the entry names holding up against identifiers that were never meant to be file names.
/// </summary>
public sealed class FormatContainerTests
{
    private static readonly ArchiveColumn[] Columns = [new("Id", "int"), new("Name", "nvarchar")];

    [Theory]
    [InlineData("dbo", "Customer", null, "data/dbo.Customer.jsonl")]
    [InlineData("dbo", "Order", 0, "data/dbo.Order.0000.jsonl")]
    [InlineData("dbo", "Order", 42, "data/dbo.Order.0042.jsonl")]
    [InlineData("Sales 2026", "Order", null, "data/Sales 2026.Order.jsonl")]
    public void DataEntryNamesFollowTheDesign(string schema, string table, int? part, string expected) =>
        Assert.Equal(expected, ArchiveFormat.DataEntry(schema, table, part));

    /// <summary>
    /// The dot is the separator, so it has to be escaped inside an identifier or
    /// [dbo].[My.Table] and [dbo.My].[Table] would produce the same entry.
    /// </summary>
    [Fact]
    public void TwoIdentifiersThatWouldCollideDoNot() =>
        Assert.NotEqual(ArchiveFormat.DataEntry("dbo", "My.Table"), ArchiveFormat.DataEntry("dbo.My", "Table"));

    [Theory]
    [InlineData("My.Table", "My%2ETable")]
    [InlineData("a/b", "a%2Fb")]
    [InlineData("a\\b", "a%5Cb")]
    [InlineData("a:b", "a%3Ab")]
    [InlineData("a*?\"<>|b", "a%2A%3F%22%3C%3E%7Cb")]
    [InlineData("100%", "100%25")]
    [InlineData("a\nb", "a%0Ab")]
    [InlineData("trailing ", "trailing%20")]
    [InlineData("Pedido", "Pedido")]
    [InlineData("Ventas 2026", "Ventas 2026")]
    public void IdentifiersAreEscapedOnlyWhereTheyHaveToBe(string identifier, string expected) =>
        Assert.Equal(expected, ArchiveFormat.EscapeIdentifier(identifier));

    [Fact]
    public void NonAsciiIdentifiersAreLeftAlone() =>
        Assert.Equal("Año", ArchiveFormat.EscapeIdentifier("Año"));

    [Fact]
    public async Task AnArchiveRoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sqlarchive-{Guid.NewGuid():N}.sqlarchive");

        try
        {
            var manifest = await BuildAsync(path, rows: 3);

            using var reader = await ArchiveReader.OpenAsync(path);

            Assert.Equal("Ventas", reader.Manifest.Source.Database);
            Assert.Equal(3, reader.Manifest.Tables[0].RowCount);
            Assert.Equal(manifest.Tables[0].RowHash, reader.Manifest.Tables[0].RowHash);

            Assert.Equal(
                ["schema/010_schemas.sql", "schema/040_tables.sql"],
                reader.SchemaEntries.ToArray());

            Assert.Contains("CREATE TABLE", await reader.ReadTextAsync("schema/040_tables.sql"), StringComparison.Ordinal);
            Assert.Contains("SqlArchive", await reader.ReadTextAsync(ArchiveFormat.ReadmeEntry), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// What inspect needs. Listing the entries and reading the manifest touch the zip's
    /// directory and the manifest entry, and nothing else - which is what makes inspect
    /// instant on an archive of a hundred gigabytes.
    /// </summary>
    [Fact]
    public async Task TheManifestAndTheListingCostNothingToRead()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sqlarchive-{Guid.NewGuid():N}.sqlarchive");

        try
        {
            await BuildAsync(path, rows: 20000);

            using var reader = await ArchiveReader.OpenAsync(path);
            var data = reader.Entries.Single(e => e.Name.StartsWith("data/", StringComparison.Ordinal));

            Assert.Equal(20000, reader.Manifest.Tables[0].RowCount);

            // The sizes come out of the directory, so a large entry can be described
            // without being decompressed.
            Assert.True(data.Length > 500_000, $"the data entry is only {data.Length} bytes unpacked");
            Assert.True(data.CompressedLength < data.Length);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task EveryEntryIsHashedAsItIsWritten()
    {
        var stream = new MemoryStream();
        var writer = ArchiveWriter.Create(stream);

        await using(writer)
        {
            await writer.AddTextAsync("schema/010_schemas.sql", "CREATE SCHEMA sales;");
            await writer.WriteManifestAsync(new ArchiveManifest());
        }

        var expected = ArchiveFormat.HashPrefix +
                       Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("CREATE SCHEMA sales;")));

        Assert.Equal(expected, writer.EntryHashes["schema/010_schemas.sql"]);
    }

    [Fact]
    public async Task WhatTheManifestDeclaresIsWhatTheEntriesHashTo()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sqlarchive-{Guid.NewGuid():N}.sqlarchive");

        try
        {
            await BuildAsync(path, rows: 5);

            using var reader = await ArchiveReader.OpenAsync(path);

            foreach(var entry in reader.Manifest.DeclaredEntries())
                Assert.Equal(reader.Manifest.HashOf(entry), await reader.ComputeEntryHashAsync(entry));

            // And between the two halves of the manifest, everything but the manifest is
            // covered exactly once.
            Assert.Equal(
                reader.Entries.Select(e => e.Name).Where(n => n != ArchiveFormat.ManifestEntry).Order(StringComparer.Ordinal),
                reader.Manifest.DeclaredEntries().Order(StringComparer.Ordinal));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The guard a restore runs: the rows read have to be the rows the manifest promised,
    /// counted and hashed, before the destination is touched.
    /// </summary>
    [Fact]
    public async Task ATableReadsBackAgainstItsDeclaredCountAndHash()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sqlarchive-{Guid.NewGuid():N}.sqlarchive");

        try
        {
            await BuildAsync(path, rows: 100, ranges: 4);

            using var reader = await ArchiveReader.OpenAsync(path);
            var table = reader.Manifest.Tables[0];

            Assert.Equal(4, table.DataFiles.Count);

            var rows = reader.OpenTable(table, Columns);

            await using(rows)
            {
                while(await rows.ReadAsync()) { }

                Assert.Equal(100, rows.Rows);
                Assert.True(rows.Matches());
                Assert.Null(rows.Difference());
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// And the same guard, told a lie. This is the shape of the message a restore refuses
    /// with, so it is asserted rather than assumed.
    /// </summary>
    [Fact]
    public async Task ATableThatDoesNotMatchSaysWhichWay()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sqlarchive-{Guid.NewGuid():N}.sqlarchive");

        try
        {
            await BuildAsync(path, rows: 10);

            using var reader = await ArchiveReader.OpenAsync(path);
            var table = reader.Manifest.Tables[0];
            table.RowHash = new string('0', 64);

            var rows = reader.OpenTable(table, Columns);

            await using(rows)
            {
                while(await rows.ReadAsync()) { }

                Assert.False(rows.Matches());
                Assert.Contains("10 rows on both sides, and the content differs", rows.Difference()!, StringComparison.Ordinal);
            }

            table.RowCount = 11;
            var again = reader.OpenTable(table, Columns);

            await using(again)
            {
                while(await again.ReadAsync()) { }

                Assert.Contains("declares 11 rows and the data holds 10", again.Difference()!, StringComparison.Ordinal);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task AFileWithNoManifestIsNotAnArchiveAndSaysSo()
    {
        var stream = new MemoryStream();

        using(var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("hello.txt");
            using var writer = new StreamWriter(entry.Open());
            await writer.WriteAsync("hello");
        }

        stream.Position = 0;

        var error = await Assert.ThrowsAsync<ArchiveFormatException>(() => ArchiveReader.OpenAsync(stream));

        Assert.Contains(ArchiveFormat.ManifestEntry, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEntryWrittenTwiceIsRefused()
    {
        var writer = ArchiveWriter.Create(new MemoryStream());

        await using(writer)
        {
            await writer.AddTextAsync("README.txt", "one");

            await Assert.ThrowsAsync<ArchiveFormatException>(() => writer.AddTextAsync("README.txt", "two"));
        }
    }

    [Fact]
    public async Task AskingForAnEntryThatIsNotThereSaysWhichOne()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sqlarchive-{Guid.NewGuid():N}.sqlarchive");

        try
        {
            await BuildAsync(path, rows: 1);

            using var reader = await ArchiveReader.OpenAsync(path);
            var error = Assert.Throws<ArchiveFormatException>(() => reader.OpenEntry("data/dbo.Nope.jsonl"));

            Assert.Contains("data/dbo.Nope.jsonl", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Writes an archive of one table, optionally split across ranges, and returns the
    /// manifest that went into it.
    /// </summary>
    private static async Task<ArchiveManifest> BuildAsync(string path, int rows, int ranges = 1)
    {
        var hash = new RowHash.Accumulator();
        var table = new ArchiveTableEntry { Schema = "dbo", Name = "Customer", RowCount = rows };

        var writer = ArchiveWriter.Create(path);

        await using(writer)
        {
            await writer.AddTextAsync("schema/010_schemas.sql", "-- no schemas\n");
            await writer.AddTextAsync("schema/040_tables.sql", "CREATE TABLE dbo.Customer (Id int, Name nvarchar(100));\n");

            var perRange = (int)Math.Ceiling(rows / (double)ranges);

            for(var range = 0; range < ranges; range++)
            {
                var name = ranges == 1
                    ? ArchiveFormat.DataEntry("dbo", "Customer")
                    : ArchiveFormat.DataEntry("dbo", "Customer", range);

                var stream = writer.CreateEntry(name);

                await using(stream)
                {
                    var rowWriter = new JsonlRowWriter(stream, Columns, hash);

                    await using(rowWriter)
                    {
                        for(var i = range * perRange; i < Math.Min(rows, (range + 1) * perRange); i++)
                            await rowWriter.WriteAsync(new object?[] { (long)i, $"customer {i}" });
                    }
                }

                table.DataFiles.Add(name);
            }

            table.RowHash = hash.Value;

            var manifest = new ArchiveManifest
            {
                Tool = new ArchiveTool { Version = "0.1.0" },
                CreatedAt = DateTimeOffset.UtcNow,
                Source = new ArchiveSource { Server = "localhost", Database = "Ventas" },
                Schema = new DatabaseSnapshot { DatabaseName = "Ventas" },
                Tables = [table]
            };

            await writer.AddTextAsync(ArchiveFormat.ReadmeEntry, ArchiveReadme.Compose(manifest));

            foreach(var entry in writer.EntryHashes)
            {
                if(table.DataFiles.Contains(entry.Key))
                    table.FileHashes[entry.Key] = entry.Value;
                else
                    manifest.Files[entry.Key] = entry.Value;
            }

            await writer.WriteManifestAsync(manifest);
            return manifest;
        }
    }
}
