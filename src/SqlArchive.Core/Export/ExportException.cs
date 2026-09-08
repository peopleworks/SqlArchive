namespace SqlArchive.Core.Export;

/// <summary>
/// An export that stopped rather than produce an archive that would be wrong or that
/// would claim more than it can.
/// </summary>
/// <remarks>
/// Every one of these could have been a warning and a partial archive instead. None of
/// them is, and the reason is always the same: the archive outlives everyone who knows
/// how it was made, so the only signal it can carry is what is written in it. A refusal
/// is read by somebody who can still do something about it.
/// </remarks>
public class ExportException : Exception
{
    public ExportException(string message) : base(message) { }

    public ExportException(string message, Exception inner) : base(message, inner) { }
}
