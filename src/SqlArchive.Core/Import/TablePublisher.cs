using System.Globalization;
using Microsoft.Data.SqlClient;
using SqlArchive.Core.Format;
using SqlArchive.Core.Verify;
using SqlSchemaDiff.Services;
using SyncJob.Core;
using SyncJob.Core.Model;

namespace SqlArchive.Core.Import;

/// <summary>
/// One table, from the archive into the destination: staged, checked twice, and then
/// published in a single transaction.
/// <para>
/// <b>The guard here is exact.</b> SyncJob has to guess - a floor of rows and a fraction
/// of what the destination already holds - because it does not know how many rows there
/// should be. Here the manifest says, so both sides are compared against it:
/// </para>
/// <list type="number">
/// <item>
/// what came <i>out of the archive</i>: <see cref="ArchiveTableReader.Rows"/> and its
/// running hash against the manifest's entry. A truncated file, a corrupted byte or an
/// entry that stopped early is caught here.
/// </item>
/// <item>
/// what actually <i>landed</i>: <see cref="LiveTableDigest"/> over the staging table.
/// This is the one that catches a load that silently dropped or duplicated rows, and it
/// is the reason <c>LiveTableDigest</c> exists. Nothing about the first check implies the
/// second: the archive can be read perfectly and still be written wrong.
/// </item>
/// </list>
/// <para>
/// If either disagrees, this table is not published and the destination is left exactly
/// as it was. One table refused, not a restore abandoned.
/// </para>
/// </summary>
internal sealed class TablePublisher
{
    private readonly ImportOptions _options;
    private readonly IStagingTableFactory _staging;
    private readonly IPublisher _publisher;

    public TablePublisher(ImportOptions options, IStagingTableFactory staging, IPublisher publisher)
    {
        _options = options;
        _staging = staging;
        _publisher = publisher;
    }

    /// <summary>
    /// Stages, checks and publishes one table.
    /// </summary>
    /// <param name="archivePath">
    /// The archive. Opened here rather than shared, because a <c>ZipArchive</c> reading
    /// two entries at once seeks one underlying stream from two places, and tables are
    /// published in parallel.
    /// </param>
    /// <param name="entry">The manifest's entry for the table - what the guard compares against.</param>
    /// <param name="columns">The archived columns, in the archive's order.</param>
    /// <param name="fenced">
    /// True when a foreign key of this table is switched off for the length of the data
    /// phase, which is what rules the swap out. See <see cref="PublishStagedAsync"/>.
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<ImportTableResult> PublishAsync(
        string archivePath,
        ArchiveTableEntry entry,
        IReadOnlyList<ArchiveColumn> columns,
        bool fenced,
        CancellationToken cancellationToken)
    {
        var destination = SqlRender.Quote(entry.Schema, entry.Name);
        string? staging = null;

        await using var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var destinationColumns = await DestinationCatalog
            .ColumnsAsync(connection, entry.Schema, entry.Name, _options.CommandTimeoutSeconds, cancellationToken)
            .ConfigureAwait(false);

        DestinationShape.Check(entry.Identifier, columns, destinationColumns);

        // Named once. Two calls to the generator would produce two names, and the guard
        // would read a table nobody had loaded.
        var stagingName = StagingName(entry.Name);

        try
        {
            staging = await _staging
                .CreateAsync(_options.ConnectionString, destination, stagingName, cancellationToken)
                .ConfigureAwait(false);

            if(await StageAsync(connection, archivePath, entry, columns, stagingName, staging, cancellationToken)
                   .ConfigureAwait(false) is { } refused)
            {
                return refused;
            }

            var publication = await PublishStagedAsync(
                connection, entry, columns, destination, staging, destinationColumns, fenced, cancellationToken)
                .ConfigureAwait(false);

            await ReseedAsync(connection, entry, destination, destinationColumns, cancellationToken).ConfigureAwait(false);

            return new ImportTableResult(entry.Schema, entry.Name, ImportTableOutcome.Published, entry.RowCount, publication);
        }
        finally
        {
            if(staging is not null)
                await DropStagingAsync(connection, staging).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Fills a staging table from the archive and runs both halves of the exact guard over
    /// it. Null when the table may be published; otherwise the refusal, and the
    /// destination has not been touched.
    /// </summary>
    /// <param name="connection">An open connection to the destination.</param>
    /// <param name="archivePath">The archive.</param>
    /// <param name="entry">The manifest's entry for the table - what both checks compare against.</param>
    /// <param name="columns">The archived columns, in the archive's order.</param>
    /// <param name="stagingName">The staging table's name alone, in the destination table's schema.</param>
    /// <param name="staging">The staging table, quoted, as the bulk copy names it.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal async Task<ImportTableResult?> StageAsync(
        SqlConnection connection,
        string archivePath,
        ArchiveTableEntry entry,
        IReadOnlyList<ArchiveColumn> columns,
        string stagingName,
        string staging,
        CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync(
            connection, archivePath, entry, columns, staging, cancellationToken).ConfigureAwait(false);

        // First check: the archive read back as the manifest declares it. Its
        // difference message is the one Verify prints, so a refusal here and a verify
        // of the same file say the same words about the same fault.
        if(loaded.Difference is { } difference)
            return Refused(entry, loaded.Rows, difference);

        // Second check: what actually landed. The rows can be read perfectly and
        // written wrong - a batch lost, a batch sent twice - and only a read of the
        // staging table can see that.
        var digest = await LiveTableDigest.ComputeAsync(
            connection,
            entry.Schema,
            stagingName,
            columns,
            rowFilter: null,
            _options.CommandTimeoutSeconds,
            cancellationToken).ConfigureAwait(false);

        if(digest.Rows != entry.RowCount ||
           !string.Equals(digest.RowHash, entry.RowHash, StringComparison.OrdinalIgnoreCase))
        {
            return Refused(
                entry,
                digest.Rows,
                $"{entry.Identifier}: the archive was read correctly and what reached the staging table is " +
                $"not it - the manifest declares {Count(entry.RowCount)} hashing to {entry.RowHash}, and " +
                $"staging holds {Count(digest.Rows)} hashing to {digest.RowHash}. The destination has not " +
                "been touched.");
        }

        return null;
    }

    /// <summary>
    /// Takes the staging table away. Not through the factory's <c>DropAsync</c>, which
    /// deliberately leaves alone any table the caller named - and this one is named here,
    /// because the guard has to read the staging table by name a moment after it is
    /// created and parsing a quoted identifier back into its two parts is a thing to get
    /// wrong for no gain.
    /// </summary>
    internal async Task DropStagingAsync(SqlConnection connection, string staging)
    {
        try
        {
            await DestinationCatalog.ExecuteAsync(
                connection, $"DROP TABLE IF EXISTS {staging};", _options.CommandTimeoutSeconds, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch(SqlException)
        {
            // A leftover staging table is greppable by its name and costs an operator a
            // DROP. Failing a publication that worked over it would cost them the restore.
        }
    }

    /// <summary>Streams the archive's rows into the staging table.</summary>
    private async Task<(long Rows, string? Difference)> LoadAsync(
        SqlConnection connection,
        string archivePath,
        ArchiveTableEntry entry,
        IReadOnlyList<ArchiveColumn> columns,
        string staging,
        CancellationToken cancellationToken)
    {
        using var archive = await ArchiveReader.OpenAsync(archivePath, cancellationToken).ConfigureAwait(false);

        var table = archive.Manifest.Table(entry.Schema, entry.Name)
            ?? throw new ImportException($"{entry.Identifier} is no longer in the archive's manifest.");

        var rows = archive.OpenTable(table, columns);

        await using(rows.ConfigureAwait(false))
        {
            using var reader = new ArchiveDataReader(rows, columns);

            // KeepNulls is not optional. Staging is created from the destination's shape
            // and carries its default constraints, and without this a null in a column
            // that has a default arrives as the default - a value that was never in the
            // source, in a table whose hash is about to be compared with one that was.
            // KeepIdentity for the obvious reason: the archived key is the key.
            using var bulk = new SqlBulkCopy(
                connection,
                SqlBulkCopyOptions.KeepNulls | SqlBulkCopyOptions.KeepIdentity | SqlBulkCopyOptions.TableLock,
                externalTransaction: null)
            {
                DestinationTableName = staging,
                BatchSize = Math.Max(1, _options.BatchSize),
                BulkCopyTimeout = _options.CommandTimeoutSeconds,
                EnableStreaming = true
            };

            // By name and never by position. Staging has the destination's columns in the
            // destination's order, and the archive's are in the archive's; they agree
            // today and the day they do not, a positional mapping loads every value into
            // the wrong column and every one of them fits.
            foreach(var column in columns)
                bulk.ColumnMappings.Add(column.Name, column.Name);

            await bulk.WriteToServerAsync(reader, cancellationToken).ConfigureAwait(false);

            return (rows.Rows, rows.Difference());
        }
    }

    /// <summary>Moves the staged rows into the destination, and says how.</summary>
    private async Task<string> PublishStagedAsync(
        SqlConnection connection,
        ArchiveTableEntry entry,
        IReadOnlyList<ArchiveColumn> columns,
        string destination,
        string staging,
        IReadOnlyList<DestinationColumn> destinationColumns,
        bool fenced,
        CancellationToken cancellationToken)
    {
        // A table with a SYSTEM_TIME period does not get here. Where the destination
        // already has one - a migration, or --data-only - the importer hands the table and
        // its history to TemporalPublisher, which is the only thing that can write the
        // rows' own periods. And on a fresh restore the period does not exist yet while
        // the rows load: 040 created the table without it and 090 adds it afterwards, so
        // the table is an ordinary one and the switch below takes it. Checked against SQL
        // Server 2025. The 13577 this used to route around - "target table has SYSTEM_TIME
        // PERIOD while source table does not have it", which SyncJob's capability check
        // cannot see because it reads temporal_type and that is 0 with versioning off -
        // needs a period on the destination, and at load time there is none.
        //
        // What is left under this test is a GENERATED ALWAYS column of any other kind -
        // a ledger table's - which the archive does not carry and no switch can take.
        if(destinationColumns.Any(c => c.IsGeneratedAlways))
        {
            await InsertAsync(connection, columns, destination, staging, destinationColumns, cancellationToken)
                .ConfigureAwait(false);

            return "insert (the destination has GENERATED ALWAYS columns, which no switch can take)";
        }

        // A table whose foreign keys are switched off cannot be swapped either, and this
        // one took two failures against a live server to find. SwapPublisher brings the
        // staged table up to the destination's shape first, and the shape it copies comes
        // from a snapshot: SwapAlignment re-creates every foreign key of the destination
        // on staging, enabled and WITH CHECK, whatever state the destination's own copy
        // is in. So staging is validated against a parent table that is in the middle of
        // being replaced (error 547 on the ALTER), and if it survives that, the switch
        // itself refuses because a constraint disabled on one side and enabled on the
        // other is a shape mismatch (4917: "the source table constraint must be enabled").
        // Publishing by insert keeps the destination's object identity just as well - it
        // is the metadata-only exchange that is lost, not the object - so the cost here
        // is speed on a migration, and the alternative was dropping the operator's
        // foreign keys and hoping to put them back.
        if(fenced)
        {
            await InsertAsync(connection, columns, destination, staging, destinationColumns, cancellationToken)
                .ConfigureAwait(false);

            return "insert (a foreign key of this table is switched off, and a switch needs both sides to agree)";
        }

        var step = new SyncStep
        {
            Id = $"{entry.Schema}.{entry.Name}",
            Name = entry.Identifier,
            DestinationTable = destination,
            CommandTimeoutSeconds = _options.CommandTimeoutSeconds,
            Publication = new PublicationPlan
            {
                Mode = PublicationMode.Replace,
                StagingTable = staging,
                KeepIdentity = true,

                // The guard SyncJob evaluates is the heuristic one, and this restore has
                // already run the exact one twice. Zero and null are what say "asked and
                // answered" rather than "not checked".
                Guard = new PublicationGuard { MinimumRows = 0, MinimumFractionOfDestination = null }
            }
        };

        await _publisher.PublishAsync(_options.ConnectionString, staging, step, cancellationToken).ConfigureAwait(false);

        return "swap";
    }

    /// <summary>
    /// Puts the destination's identity counter where the restored rows leave it, so the
    /// next row anything else inserts is the next number and not a collision.
    /// </summary>
    /// <remarks>
    /// <b>Checked against SQL Server 2025, and the reason this is here rather than left to
    /// the publisher.</b> <c>DBCC CHECKIDENT(t, RESEED, n)</c> means two different things:
    /// on a table that has had a row inserted into it the next value is <c>n + 1</c>, and
    /// on one that has not it is <c>n</c> itself. A restore's destination is always the
    /// second kind - it was created by phase 040 and filled by an <c>ALTER TABLE ...
    /// SWITCH</c>, which is not an insert - so <c>SwapPublisher</c>'s reseed to the highest
    /// staged value leaves the counter one short and the first row somebody inserts
    /// afterwards collides with the last row restored. SyncJob never sees it because its
    /// destinations are tables that have been inserted into for years.
    /// <para>
    /// The bare form has no such ambiguity: it sets the current value to the maximum in
    /// the column, and the next row is the one after it. Running it after the publisher's
    /// reseed is idempotent and corrects both cases.
    /// </para>
    /// </remarks>
    internal async Task ReseedAsync(
        SqlConnection connection,
        ArchiveTableEntry entry,
        string destination,
        IReadOnlyList<DestinationColumn> destinationColumns,
        CancellationToken cancellationToken)
    {
        // Nothing to correct on an empty table, and DBCC would have no maximum to read.
        if(entry.RowCount == 0 || !destinationColumns.Any(c => c.IsIdentity))
            return;

        await DestinationCatalog.ExecuteAsync(
            connection,
            $"DBCC CHECKIDENT('{destination.Replace("'", "''", StringComparison.Ordinal)}', RESEED) WITH NO_INFOMSGS;",
            _options.CommandTimeoutSeconds,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The publication for a destination no switch can reach: empty it and fill it from
    /// staging, in one transaction, with an explicit column list.
    /// </summary>
    /// <remarks>
    /// A near-twin of the fallback inside <c>SwapPublisher</c>, and here rather than
    /// there because that one is chosen by a capability check that cannot see this case -
    /// see the caller. The column list is the part that matters: a <c>SELECT *</c> here
    /// would load the rows across shifted the day the two tables differ by a column, and
    /// silently, for as long as the types happened to line up.
    /// </remarks>
    private async Task InsertAsync(
        SqlConnection connection,
        IReadOnlyList<ArchiveColumn> columns,
        string destination,
        string staging,
        IReadOnlyList<DestinationColumn> destinationColumns,
        CancellationToken cancellationToken)
    {
        var identity = destinationColumns
            .Where(c => c.IsIdentity)
            .Select(c => c.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var list = string.Join(", ", columns.Select(c => SqlRender.Quote(c.Name)));
        var keepIdentity = columns.Any(c => identity.Contains(c.Name));

        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        try
        {
            // DELETE and not TRUNCATE: a table another foreign key points at refuses a
            // truncate even with that key switched off (4712), and this path is the one
            // a fenced table takes.
            await ExecuteAsync(connection, transaction, $"DELETE FROM {destination};", cancellationToken).ConfigureAwait(false);

            if(keepIdentity)
                await ExecuteAsync(connection, transaction, $"SET IDENTITY_INSERT {destination} ON;", cancellationToken).ConfigureAwait(false);

            await ExecuteAsync(
                connection,
                transaction,
                $"INSERT INTO {destination} ({list}) SELECT {list} FROM {staging};",
                cancellationToken).ConfigureAwait(false);

            if(keepIdentity)
                await ExecuteAsync(connection, transaction, $"SET IDENTITY_INSERT {destination} OFF;", cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await RollbackQuietlyAsync(transaction).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Rolls back, and never lets the rollback's own failure take the place of the error
    /// that made it necessary.
    /// </summary>
    /// <remarks>
    /// <b>Not the case it was first written for.</b> Some errors end the transaction on the
    /// server's side before the client hears of them - a <c>SYSTEM_VERSIONING = ON</c>
    /// refused with 13573 leaves <c>@@TRANCOUNT</c> at 0, and so does a trigger's own
    /// <c>ROLLBACK</c> - and this was written on the belief that the driver then refuses a
    /// second rollback. It does not: measured on SQL Server 2025 with SqlClient 6.1, by two
    /// mutations that swapped this for a plain rollback and went unnoticed, the driver
    /// accepts it quietly. What is left is a connection that broke half way, where the
    /// server rolls the transaction back when it notices and a rollback here would only
    /// throw a second error over the first. That path has no test.
    /// </remarks>
    internal static async Task RollbackQuietlyAsync(SqlTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch(Exception ex) when(ex is InvalidOperationException or SqlException)
        {
            // A connection that broke half way - the driver says so with either type - is
            // rolled back by the server when it notices; the error that broke it is the
            // one worth reporting.
        }
    }

    private async Task ExecuteAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(sql, connection, transaction)
        {
            CommandTimeout = _options.CommandTimeoutSeconds
        };

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static ImportTableResult Refused(ArchiveTableEntry entry, long rows, string reason) =>
        new(entry.Schema, entry.Name, ImportTableOutcome.Refused, rows, Publication: null, reason);

    /// <summary>
    /// The staging table's name: the destination's, an unmistakable middle, and eight hex
    /// digits so two runs of the same restore cannot collide.
    /// </summary>
    internal static string StagingName(string table)
    {
        const int suffixLength = 20; // "_sqlarchive_" plus eight hex digits
        var stem = table.Length > 128 - suffixLength ? table[..(128 - suffixLength)] : table;

        return $"{stem}_sqlarchive_{Guid.NewGuid():N}"[..(stem.Length + suffixLength)];
    }

    private static string Count(long rows) => rows.ToString("N0", CultureInfo.InvariantCulture);
}
