using System.IO.Compression;
using System.Security.Cryptography;

namespace SqlArchive.Core.Format;

/// <summary>
/// Opens an archive and reads it without unpacking it.
/// <para>
/// A zip keeps its directory at the end, so listing the entries and reading the manifest
/// cost one seek and a few kilobytes however large the archive is - which is what
/// <c>inspect</c> needs, and what a restore needs before it decides to touch anything.
/// Data entries are opened as streams and decompressed as they are read; a table larger
/// than memory is read the same way as a small one.
/// </para>
/// </summary>
public sealed class ArchiveReader : IDisposable
{
    private readonly ZipArchive _zip;
    private readonly Stream? _file;

    private bool _disposed;

    private ArchiveReader(ZipArchive zip, Stream? file, ArchiveManifest manifest)
    {
        _zip = zip;
        _file = file;
        Manifest = manifest;
        Entries = zip.Entries
            .Select(e => new ArchiveEntryInfo(e.FullName, e.Length, e.CompressedLength, e.LastWriteTime))
            .ToArray();
    }

    /// <summary>The manifest, read when the archive was opened.</summary>
    public ArchiveManifest Manifest { get; }

    /// <summary>Every entry, with its sizes. Reading this decompresses nothing.</summary>
    public IReadOnlyList<ArchiveEntryInfo> Entries { get; }

    /// <summary>
    /// The schema phases, in the order they have to run. The numeric prefixes are what
    /// put them in that order, which is also why an ordinal sort is the right one and a
    /// culture-aware sort would be a bug waiting for a Turkish locale.
    /// </summary>
    public IReadOnlyList<string> SchemaEntries =>
        Entries
            .Select(e => e.Name)
            .Where(n => n.StartsWith(ArchiveFormat.SchemaDirectory, StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

    public static async Task<ArchiveReader> OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);

        try
        {
            return await OpenCoreAsync(file, leaveOpen: true, file, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await file.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public static Task<ArchiveReader> OpenAsync(Stream source, bool leaveOpen = true, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        return OpenCoreAsync(source, leaveOpen, owned: null, cancellationToken);
    }

    /// <summary>Opens one entry for streaming. The caller disposes it.</summary>
    public Stream OpenEntry(string entryName)
    {
        ArgumentException.ThrowIfNullOrEmpty(entryName);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var entry = _zip.GetEntry(entryName)
            ?? throw new ArchiveFormatException($"The archive has no entry named '{entryName}'.");

        return entry.Open();
    }

    /// <summary>Reads a whole entry as text. For a schema phase or the README, not for data.</summary>
    public async Task<string> ReadTextAsync(string entryName, CancellationToken cancellationToken = default)
    {
        var stream = OpenEntry(entryName);
        await using(stream.ConfigureAwait(false))
        {
            using var reader = new StreamReader(stream, ArchiveFormat.Utf8);
            return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reads a table's rows across however many entries hold them, as one sequence, with
    /// one running hash.
    /// </summary>
    /// <remarks>
    /// That the ranges add up whatever order they are read in is the property the hash
    /// exists for; this is where it is used.
    /// </remarks>
    public ArchiveTableReader OpenTable(ArchiveTableEntry table, IReadOnlyList<ArchiveColumn> columns)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(columns);

        return new ArchiveTableReader(this, table, columns);
    }

    /// <summary>
    /// The SHA-256 of an entry as it actually is, prefixed <c>sha256:</c>, for comparing
    /// against what the manifest says it should be.
    /// </summary>
    public async Task<string> ComputeEntryHashAsync(string entryName, CancellationToken cancellationToken = default)
    {
        var stream = OpenEntry(entryName);
        await using(stream.ConfigureAwait(false))
        {
            var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            return ArchiveFormat.HashPrefix + Convert.ToHexStringLower(hash);
        }
    }

    public void Dispose()
    {
        if(_disposed)
            return;

        _disposed = true;
        _zip.Dispose();
        _file?.Dispose();
    }

    private static async Task<ArchiveReader> OpenCoreAsync(
        Stream source,
        bool leaveOpen,
        Stream? owned,
        CancellationToken cancellationToken)
    {
        var zip = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen);

        try
        {
            var entry = zip.GetEntry(ArchiveFormat.ManifestEntry)
                ?? throw new ArchiveFormatException(
                    $"The file has no '{ArchiveFormat.ManifestEntry}', so it is not a SqlArchive archive. " +
                    "Every entry in it may still be a perfectly good zip; there is just nothing here that says " +
                    "what the rows mean.");

            var stream = entry.Open();
            await using(stream.ConfigureAwait(false))
            {
                var manifest = await ManifestSerializer.ReadAsync(stream, cancellationToken).ConfigureAwait(false);
                return new ArchiveReader(zip, owned, manifest);
            }
        }
        catch
        {
            zip.Dispose();
            throw;
        }
    }
}

/// <summary>One entry, as the zip's directory describes it. Nothing is decompressed to produce this.</summary>
/// <param name="Name">The entry's full name, with forward slashes.</param>
/// <param name="Length">Its size when unpacked.</param>
/// <param name="CompressedLength">Its size in the archive.</param>
/// <param name="LastWriteTime">What the writer stamped on it.</param>
public sealed record ArchiveEntryInfo(string Name, long Length, long CompressedLength, DateTimeOffset LastWriteTime);
