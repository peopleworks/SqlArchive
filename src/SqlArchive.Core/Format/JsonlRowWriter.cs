using System.Data.Common;

namespace SqlArchive.Core.Format;

/// <summary>
/// Writes rows into one JSONL entry, hashing them as they go.
/// <para>
/// The hash costs nothing here: the line has just been encoded and is sitting in a
/// buffer, so the SHA-256 is one pass over bytes already in cache. That is the third of
/// the three reasons DESIGN.md gives for defining equality this way - export already does
/// the work, so the manifest gets its hash for free.
/// </para>
/// </summary>
public sealed class JsonlRowWriter : IAsyncDisposable
{
    private const int FlushThreshold = 64 * 1024;

    private readonly Stream _destination;
    private readonly IReadOnlyList<ArchiveColumn> _columns;
    private readonly bool _leaveOpen;
    private readonly CanonicalJsonBuffer _row = new();
    private readonly CanonicalJsonBuffer _pending = new(FlushThreshold + 8192);

    private bool _disposed;

    /// <param name="destination">Where the lines go. Not disposed unless asked for.</param>
    /// <param name="columns">The archived columns, in order. See <see cref="ArchiveColumns"/>.</param>
    /// <param name="hash">
    /// An accumulator to feed. Pass one shared by the ranges of a table being written in
    /// parallel to different entries, or leave it null for a fresh one.
    /// </param>
    /// <param name="leaveOpen">Whether disposing this leaves <paramref name="destination"/> open.</param>
    public JsonlRowWriter(
        Stream destination,
        IReadOnlyList<ArchiveColumn> columns,
        RowHash.Accumulator? hash = null,
        bool leaveOpen = true)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(columns);

        if(columns.Count == 0)
            throw new ArgumentException("A table with no archived columns has no rows to write.", nameof(columns));

        _destination = destination;
        _columns = columns;
        _leaveOpen = leaveOpen;
        Hash = hash ?? new RowHash.Accumulator();
    }

    /// <summary>The running table hash and row count.</summary>
    public RowHash.Accumulator Hash { get; }

    /// <summary>How many rows this writer has written. Not the accumulator's count, which may be shared.</summary>
    public long Rows { get; private set; }

    /// <summary>Encodes and writes the row the reader is positioned on.</summary>
    public ValueTask WriteAsync(DbDataReader reader, CancellationToken cancellationToken = default)
    {
        RowEncoder.Encode(_columns, reader, _row);
        return AppendAsync(cancellationToken);
    }

    /// <summary>Encodes and writes a row already in .NET values.</summary>
    public ValueTask WriteAsync(IReadOnlyList<object?> values, CancellationToken cancellationToken = default)
    {
        RowEncoder.Encode(_columns, values, _row);
        return AppendAsync(cancellationToken);
    }

    /// <summary>
    /// Drains every row the reader has left, and answers how many there were. The shape
    /// export uses for a table it reads in one pass.
    /// </summary>
    public async Task<long> WriteAllAsync(DbDataReader reader, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);

        var written = 0L;

        while(await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            await WriteAsync(reader, cancellationToken).ConfigureAwait(false);
            written++;
        }

        return written;
    }

    public async ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        if(_pending.Length == 0)
            return;

        await _destination.WriteAsync(_pending.WrittenMemory, cancellationToken).ConfigureAwait(false);
        _pending.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        if(_disposed)
            return;

        _disposed = true;

        await FlushAsync().ConfigureAwait(false);
        await _destination.FlushAsync().ConfigureAwait(false);

        if(!_leaveOpen)
            await _destination.DisposeAsync().ConfigureAwait(false);
    }

    private ValueTask AppendAsync(CancellationToken cancellationToken)
    {
        // The terminator is not hashed. A row written as the last line of one range file
        // and the same row written in the middle of a whole-table file have to hash the
        // same, or re-reading a table with different range boundaries would look like drift.
        Hash.AddRow(_row.WrittenSpan);
        Rows++;

        _pending.Write(_row.WrittenSpan);
        _pending.WriteByte(ArchiveFormat.LineTerminator);

        return _pending.Length >= FlushThreshold ? FlushAsync(cancellationToken) : ValueTask.CompletedTask;
    }
}
