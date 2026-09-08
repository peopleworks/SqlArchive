namespace SqlArchive.Core.Format;

/// <summary>
/// An archive, or a manifest, that this build cannot read.
/// </summary>
public class ArchiveFormatException : Exception
{
    public ArchiveFormatException(string message) : base(message) { }

    public ArchiveFormatException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// A value that cannot be written into the archive without changing it.
/// <para>
/// This is thrown rather than worked around on purpose. Every alternative - a
/// replacement character, a truncated fraction, a best-effort string - produces an
/// archive that looks correct and is not, and nobody finds out until the day someone
/// tries to restore from it, by which time the source is gone. A refusal is recoverable;
/// a silent lie is not.
/// </para>
/// </summary>
public sealed class ArchiveEncodingException : ArchiveFormatException
{
    public ArchiveEncodingException(string column, string message)
        : base($"Column [{column}]: {message}") =>
        Column = column;

    /// <summary>The column whose value could not be written.</summary>
    public string Column { get; }
}
