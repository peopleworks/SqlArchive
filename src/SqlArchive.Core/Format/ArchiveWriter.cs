using System.IO.Compression;
using System.Security.Cryptography;

namespace SqlArchive.Core.Format;

/// <summary>
/// Builds the zip: the schema phases, the JSONL entries, the README, and last of all the
/// manifest.
/// <para>
/// Every entry is hashed as it is written, by a stream that sits between the caller and
/// the compressor. Nothing is read back to hash it, and nothing is held in memory to
/// hash it, which matters when a single table is larger than the machine.
/// </para>
/// <para>
/// The manifest goes in last because it carries the hashes of everything else, and that
/// costs nothing: a zip's directory is at its end and a reader finds any entry through
/// it, so <c>inspect</c> still reads the manifest without touching the data.
/// </para>
/// </summary>
public sealed class ArchiveWriter : IAsyncDisposable
{
    private readonly ZipArchive _zip;
    private readonly Stream? _file;
    private readonly Dictionary<string, string> _hashes = new(StringComparer.Ordinal);

    private bool _manifestWritten;
    private bool _disposed;

    private ArchiveWriter(ZipArchive zip, Stream? file, CompressionLevel level)
    {
        _zip = zip;
        _file = file;
        Level = level;
    }

    public CompressionLevel Level { get; }

    /// <summary>
    /// The SHA-256 of every entry written so far, keyed by entry name and prefixed
    /// <c>sha256:</c>. This is what fills the manifest's <see cref="ArchiveManifest.Files"/>
    /// and each table's <see cref="ArchiveTableEntry.FileHashes"/>.
    /// </summary>
    public IReadOnlyDictionary<string, string> EntryHashes => _hashes;

    /// <param name="path">The archive to create. Overwritten if it exists.</param>
    /// <param name="level">
    /// <see cref="CompressionLevel.Optimal"/> by default. <c>SmallestSize</c> is
    /// available and is many times slower on JSONL for a few percent, which is a bad
    /// trade on a table of a hundred million rows.
    /// </param>
    public static ArchiveWriter Create(string path, CompressionLevel level = CompressionLevel.Optimal)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);

        try
        {
            return new ArchiveWriter(new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true), file, level);
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    /// <summary>Writes into a stream the caller owns. Used by the tests and by anything that spools.</summary>
    public static ArchiveWriter Create(Stream destination, bool leaveOpen = true, CompressionLevel level = CompressionLevel.Optimal)
    {
        ArgumentNullException.ThrowIfNull(destination);
        return new ArchiveWriter(new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen), file: null, level);
    }

    /// <summary>
    /// Opens an entry for writing. Dispose the returned stream before opening another -
    /// a zip in create mode holds one entry open at a time - and the entry's hash appears
    /// in <see cref="EntryHashes"/> when it closes.
    /// </summary>
    public Stream CreateEntry(string entryName)
    {
        ArgumentException.ThrowIfNullOrEmpty(entryName);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if(_hashes.ContainsKey(entryName))
            throw new ArchiveFormatException($"The archive already has an entry named '{entryName}'.");

        var entry = _zip.CreateEntry(entryName, Level);
        return new HashingStream(entry.Open(), entryName, this);
    }

    /// <summary>Writes a whole entry from text. For the schema phases and the README.</summary>
    public async Task AddTextAsync(string entryName, string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);

        var stream = CreateEntry(entryName);
        await using(stream.ConfigureAwait(false))
        {
            var bytes = ArchiveFormat.Utf8.GetBytes(text);
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Writes the manifest and closes the archive to further entries. Must be last: it
    /// carries the hashes of everything else.
    /// </summary>
    public async Task WriteManifestAsync(ArchiveManifest manifest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if(_manifestWritten)
            throw new ArchiveFormatException("The manifest has already been written.");

        var entry = _zip.CreateEntry(ArchiveFormat.ManifestEntry, Level);

        var stream = entry.Open();
        await using(stream.ConfigureAwait(false))
            await ManifestSerializer.WriteAsync(manifest, stream, cancellationToken).ConfigureAwait(false);

        _manifestWritten = true;
    }

    public async ValueTask DisposeAsync()
    {
        if(_disposed)
            return;

        _disposed = true;

        _zip.Dispose();

        if(_file is not null)
            await _file.DisposeAsync().ConfigureAwait(false);
    }

    private void Record(string entryName, byte[] hash) =>
        _hashes[entryName] = ArchiveFormat.HashPrefix + Convert.ToHexStringLower(hash);

    /// <summary>
    /// Feeds everything written through a SHA-256 on the way to the compressor, so the
    /// manifest's file hashes cost one pass over bytes that are in cache anyway.
    /// </summary>
    private sealed class HashingStream : Stream
    {
        private readonly Stream _inner;
        private readonly string _entryName;
        private readonly ArchiveWriter _owner;
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        private bool _closed;

        public HashingStream(Stream inner, string entryName, ArchiveWriter owner)
        {
            _inner = inner;
            _entryName = entryName;
            _owner = owner;
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count) =>
            Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _hash.AppendData(buffer);
            _inner.Write(buffer);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _hash.AppendData(buffer.Span);
            await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush() => _inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if(disposing && !_closed)
            {
                _closed = true;
                _inner.Dispose();
                _owner.Record(_entryName, _hash.GetHashAndReset());
                _hash.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if(!_closed)
            {
                _closed = true;
                await _inner.DisposeAsync().ConfigureAwait(false);
                _owner.Record(_entryName, _hash.GetHashAndReset());
                _hash.Dispose();
            }

            await base.DisposeAsync().ConfigureAwait(false);
        }
    }
}
