using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Spectre.Console;
using Spectre.Console.Cli;
using SqlArchive.Cli.Commands;
using SqlArchive.Core.Format;

// The CLI is tested through the same entry point the operator types into, with the same
// parser, the same flags and the same exit codes. A test that calls something else tests
// something else.
[assembly: InternalsVisibleTo("SqlArchive.Core.Tests")]

namespace SqlArchive.Cli;

/// <summary>
/// The four verbs of SqlArchive, of which one is finished.
/// <para>
/// <c>inspect</c> works. <c>export</c>, <c>import</c> and <c>verify</c> are declared here
/// with the options they will take and refuse to run, naming the work package that brings
/// them. Listing what is coming is useful; pretending it works is not, and a command that
/// takes arguments and returns zero without doing anything is worse than one that is not
/// there yet.
/// </para>
/// </summary>
internal static class Program
{
    /// <summary>What <c>--version</c> reports. One line in the csproj drives it.</summary>
    internal static string Version { get; } =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    private static int Main(string[] args)
    {
        // An archive is UTF-8 by definition and this tool prints identifiers out of it.
        // A console left on the machine's ANSI code page turns Böhm into B?hm on the way
        // out. Guarded because there is not always a console to configure.
        try
        {
            Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }
        catch(IOException)
        {
            // Redirected somewhere that has no code page to set. Nothing to do about it.
        }

        return Run(args);
    }

    /// <param name="console">
    /// Where the help and the errors go. Null is the real console; the tests hand in a
    /// recorder, because Spectre renders help through the configured console and not
    /// through the static one.
    /// </param>
    internal static int Run(string[] args, IAnsiConsole? console = null)
    {
        var app = new CommandApp();
        app.Configure(config => Configure(config, console));
        return app.Run(args);
    }

    private static void Configure(IConfigurator config, IAnsiConsole? console)
    {
        if(console is not null)
            config.ConfigureConsole(console);

        config.SetApplicationName("sqlarchive");
        config.SetApplicationVersion(Version);

        // Without this, an option nobody declared is quietly collected as a leftover
        // argument and the command runs anyway - so a typo in --schema-only exports the
        // data and reports success. A flag this tool does not know is an error.
        config.UseStrictParsing();

        // Every example in the help is parsed at startup. An example that drifted away
        // from the options it demonstrates is then a failure here rather than a wrong
        // answer on somebody's screen.
        config.ValidateExamples();

        config.SetExceptionHandler((exception, _) => Report(exception));

        config.AddCommand<InspectCommand>("inspect")
            .WithDescription("Show what an archive says about itself, without unpacking it.")
            .WithExample("inspect", "Ventas.sqlarchive")
            .WithExample("inspect", "Ventas.sqlarchive", "--entries")
            .WithExample("inspect", "Ventas.sqlarchive", "--json");

        config.AddCommand<ExportCommand>("export")
            .WithDescription("Read a database into an archive. Not built yet - work package 2.2.")
            .WithExample("export", "--source", "Server=SQL2022;Database=Ventas;Integrated Security=true", "--out", "Ventas.sqlarchive");

        config.AddCommand<ImportCommand>("import")
            .WithDescription("Restore an archive, as a migration over what is already there. Not built yet - work package 2.3.")
            .WithExample("import", "Ventas.sqlarchive", "--destination", "Server=SQL2022;Database=VentasCopia;Integrated Security=true");

        config.AddCommand<VerifyCommand>("verify")
            .WithDescription("Prove an archive is intact, or that a database still matches it. Not built yet - work package 2.4.")
            .WithExample("verify", "Ventas.sqlarchive")
            .WithExample("verify", "Ventas.sqlarchive", "--against", "Server=SQL2022;Database=Ventas;Integrated Security=true");
    }

    /// <summary>
    /// One place decides what the process returns, so that "this cannot be done yet",
    /// "you asked for something impossible" and "it broke" are three different answers
    /// rather than one exit code and three paragraphs.
    /// </summary>
    private static int Report(Exception exception)
    {
        switch(exception)
        {
            case NotBuiltYetException pending:
                AnsiConsole.WriteLine();

                AnsiConsole.Write(
                    new Panel(new Markup(pending.Message.EscapeMarkup()))
                        .Header($" {pending.Verb} - work package {pending.WorkPackage} ")
                        .Border(BoxBorder.Rounded)
                        .BorderColor(Color.Yellow)
                        .Expand());

                AnsiConsole.WriteLine();
                return ExitCodes.NotBuiltYet;

            // Spectre's own: a flag that does not exist, a value that failed the
            // settings' own Validate, an example that no longer parses. It renders these
            // better than a stack trace can, and taking over the handler is what would
            // otherwise have thrown that away.
            case CommandAppException parse:
                if(parse.Pretty is { } pretty)
                    AnsiConsole.Write(pretty);
                else
                    AnsiConsole.MarkupLine($"[red]{parse.Message.EscapeMarkup()}[/]");

                return ExitCodes.Failed;

            // The archive said what is wrong with it, in a sentence written for whoever
            // is holding the file. A stack trace on top of that helps nobody.
            case ArchiveFormatException format:
                AnsiConsole.MarkupLine($"[red]{format.Message.EscapeMarkup()}[/]");
                return ExitCodes.Failed;

            case FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException:
                AnsiConsole.MarkupLine($"[red]{exception.Message.EscapeMarkup()}[/]");
                return ExitCodes.Failed;

            default:
                AnsiConsole.WriteException(
                    exception,
                    ExceptionFormats.ShortenPaths | ExceptionFormats.ShortenTypes | ExceptionFormats.ShortenMethods);

                return ExitCodes.Failed;
        }
    }
}
