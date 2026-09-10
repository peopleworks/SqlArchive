using SqlArchive.Core.Format;

namespace SqlArchive.Core.Import;

/// <summary>What a restore did, table by table.</summary>
/// <param name="Path">The archive it read.</param>
/// <param name="Mode">Which of the three restores this was.</param>
/// <param name="DryRun">True when nothing was written and this is what would have happened.</param>
/// <param name="Rows">Rows published, across every table.</param>
/// <param name="SchemaBatches">Statements run against the destination before and after the data.</param>
/// <param name="Elapsed">How long it took.</param>
/// <param name="Tables">One entry per table in the archive, in the manifest's order.</param>
/// <param name="Notices">
/// Everything worth telling the operator: foreign keys switched off and put back, a
/// table published by an insert because no switch could reach it, a diff that changed
/// the destination's shape.
/// </param>
/// <param name="Manifest">The archive's manifest, so a caller does not have to reopen the file to see it.</param>
public sealed record ImportResult(
    string Path,
    ImportMode Mode,
    bool DryRun,
    long Rows,
    int SchemaBatches,
    TimeSpan Elapsed,
    IReadOnlyList<ImportTableResult> Tables,
    IReadOnlyList<string> Notices,
    ArchiveManifest Manifest)
{
    /// <summary>Tables whose rows are now in the destination.</summary>
    public int Published => Tables.Count(t => t.Outcome is ImportTableOutcome.Published);

    /// <summary>Tables the guard would not let through, or that failed. The destination holds what it held before for each of them.</summary>
    public int Refused => Tables.Count(t => t.Outcome is ImportTableOutcome.Refused or ImportTableOutcome.Failed);

    /// <summary>Tables deliberately not published: filtered out, already done on an earlier run, or carrying no rows in the archive.</summary>
    public int Skipped => Tables.Count(t => t.Outcome is ImportTableOutcome.Skipped);

    /// <summary>True when every table that was meant to be published was.</summary>
    public bool Complete => Refused == 0;
}

/// <summary>One table's outcome.</summary>
/// <param name="Schema">Its schema.</param>
/// <param name="Name">Its name.</param>
/// <param name="Outcome">What happened to it.</param>
/// <param name="Rows">Rows published, or the rows that would have been.</param>
/// <param name="Publication">How the rows reached the destination, in words - for the summary and for a bug report.</param>
/// <param name="Reason">Why, when the outcome is anything other than published.</param>
public sealed record ImportTableResult(
    string Schema,
    string Name,
    ImportTableOutcome Outcome,
    long Rows,
    string? Publication = null,
    string? Reason = null)
{
    /// <summary>The two-part name, for messages.</summary>
    public string Identifier => $"[{Schema}].[{Name}]";
}

/// <summary>What became of one table.</summary>
public enum ImportTableOutcome
{
    /// <summary>Its rows are in the destination, and they are the ones the manifest declares.</summary>
    Published,

    /// <summary>
    /// The guard would not let it through: what came out of the archive, or what landed
    /// in staging, is not what the manifest says it should be. The destination holds
    /// exactly what it held before.
    /// </summary>
    Refused,

    /// <summary>Something went wrong that is not the guard. The destination holds exactly what it held before.</summary>
    Failed,

    /// <summary>Not attempted: filtered out, carrying no rows in the archive, or already done by an earlier run.</summary>
    Skipped,

    /// <summary>A dry run: this is the table that would have been published.</summary>
    WouldPublish
}
