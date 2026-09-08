namespace SqlArchive.Cli;

/// <summary>
/// What the process returns. A script is going to branch on these, so they are named
/// here rather than written as bare numbers wherever a command happens to end.
/// </summary>
internal static class ExitCodes
{
    /// <summary>The command did what it says it does.</summary>
    public const int Ok = 0;

    /// <summary>It tried and failed: a bad path, a refused value, an unreadable archive.</summary>
    public const int Failed = 1;

    /// <summary>
    /// The verb exists in the help and its implementation does not yet. Distinct from
    /// <see cref="Failed"/> on purpose: "this tool cannot do that yet" and "this run went
    /// wrong" are different answers, and a script that has to tell them apart should not
    /// have to read the message to do it.
    /// </summary>
    public const int NotBuiltYet = 2;
}
