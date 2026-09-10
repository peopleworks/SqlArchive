namespace SqlArchive.Core.Import;

/// <summary>
/// A restore that stopped rather than put back something that is not what was archived.
/// </summary>
/// <remarks>
/// The refusals divide in two, and the division is worth keeping in mind when reading
/// the messages. One kind is about the archive - a file whose bytes do not add up to
/// what the manifest declares - and there the destination has not been touched at all.
/// The other is about the destination - a shape the rows will not go into - and there
/// the answer is to say which table and which column, because that is the only thing an
/// operator can act on.
/// </remarks>
public class ImportException : Exception
{
    public ImportException(string message) : base(message) { }

    public ImportException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>A journal that cannot be picked up by this run.</summary>
public sealed class ImportResumeException : ImportException
{
    public ImportResumeException(string message) : base(message) { }
}

/// <summary>
/// The destination's shape will not take the archive's rows, naming the table and the
/// column.
/// </summary>
/// <remarks>
/// Its own type because it is the one failure a caller can usefully catch: it says the
/// destination is wrong, not that the archive is, and the two are fixed by different
/// people.
/// </remarks>
public sealed class ImportShapeException : ImportException
{
    public ImportShapeException(string message) : base(message) { }
}
