using SqlArchive.Core.Format;

namespace SqlArchive.Core.Import;

/// <summary>
/// Whether a destination table will take an archived table's rows, decided before
/// anything is created and answered by naming the table and the column.
/// <para>
/// This is what <c>--data-only</c> stands on - "refuse clearly where the shape does not
/// match" - and it runs in every mode, because a diff that could not reconcile a table is
/// exactly the case where the next thing to happen would otherwise be a bulk copy failing
/// on row one with an error that names a staging table nobody has heard of.
/// </para>
/// </summary>
public static class DestinationShape
{
    /// <summary>
    /// Checks the destination's columns against the archived ones.
    /// </summary>
    /// <param name="identifier">The table, for the message.</param>
    /// <param name="columns">The archived columns, in the archive's order.</param>
    /// <param name="destination">The destination's columns as its catalog has them now.</param>
    /// <exception cref="ImportShapeException">
    /// When the rows will not go in. Three ways, and they are different failures. A column
    /// the archive carries and the destination does not have is a table that is not the
    /// same table. A column the destination has but will not accept - computed, a
    /// rowversion, a period column - is one the archive deliberately does not carry, and
    /// the two agreeing about that is the check. And a column the destination requires and
    /// the archive has nothing for is a row that cannot be inserted at all, which is worth
    /// saying here rather than as error 515 halfway through a bulk copy.
    /// </exception>
    public static void Check(
        string identifier,
        IReadOnlyList<ArchiveColumn> columns,
        IReadOnlyList<DestinationColumn> destination)
    {
        ArgumentException.ThrowIfNullOrEmpty(identifier);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(destination);

        if(destination.Count == 0)
        {
            throw new ImportShapeException(
                $"{identifier} is not in the destination. With --data-only the shape has to be there already; " +
                "in the other modes the schema phases or the diff should have created it, so this means one of " +
                "them was refused.");
        }

        var byName = destination.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);

        foreach(var column in columns)
        {
            if(!byName.TryGetValue(column.Name, out var target))
            {
                throw new ImportShapeException(
                    $"{identifier} in the destination has no column called '{column.Name}', and the archive " +
                    "carries values for it. The two are not the same table.");
            }

            if(!target.IsWritable)
            {
                throw new ImportShapeException(
                    $"{identifier}.[{column.Name}] is " + Why(target) +
                    " in the destination, and SQL Server assigns it rather than accepting it. The archive " +
                    "carries a value for it, so the destination's column is not the column that was archived.");
            }
        }

        var archived = columns.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach(var target in destination)
        {
            if(!target.IsWritable || archived.Contains(target.Name) || target.IsNullable || target.HasDefault)
                continue;

            throw new ImportShapeException(
                $"{identifier}.[{target.Name}] is NOT NULL with no default in the destination, and the archive " +
                "carries nothing for it. Every row would be refused; the column has to be made nullable, given " +
                "a default, or dropped.");
        }
    }

    private static string Why(DestinationColumn column) =>
        column.IsComputed ? "a computed column"
        : column.IsGeneratedAlways ? "a GENERATED ALWAYS period column"
        : "a rowversion";
}
