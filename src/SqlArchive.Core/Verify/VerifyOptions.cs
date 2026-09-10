namespace SqlArchive.Core.Verify;

/// <summary>
/// What a verify is being asked to compare, and how hard it is allowed to work at it.
/// <para>
/// <see cref="ConnectionString"/> is the option that changes the question. Without it
/// the archive is checked against itself and no server is touched at all, which is the
/// archival use: years from now, that is the only question still answerable. With it,
/// the same code answers the other two - whether a restored database matches the
/// archive, and whether a live one has drifted from it - and those two differ only in
/// which database is named.
/// </para>
/// </summary>
public sealed class VerifyOptions
{
    /// <summary>
    /// The database to compare against, or null to check only that the archive is
    /// intact. Null is not a degraded mode: it is the question an archive exists to
    /// answer when the database it came from is gone.
    /// </summary>
    public string? ConnectionString { get; init; }

    /// <summary>Table globs to include, as <c>schema.name</c>. Empty means all of them.</summary>
    public IReadOnlyList<string> IncludeTables { get; init; } = [];

    /// <summary>Table globs to leave out. Applied after <see cref="IncludeTables"/>.</summary>
    public IReadOnlyList<string> ExcludeTables { get; init; } = [];

    /// <summary>
    /// Compare the schema and stop: no table is read on either side.
    /// <para>
    /// This also keeps the integrity check off the data entries. Hashing them is reading
    /// the table on the archive's side, and someone who asked for a schema comparison of
    /// a hundred-gigabyte archive did not ask to decompress it.
    /// </para>
    /// </summary>
    public bool SchemaOnly { get; init; }

    /// <summary>How many tables to read at once, each through a connection of its own.</summary>
    public int Parallelism { get; init; } = Environment.ProcessorCount;

    /// <summary>Zero for no limit. A verify reads whole tables, so a default timeout is the wrong default.</summary>
    public int CommandTimeoutSeconds { get; init; }

    public IProgress<VerifyProgress>? Progress { get; init; }
}

/// <summary>Where a verify has got to.</summary>
/// <param name="Phase">One of <c>integrity</c>, <c>schema</c>, <c>tables</c>.</param>
/// <param name="Table">The table being read, when there is one.</param>
/// <param name="Done">Units finished.</param>
/// <param name="Total">Units in this phase, or zero when it is not counted in units.</param>
public sealed record VerifyProgress(string Phase, string? Table, int Done, int Total);
