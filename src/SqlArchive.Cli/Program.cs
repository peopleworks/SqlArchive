namespace SqlArchive.Cli;

/// <summary>
/// El punto de entrada. Los comandos llegan en WP 2.5; por ahora esto existe para que
/// la solución tenga cuatro proyectos que compilan y CI tenga algo que construir.
/// </summary>
internal static class Program
{
    private static int Main()
    {
        Console.WriteLine("sqlarchive — en construcción. Ver DESIGN.md.");
        return 0;
    }
}
