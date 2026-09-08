namespace SqlArchive.Core.Format;

/// <summary>
/// One table's rows, read as a single sequence however many range entries hold them, and
/// hashed on the way past.
/// <para>
/// This is what a restore's guard reads. DESIGN.md calls that guard exact rather than
/// heuristic: SyncJob has to guess with a floor and a fraction because it does not know
/// how many rows there should be, and here the manifest says. The comparison is
/// <see cref="Rows"/> against <see cref="ArchiveTableEntry.RowCount"/> and
/// <see cref="Hash"/> against <see cref="ArchiveTableEntry.RowHash"/>, and if either
/// disagrees that table is not published - which is one table refused, not a restore
/// abandoned.
/// </para>
/// </summary>
public sealed class ArchiveTableReader : IAsyncDisposable
{
    private readonly ArchiveReader _archive;
    private readonly IReadOnlyList<ArchiveColumn> _columns;
    private readonly IReadOnlyList<string> _entries;

    private int _next;
    private Stream? _stream;
    private JsonlRowReader? _reader;
    private bool _disposed;

    internal ArchiveTableReader(ArchiveReader archive, ArchiveTableEntry table, IReadOnlyList<ArchiveColumn> columns)
    {
        _archive = archive;
        _columns = columns;
        _entries = table.DataFiles;
        Table = table;
        Hash = new RowHash.Accumulator();
    }

    /// <summary>The manifest's entry for this table - what the rows are being checked against.</summary>
    public ArchiveTableEntry Table { get; }

    /// <summary>The hash of every line read so far, across all the ranges.</summary>
    public RowHash.Accumulator Hash { get; }

    /// <summary>How many rows have been read, across all the ranges.</summary>
    public long Rows => Hash.Rows;

    /// <summary>The current row's values. The array is reused; a caller that keeps rows copies.</summary>
    public IReadOnlyList<object?> Current =>
        _reader?.Current ?? throw new InvalidOperationException("Read has not been called, or there are no more rows.");

    /// <summary>The current row's bytes, as they are in the file.</summary>
    public ReadOnlyMemory<byte> CurrentLine => _reader?.CurrentLine ?? ReadOnlyMemory<byte>.Empty;

    /// <summary>The entry the current row came from, for a message that has to say where.</summary>
    public string? CurrentEntry { get; private set; }

    public async ValueTask<bool> ReadAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        while(true)
        {
            if(_reader is not null && await _reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                return true;

            await CloseCurrentAsync().ConfigureAwait(false);

            if(_next >= _entries.Count)
                return false;

            CurrentEntry = _entries[_next++];
            _stream = _archive.OpenEntry(CurrentEntry);
            _reader = new JsonlRowReader(_stream, _columns, Hash);
        }
    }

    /// <summary>
    /// True when the rows read match what the manifest declares. Only meaningful once
    /// the table has been read to the end.
    /// </summary>
    public bool Matches() =>
        Rows == Table.RowCount &&
        string.Equals(Hash.Value, Table.RowHash, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// What differs, in the words the verdict uses: the count, or the content at the same
    /// count - which is the case a row count cannot see and the one that matters most.
    /// </summary>
    public string? Difference()
    {
        if(Rows != Table.RowCount)
            return $"{Table.Identifier}: the archive declares {Table.RowCount} rows and the data holds {Rows}.";

        if(!string.Equals(Hash.Value, Table.RowHash, StringComparison.OrdinalIgnoreCase))
            return $"{Table.Identifier}: {Rows} rows on both sides, and the content differs " +
                   $"(declared {Table.RowHash}, read {Hash.Value}).";

        return null;
    }

    public async ValueTask DisposeAsync()
    {
        if(_disposed)
            return;

        _disposed = true;
        await CloseCurrentAsync().ConfigureAwait(false);
    }

    private async ValueTask CloseCurrentAsync()
    {
        if(_reader is not null)
        {
            // Nothing to fold: every range reader was handed this accumulator, which is
            // sound precisely because the order the rows arrive in does not change it.
            await _reader.DisposeAsync().ConfigureAwait(false);
            _reader = null;
        }

        if(_stream is not null)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
            _stream = null;
        }
    }
}
