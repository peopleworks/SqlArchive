using SqlArchive.Core.Export;
using SqlSchemaDiff.Models;

namespace SqlArchive.Core.Verify;

/// <summary>
/// Puts the two sides of a schema comparison on the same footing before the differ sees
/// them.
/// <para>
/// A restored database is never byte-for-byte the database the archive came from, and
/// most of the ways it differs are not drift - they are the server answering with its
/// own name for something the archive never named. <b>A verify that cries drift on a
/// clean round trip is worthless</b>, so everything left out of the comparison is left
/// out here, in one place, and written into the report as a sentence rather than
/// discovered by whoever runs it.
/// </para>
/// <para>
/// The bar for being here is that the difference cannot be avoided by a correct restore.
/// Anything a restore could have got right stays in the comparison, however noisy.
/// </para>
/// </summary>
internal static class ComparableSchema
{
    /// <summary>
    /// The archive's snapshot and the database's, ready to be diffed against each other.
    /// </summary>
    /// <param name="archive">The manifest's schema snapshot.</param>
    /// <param name="live">What the extractor read from the database.</param>
    /// <param name="keep">Which tables this run is comparing at all.</param>
    /// <param name="filtered">True when <paramref name="keep"/> is anything other than "all of them".</param>
    /// <param name="ignored">Where each thing left out of the comparison is written down.</param>
    public static (DatabaseSnapshot Archive, DatabaseSnapshot Live) Prepare(
        DatabaseSnapshot archive,
        DatabaseSnapshot live,
        Func<string, string, bool> keep,
        bool filtered,
        IList<string> ignored)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(ignored);

        // The database's name. An archive of Ventas restored into VentasCopia is a
        // correct restore, and the differ never compares the two anyway - saying so here
        // is for the person reading the report and wondering whether it was checked.
        ignored.Add(
            "The name of the database. An archive of one database restored into another with a different name is " +
            "a correct restore, not drift.");

        live = WithoutSequenceStartValues(archive, live, ignored);

        if(!filtered)
            return (archive, live);

        // Both sides through the same sieve. Comparing a partial archive against a whole
        // database would report every table the run was told not to look at.
        var discarded = new List<string>();

        var restrictedArchive = SnapshotSelection.Restrict(archive, keep, archive.DatabaseName, discarded);
        var restrictedLive = SnapshotSelection.Restrict(live, keep, live.DatabaseName, discarded);

        ignored.Add(
            "Tables the --table and --exclude patterns left out, on both sides at once. A run that compared the " +
            "archive's chosen tables against the whole database would report every table it was told to ignore.");

        // Views, procedures and the rest are not tables and no glob names them, so they
        // stay in the comparison. Said out loud because a run with --table looks as
        // though it narrowed everything, and it did not.
        ignored.Add(
            "Nothing else was narrowed: --table and --exclude name tables, so views, procedures, functions, " +
            "triggers, sequences and types are still compared in full.");

        return (restrictedArchive, restrictedLive);
    }

    /// <summary>
    /// The database's sequences with the archive's start values written over them, so
    /// that the one property of a sequence a restore necessarily changes stays out of
    /// the comparison.
    /// </summary>
    /// <remarks>
    /// <b>Found against SQL Server 2025, not assumed.</b> <c>ALTER SEQUENCE ... RESTART
    /// WITH n</c> moves <c>sys.sequences.start_value</c> as well as
    /// <c>current_value</c> - the two come back as <c>4</c> and <c>4</c> after a restart
    /// of a sequence declared <c>START WITH 1</c>. The archive's own finalize phase
    /// issues exactly that restart, because a restored database that handed out numbers
    /// already given out would be worse than one that lost the sequence. So the archive
    /// says the sequence starts at 1, the database it built says it starts at 300, and
    /// the differ - which is right to say a start value cannot be altered - reports every
    /// used sequence in the database as needing a DROP and CREATE.
    /// <para>
    /// SQL Server keeps no separate record of the value a sequence was declared with, so
    /// there is nothing to compare that would tell a restart from a redeclaration. The
    /// property is dropped rather than guessed at. Everything else about the sequence -
    /// its type, increment, bounds, cycling and cache - is still compared, and
    /// SqlSchemaDiff already leaves <c>current_value</c> out for the same kind of reason.
    /// </para>
    /// </remarks>
    private static DatabaseSnapshot WithoutSequenceStartValues(
        DatabaseSnapshot archive,
        DatabaseSnapshot live,
        IList<string> ignored)
    {
        var archived = archive.Objects
            .Where(o => o.Type == DbObjectType.Sequence && o.Sequence is not null)
            .ToDictionary(o => o.Key, StringComparer.OrdinalIgnoreCase);

        if(archived.Count == 0)
            return live;

        ignored.Add(
            "A sequence's start value. ALTER SEQUENCE ... RESTART WITH moves start_value as well as the current " +
            "value, and the archive's finalize phase issues that restart so a restored database does not hand out " +
            "numbers that were already given out. What a sequence reports as its start is therefore how far it has " +
            "been used, not how it was declared. Its type, increment, bounds, cycling and cache are still compared.");

        var objects = new List<DbSchemaObject>(live.Objects.Count);
        var replaced = false;

        foreach(var entry in live.Objects)
        {
            if(entry.Type != DbObjectType.Sequence ||
               entry.Sequence is not { } sequence ||
               !archived.TryGetValue(entry.Key, out var counterpart) ||
               string.Equals(sequence.StartValue, counterpart.Sequence!.StartValue, StringComparison.Ordinal))
            {
                objects.Add(entry);
                continue;
            }

            // A copy rather than an assignment: the snapshot handed in belongs to the
            // caller, and a verify that edited it would change what a later comparison
            // in the same process was looking at.
            objects.Add(new DbSchemaObject
            {
                Type = entry.Type,
                Schema = entry.Schema,
                Name = entry.Name,
                Definition = entry.Definition,
                Dependencies = entry.Dependencies,
                Table = entry.Table,
                UsesAnsiNulls = entry.UsesAnsiNulls,
                UsesQuotedIdentifier = entry.UsesQuotedIdentifier,
                Trigger = entry.Trigger,
                Sequence = Restarted(sequence, counterpart.Sequence!.StartValue),
                TableType = entry.TableType,
                Synonym = entry.Synonym
            });

            replaced = true;
        }

        if(!replaced)
            return live;

        return new DatabaseSnapshot
        {
            DatabaseName = live.DatabaseName,
            GeneratedAtUtc = live.GeneratedAtUtc,
            Schemas = live.Schemas,
            SchemaOwners = live.SchemaOwners,
            Types = live.Types,
            Objects = objects,
            FormatVersion = live.FormatVersion,
            GeneratedBy = live.GeneratedBy
        };
    }

    private static SequenceModel Restarted(SequenceModel sequence, string startValue) => new()
    {
        Schema = sequence.Schema,
        Name = sequence.Name,
        TypeName = sequence.TypeName,
        Precision = sequence.Precision,
        Scale = sequence.Scale,
        StartValue = startValue,
        Increment = sequence.Increment,
        MinValue = sequence.MinValue,
        MaxValue = sequence.MaxValue,
        IsCycling = sequence.IsCycling,
        IsCached = sequence.IsCached,
        CacheSize = sequence.CacheSize,
        CurrentValue = sequence.CurrentValue
    };
}
