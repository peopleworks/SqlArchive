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

    // 2 used to mean "the verb exists in the help and its implementation does not".
    // Every verb is built now, so nothing returns it - and it is deliberately not
    // reused for something else, because a script written against the old meaning
    // would keep working and mean the wrong thing.

    /// <summary>
    /// The comparison ran, correctly and to the end, and the two sides do not match.
    /// <para>
    /// Distinct from <see cref="Failed"/>: "the archive has drifted from the database"
    /// and "the tool could not make the comparison" are different answers, and a nightly job that treats a corrupt archive
    /// and an unreachable server as the same event will eventually act on the wrong one.
    /// A verify that finds differences did its job.
    /// </para>
    /// </summary>
    public const int Differences = 3;
}
