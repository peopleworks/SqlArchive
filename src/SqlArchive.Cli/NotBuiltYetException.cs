namespace SqlArchive.Cli;

/// <summary>
/// Thrown by a verb whose options exist and whose implementation does not.
/// <para>
/// The alternative - accepting the arguments and returning zero - is worse than not
/// having the command at all. A script that calls it succeeds, a scheduled job reports
/// success, and nobody finds out that nothing was archived until the day somebody needs
/// the archive. So the verb is listed, its options are documented, and running it fails
/// loudly with the name of the work package that will make it work.
/// </para>
/// </summary>
internal sealed class NotBuiltYetException : Exception
{
    public NotBuiltYetException(string verb, string workPackage, string because)
        : base(Compose(verb, workPackage, because))
    {
        Verb = verb;
        WorkPackage = workPackage;
    }

    /// <summary>The verb that was asked for.</summary>
    public string Verb { get; }

    /// <summary>The work package that brings it, as DESIGN.md numbers them.</summary>
    public string WorkPackage { get; }

    private static string Compose(string verb, string workPackage, string because) =>
        $"'{verb}' is not built yet - it is work package {workPackage}. {because}\n\n" +
        "Nothing was read and nothing was written, and this returns " +
        $"{ExitCodes.NotBuiltYet} rather than 0 so that a script calling it fails instead of " +
        "reporting work that did not happen.\n\n" +
        "The options in --help are the ones this verb will take. They are published so the " +
        "shape of the command can be argued with before it exists, not so it can be run.\n\n" +
        "One verb works end to end in this build:  sqlarchive inspect <ARCHIVE>";
}
