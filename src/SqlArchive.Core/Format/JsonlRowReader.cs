namespace SqlArchive.Core.Format;

/// <summary>
/// Reads a JSONL entry back, one row at a time, hashing the lines exactly as they are on
/// disk.
/// <para>
/// The hash is taken over the bytes read, not over a re-encoding of the decoded values.
/// That is what makes it answer the question a restore actually asks - "is this file the
/// one the manifest describes?" - rather than the weaker "does this file decode to
/// something that would encode back to what the manifest describes". A truncated file, a
/// corrupt block or a half-written range is caught before a row reaches the destination.
/// </para>
/// <para>
/// Nothing is held in memory but one line. A table can be bigger than the machine, which
/// is the reason SyncJob.Core exists and would be a strange thing for this package to
/// forget.
/// </para>
/// </summary>
public sealed class JsonlRowReader : IAsyncDisposable
{
    private const int InitialBuffer = 64 * 1024;

    private readonly Stream _source;
    private readonly IReadOnlyList<ArchiveColumn> _columns;
    private readonly bool _leaveOpen;
    private readonly object?[] _values;

    private byte[] _buffer = new byte[InitialBuffer];
    private int _start;
    private int _end;
    private int _lineStart;
    private int _lineLength;
    private bool _exhausted;
    private bool _disposed;

    /// <param name="source">The entry to read.</param>
    /// <param name="columns">The archived columns, in order.</param>
    /// <param name="hash">
    /// An accumulator to feed, so a table spread over several range entries adds up in
    /// one place rather than being folded together afterwards. Null for a fresh one.
    /// </param>
    /// <param name="leaveOpen">Whether disposing this disposes <paramref name="source"/>.</param>
    public JsonlRowReader(
        Stream source,
        IReadOnlyList<ArchiveColumn> columns,
        RowHash.Accumulator? hash = null,
        bool leaveOpen = true)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(columns);

        _source = source;
        _columns = columns;
        _leaveOpen = leaveOpen;
        _values = new object?[columns.Count];
        Hash = hash ?? new RowHash.Accumulator();
    }

    /// <summary>
    /// The hash of the lines read so far, to compare against the manifest. Shared with
    /// the other range entries of the same table when the caller passed one in.
    /// </summary>
    public RowHash.Accumulator Hash { get; }

    /// <summary>How many rows this reader has read, which is not the shared accumulator's count.</summary>
    public long Rows { get; private set; }

    /// <summary>
    /// The decoded values of the current row. The array is reused, so a caller that
    /// keeps rows has to copy.
    /// </summary>
    public IReadOnlyList<object?> Current => _values;

    /// <summary>
    /// The current row's bytes, without the terminator - what the hash was taken over.
    /// Valid until the next read.
    /// </summary>
    public ReadOnlyMemory<byte> CurrentLine => _buffer.AsMemory(_lineStart, _lineLength);

    /// <summary>
    /// Advances to the next row. False at the end of the entry.
    /// </summary>
    public async ValueTask<bool> ReadAsync(CancellationToken cancellationToken = default)
    {
        while(true)
        {
            var newline = Array.IndexOf(_buffer, ArchiveFormat.LineTerminator, _start, _end - _start);

            if(newline >= 0)
            {
                Accept(_start, newline - _start);
                _start = newline + 1;
                return true;
            }

            if(_exhausted)
            {
                // A last line with no terminator. Written by nobody here, accepted anyway:
                // an archive that lost its final newline to a truncated copy is still
                // worth reading, and the row hash will say whether the row survived.
                if(_end > _start)
                {
                    Accept(_start, _end - _start);
                    _start = _end;
                    return true;
                }

                return false;
            }

            await FillAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if(_disposed)
            return;

        _disposed = true;

        if(!_leaveOpen)
            await _source.DisposeAsync().ConfigureAwait(false);
    }

    private void Accept(int offset, int length)
    {
        // A file written on Windows by something other than this library may carry CRLF.
        // The carriage return is not part of the row and must not be part of its hash.
        if(length > 0 && _buffer[offset + length - 1] == (byte)'\r')
            length--;

        _lineStart = offset;
        _lineLength = length;

        Hash.AddRow(_buffer.AsSpan(offset, length));
        Rows++;

        RowDecoder.Decode(_columns, _buffer.AsSpan(offset, length), _values);
    }

    private async ValueTask FillAsync(CancellationToken cancellationToken)
    {
        if(_start > 0)
        {
            Array.Copy(_buffer, _start, _buffer, 0, _end - _start);
            _end -= _start;
            _start = 0;
        }

        if(_end == _buffer.Length)
        {
            // One row larger than the buffer. A varbinary(max) of a hundred megabytes is
            // one line, so growing is the normal case rather than an error.
            Array.Resize(ref _buffer, _buffer.Length * 2);
        }

        var read = await _source.ReadAsync(_buffer.AsMemory(_end), cancellationToken).ConfigureAwait(false);

        if(read == 0)
            _exhausted = true;
        else
            _end += read;
    }
}
