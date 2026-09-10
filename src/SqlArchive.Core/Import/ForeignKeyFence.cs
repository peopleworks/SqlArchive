using Microsoft.Data.SqlClient;

namespace SqlArchive.Core.Import;

/// <summary>
/// Switches off the destination's foreign keys around the data phase, and puts them back
/// exactly as they were.
/// <para>
/// Only ever needed by a restore into a destination that already has its keys - the
/// migration over a live database, and <c>--data-only</c>. On a fresh restore the keys
/// are in phase 070 and do not exist yet while the rows are loading, which is what the
/// phases are for; there this finds nothing and costs one query.
/// </para>
/// <para>
/// <b>Why it has to exist.</b> Every table is replaced whole, and SQL Server checked all
/// three ways out against a live server: a <c>TRUNCATE</c> of a table another key points
/// at is refused (4712); a <c>DELETE</c> of one whose children hold rows is refused
/// (547); and an <c>ALTER TABLE ... SWITCH</c> needs the destination empty first, which
/// is the same problem one step earlier. Ordering the tables does not help, because every
/// child is being emptied and refilled too - the parent is empty in the middle of the
/// restore whatever order they go in. The keys therefore come off for the length of the
/// data phase and are validated on the way back, which is also the only moment at which
/// validating them means anything: the rows on both sides are the archive's.
/// </para>
/// </summary>
public sealed class ForeignKeyFence
{
    private readonly List<DestinationForeignKey> _lowered = [];

    private ForeignKeyFence(IReadOnlyList<DestinationForeignKey> all) => All = all;

    /// <summary>Every foreign key the destination had when the fence was opened.</summary>
    public IReadOnlyList<DestinationForeignKey> All { get; }

    /// <summary>The keys this restore switched off, in the order it did.</summary>
    public IReadOnlyList<DestinationForeignKey> Lowered => _lowered;

    /// <summary>
    /// Reads the destination's foreign keys and switches off every enabled one that
    /// touches a table being published, on either side of it.
    /// </summary>
    /// <param name="connection">An open connection to the destination.</param>
    /// <param name="published">The tables whose rows are about to be replaced, as <c>schema.name</c>.</param>
    /// <param name="dryRun">When true, nothing is altered and the answer only says what would be.</param>
    /// <param name="commandTimeoutSeconds">Seconds a statement may take.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public static async Task<ForeignKeyFence> LowerAsync(
        SqlConnection connection,
        IReadOnlySet<string> published,
        bool dryRun,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(published);

        var all = await DestinationCatalog.ForeignKeysAsync(connection, commandTimeoutSeconds, cancellationToken)
            .ConfigureAwait(false);

        var fence = new ForeignKeyFence(all);

        foreach(var key in all)
        {
            if(!Touches(key, published))
                continue;

            fence._lowered.Add(key);

            if(!dryRun)
            {
                await DestinationCatalog
                    .ExecuteAsync(connection, key.Lower, commandTimeoutSeconds, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return fence;
    }

    /// <summary>
    /// True when this restore has to switch the key off: it is on, and one of the two
    /// tables it joins is about to be replaced whole.
    /// </summary>
    /// <remarks>
    /// Either side, not just the referenced one. A child whose parent is being replaced
    /// cannot be filled before the parent is - and the parent is empty in the middle of
    /// its own publication whichever order the two go in, so there is no order that makes
    /// this unnecessary.
    /// <para>
    /// A key that was <i>already</i> off stays off and is not recorded. Putting back
    /// something that was not there is the one way this could damage a destination it was
    /// meant to leave alone.
    /// </para>
    /// </remarks>
    public static bool Touches(DestinationForeignKey key, IReadOnlySet<string> published)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(published);

        return !key.IsDisabled &&
               (published.Contains($"{key.ParentSchema}.{key.ParentName}") ||
                published.Contains($"{key.ReferencedSchema}.{key.ReferencedName}"));
    }

    /// <summary>
    /// Puts every key this fence lowered back into the state it was in, and says which
    /// ones no longer hold.
    /// </summary>
    /// <param name="connection">An open connection to the destination.</param>
    /// <param name="revalidate">
    /// Whether a key that was trusted is validated on the way back. Turning this off
    /// leaves it enabled and untrusted, which is a state SQL Server has a name for and
    /// an optimiser that stops believing it - so it is a deliberate choice and not a
    /// default.
    /// </param>
    /// <param name="commandTimeoutSeconds">Seconds a statement may take.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>One message per key that could not be put back, empty when all of them were.</returns>
    public async Task<IReadOnlyList<string>> RaiseAsync(
        SqlConnection connection,
        bool revalidate,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var problems = new List<string>();

        foreach(var key in _lowered)
        {
            var sql = key.Raise(revalidate);

            try
            {
                await DestinationCatalog.ExecuteAsync(connection, sql, commandTimeoutSeconds, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch(SqlException ex)
            {
                // The validation is what failed, so the key is still off. Enabling it
                // untrusted is strictly better than leaving it off: from here on it is
                // enforced, and the rows already in the destination are the ones the
                // message is about. The failure is still reported - it is not fixed by
                // this - but the destination is not left with a key that enforces
                // nothing and looks like one that does.
                var enabled = false;

                try
                {
                    await DestinationCatalog.ExecuteAsync(
                        connection, key.Raise(revalidate: false), commandTimeoutSeconds, cancellationToken)
                        .ConfigureAwait(false);

                    enabled = true;
                }
                catch(SqlException)
                {
                }

                problems.Add(
                    $"{key.Identifier} was switched off for the data phase and would not validate against the " +
                    $"rows that were published: {ex.Message} It is now " +
                    (enabled ? "enabled and untrusted" : "still switched off") +
                    $"; the statement that failed is: {sql}");
            }
        }

        return problems;
    }
}
