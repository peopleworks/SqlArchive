using System.Security.Cryptography;
using SqlArchive.Core.Format;

namespace SqlArchive.Core.Export;

/// <summary>
/// Passes everything written through to another stream and hashes it on the way.
/// <para>
/// The spool file's SHA-256 is recorded beside it so that packing can check the bytes it
/// is putting into the archive are the bytes that came out of the database. Without it, a
/// spool damaged between the read and the pack - a resumed export days later, a disk that
/// lied - would be sealed into an archive together with a manifest that agrees with it,
/// and the archive would verify.
/// </para>
/// <para>
/// <c>ArchiveWriter</c> does the same thing for zip entries and does not expose it, and
/// there is nothing to share: forty lines, and a hashing stream that both a zip and a
/// file could use would have to be a public type of the format package, which is WP 2.1's
/// to decide.
/// </para>
/// </summary>
public sealed class HashingWriteStream : Stream
{
    private readonly Stream _inner;
    private readonly bool _leaveOpen;
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

    private long _bytes;
    private string? _final;

    public HashingWriteStream(Stream inner, bool leaveOpen = true)
    {
        ArgumentNullException.ThrowIfNull(inner);

        _inner = inner;
        _leaveOpen = leaveOpen;
    }

    /// <summary>How many bytes went through.</summary>
    public long Bytes => _bytes;

    /// <summary>
    /// The SHA-256 of everything written, prefixed <c>sha256:</c> to match the manifest's
    /// file hashes. Finalised on first read, so it is the same answer however often it is
    /// asked for.
    /// </summary>
    public string Hash => _final ??= ArchiveFormat.HashPrefix + Convert.ToHexStringLower(_hash.GetHashAndReset());

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => _bytes;

    public override long Position
    {
        get => _bytes;
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        Append(buffer);
        _inner.Write(buffer);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Append(buffer.Span);
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
        if(disposing)
        {
            // Finalised before the hash object goes, so a caller reading Hash after the
            // stream is disposed - which is the normal way round - gets an answer.
            _ = Hash;
            _hash.Dispose();

            if(!_leaveOpen)
                _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    private void Append(ReadOnlySpan<byte> buffer)
    {
        if(_final is not null)
            throw new InvalidOperationException("The hash has already been read, so nothing more can be written.");

        _hash.AppendData(buffer);
        _bytes += buffer.Length;
    }
}
