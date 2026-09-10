namespace SqlArchive.Core.Tests;

/// <summary>
/// The test classes that swap <see cref="Spectre.Console.AnsiConsole.Console"/> for a
/// recorder, run one at a time.
/// <para>
/// The console is a static and xunit runs different classes in parallel, so two classes
/// recording at once interleave and each sees the other's output in the middle of its
/// own. It is named here rather than left to xunit's generated per-class name, which a
/// second class could only join by writing that generated string out and hoping it does
/// not change.
/// </para>
/// </summary>
[CollectionDefinition(Name)]
public sealed class ConsoleCollection
{
    public const string Name = "console";
}
