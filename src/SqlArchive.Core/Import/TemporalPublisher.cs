using Microsoft.Data.SqlClient;
using SqlArchive.Core.Format;
using SqlSchemaDiff.Services;

namespace SqlArchive.Core.Import;

/// <summary>
/// A table with a <c>SYSTEM_TIME</c> period and its history table, from the archive into a
/// destination that already has them - a migration, or <c>--data-only</c> - published as
/// one timeline in one transaction.
/// <para>
/// <b>Why neither can go the ordinary way.</b> While a period exists, SQL Server refuses
/// an <c>INSERT</c> that names its columns, versioning on or off (13536), so the rows'
/// own <c>ValidFrom</c> and <c>ValidTo</c> cannot be written into the table as it
/// stands. And while versioning is on, the history table refuses every write at all,
/// to the point of refusing to compile a batch that deletes from it (13560). A fresh
/// restore never meets either: its tables are created without the period and it is only
/// added once the rows are in. A destination that is already temporal has to be taken
/// back to that state and brought forward again, and that is what this does.
/// </para>
/// <para>
/// <b>The sequence, measured against SQL Server 2025</b>, one statement per batch in one
/// transaction on one connection: versioning off; the period dropped, which leaves its two
/// columns plain <c>datetime2</c> and clears <c>HIDDEN</c>; each table emptied and filled
/// from its staging table with an explicit column list, periods included; the period
/// added back and <c>HIDDEN</c> restored where it was; versioning on, naming the same
/// history table, the same retention, and the consistency check. The destination is put
/// back exactly as it was found, except for its rows.
/// </para>
/// <para>
/// <b>And a failure anywhere leaves it exactly as it was</b>, which is the promise every
/// table gets. Nothing reaches the destination before both staging tables have passed the
/// exact guard; everything after that is one transaction; and the refusals SQL Server can
/// still make at the end - a period that starts in the future (13542), a history row that
/// ends in it (13543), a history whose periods overlap (13573) - roll the whole of it back,
/// period, versioning, <c>HIDDEN</c> and rows. Measured, not assumed: 13573 ends the
/// transaction on the server's side, and the table, both sets of rows and every flag come
/// back as they were.
/// </para>
/// </summary>
internal sealed class TemporalPublisher
{
    private readonly ImportOptions _options;
    private readonly TablePublisher _tables;

    public TemporalPublisher(ImportOptions options, TablePublisher tables)
    {
        _options = options;
        _tables = tables;
    }

    /// <summary>
    /// Stages, checks and publishes the table and its history together. Either member may
    /// be absent - left out of the publication by <c>--table</c> or <c>--exclude</c> - and
    /// the other is still published the same way, because writing either one needs the
    /// period or the versioning off.
    /// </summary>
    /// <param name="archivePath">The archive.</param>
    /// <param name="table">The destination's temporal table as its catalog has it now.</param>
    /// <param name="current">The table itself, when its rows are being published.</param>
    /// <param name="history">Its history table, when its rows are being published.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>One result per member that was asked for.</returns>
    public async Task<IReadOnlyList<ImportTableResult>> PublishAsync(
        string archivePath,
        DestinationTemporalTable table,
        TemporalMember? current,
        TemporalMember? history,
        CancellationToken cancellationToken)
    {
        var members = new List<(TemporalMember Member, bool IsCurrent)>();

        if(current is not null)
            members.Add((current, true));

        if(history is not null)
            members.Add((history, false));

        await using var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var staged = new List<Staged>();
        var refusals = new List<ImportTableResult>();
        var created = new List<string>();

        try
        {
            foreach(var (member, isCurrent) in members)
            {
                var entry = member.Entry;

                var destinationColumns = await DestinationCatalog
                    .ColumnsAsync(connection, entry.Schema, entry.Name, _options.CommandTimeoutSeconds, cancellationToken)
                    .ConfigureAwait(false);

                DestinationShape.Check(entry.Identifier, member.Columns, destinationColumns);

                var stagingName = TablePublisher.StagingName(entry.Name);

                var staging = await CreateStagingAsync(connection, entry.Schema, entry.Name, stagingName, cancellationToken)
                    .ConfigureAwait(false);

                created.Add(staging);

                var refused = await _tables
                    .StageAsync(connection, archivePath, entry, member.Columns, stagingName, staging, cancellationToken)
                    .ConfigureAwait(false);

                if(refused is not null)
                    refusals.Add(refused);
                else
                    staged.Add(new Staged(member, staging, destinationColumns, isCurrent));
            }

            // The two are one timeline. Publishing the history of a table whose own rows
            // were refused - or the other way round - would leave the destination with a
            // timeline that is neither the archive's nor the one it had, and the one
            // promise a refusal makes is that the destination has not been touched.
            if(refusals.Count > 0)
            {
                var refused = string.Join(" and ", refusals.Select(r => r.Identifier));

                foreach(var partner in staged)
                {
                    refusals.Add(new ImportTableResult(
                        partner.Member.Entry.Schema,
                        partner.Member.Entry.Name,
                        ImportTableOutcome.Refused,
                        0,
                        Publication: null,
                        Reason:
                            $"{partner.Member.Entry.Identifier} passed the guard and was not published: {refused} " +
                            "was refused, and a system-versioned table and its history are one timeline. Publishing " +
                            "one without the other would leave a history that belongs to neither. The destination " +
                            "has not been touched."));
                }

                return refusals;
            }

            await ReplaceAsync(connection, table, staged, cancellationToken).ConfigureAwait(false);

            var results = new List<ImportTableResult>(staged.Count);

            foreach(var published in staged)
            {
                var entry = published.Member.Entry;

                await _tables.ReseedAsync(
                    connection, entry, SqlRender.Quote(entry.Schema, entry.Name), published.DestinationColumns, cancellationToken)
                    .ConfigureAwait(false);

                results.Add(new ImportTableResult(
                    entry.Schema,
                    entry.Name,
                    ImportTableOutcome.Published,
                    entry.RowCount,
                    published.IsCurrent
                        ? "insert, with the period taken off and put back in the same transaction (SQL Server refuses a period's own values while it exists)"
                        : $"insert, as the history of {table.Quoted}, with versioning off for the length of the same transaction"));
            }

            return results;
        }
        finally
        {
            foreach(var staging in created)
                await _tables.DropStagingAsync(connection, staging).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The one transaction: takes the period and the versioning off, replaces the rows of
    /// every staged member, and puts both back as they were.
    /// </summary>
    private async Task ReplaceAsync(
        SqlConnection connection,
        DestinationTemporalTable table,
        IReadOnlyList<Staged> staged,
        CancellationToken cancellationToken)
    {
        // The period only has to come off when the table's own rows are being written.
        // A history table written alone needs versioning off and nothing else.
        var writesCurrent = staged.Any(s => s.IsCurrent);

        var statements = new List<string>();

        if(table.IsVersioned)
            statements.Add($"ALTER TABLE {table.Quoted} SET (SYSTEM_VERSIONING = OFF);");

        if(writesCurrent)
            statements.Add($"ALTER TABLE {table.Quoted} DROP PERIOD FOR SYSTEM_TIME;");

        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        try
        {
            // One statement per command, and that is not style. A batch is compiled
            // whole before any of it runs, so a DELETE from the history table in the same
            // batch as the SET (SYSTEM_VERSIONING = OFF) is refused at compile time
            // (13560) - measured on 2025 - because at that moment the table is still a
            // history table.
            foreach(var statement in statements)
                await ExecuteAsync(connection, transaction, statement, cancellationToken).ConfigureAwait(false);

            foreach(var member in staged)
                await ReplaceRowsAsync(connection, transaction, member, cancellationToken).ConfigureAwait(false);

            if(writesCurrent)
            {
                await ExecuteAsync(
                    connection,
                    transaction,
                    $"ALTER TABLE {table.Quoted} ADD PERIOD FOR SYSTEM_TIME " +
                    $"({SqlRender.Quote(table.PeriodStart)}, {SqlRender.Quote(table.PeriodEnd)});",
                    cancellationToken).ConfigureAwait(false);

                // HIDDEN goes on after the period and never before: it can only be set on
                // a column that is already GENERATED ALWAYS (13735).
                if(table.PeriodStartHidden)
                    await ExecuteAsync(connection, transaction, Hide(table, table.PeriodStart), cancellationToken).ConfigureAwait(false);

                if(table.PeriodEndHidden)
                    await ExecuteAsync(connection, transaction, Hide(table, table.PeriodEnd), cancellationToken).ConfigureAwait(false);
            }

            if(table.IsVersioned)
                await ExecuteAsync(connection, transaction, table.VersioningOn, cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch(SqlException refused) when(TemporalRefusal.Explain(refused.Number) is { } why)
        {
            await TablePublisher.RollbackQuietlyAsync(transaction).ConfigureAwait(false);

            throw new ImportException(
                $"{table.Quoted}: SQL Server refused the restored timeline - {refused.Message.TrimEnd()} {why} " +
                "The destination is exactly as it was: everything from switching versioning off to switching it " +
                "back on was one transaction, and it was rolled back.",
                refused);
        }
        catch
        {
            await TablePublisher.RollbackQuietlyAsync(transaction).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>One member's rows out and the staged ones in, with the columns named.</summary>
    private async Task ReplaceRowsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        Staged member,
        CancellationToken cancellationToken)
    {
        var entry = member.Member.Entry;
        var destination = SqlRender.Quote(entry.Schema, entry.Name);
        var list = string.Join(", ", member.Member.Columns.Select(c => SqlRender.Quote(c.Name)));

        var identity = member.DestinationColumns
            .Where(c => c.IsIdentity)
            .Select(c => c.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var keepIdentity = member.Member.Columns.Any(c => identity.Contains(c.Name));

        // DELETE and not TRUNCATE: a table another foreign key points at refuses a
        // truncate even with the key switched off (4712).
        await ExecuteAsync(connection, transaction, $"DELETE FROM {destination};", cancellationToken).ConfigureAwait(false);

        if(keepIdentity)
            await ExecuteAsync(connection, transaction, $"SET IDENTITY_INSERT {destination} ON;", cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(
            connection,
            transaction,
            $"INSERT INTO {destination} ({list}) SELECT {list} FROM {member.Staging};",
            cancellationToken).ConfigureAwait(false);

        if(keepIdentity)
            await ExecuteAsync(connection, transaction, $"SET IDENTITY_INSERT {destination} OFF;", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A staging table shaped like the destination table with its period taken off: the
    /// two period columns plain <c>datetime2 NOT NULL</c>, so the archive's values for them
    /// can be loaded at all.
    /// </summary>
    /// <remarks>
    /// Not SyncJob's <c>StagingTableFactory</c>, which is right for what SyncJob does and
    /// wrong here: it drops a period's columns from staging altogether
    /// (<c>StagingTableFactory.AsStaging</c>), because a sync into a temporal table lets the
    /// destination stamp its own. A restore carries them. The shape still comes from the
    /// destination's catalog through SQLDiff's own extractor and renderer - one table's
    /// worth of reads through <c>ExtractTableAsync</c>, which also returns a history table
    /// by name, where the factory's whole-database read skips it.
    /// </remarks>
    private async Task<string> CreateStagingAsync(
        SqlConnection connection,
        string schema,
        string name,
        string stagingName,
        CancellationToken cancellationToken)
    {
        var model = await new SqlServerSchemaExtractor()
            .ExtractTableAsync(connection, null, schema, name, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new ImportShapeException(
                $"[{schema}].[{name}] disappeared from the destination between reading its columns and staging its rows.");

        var staging = model.Clone();
        staging.Schema = schema;
        staging.Name = stagingName;

        // Clone is shallow, so every list is replaced rather than cleared - clearing would
        // reach into the destination's own model.
        staging.KeyConstraints = [];
        staging.ForeignKeys = [];
        staging.CheckConstraints = [];
        staging.Indexes = [];
        staging.TemporalType = null;
        staging.HistoryTableSchema = null;
        staging.HistoryTableName = null;
        staging.PeriodStartColumn = null;
        staging.PeriodEndColumn = null;
        staging.IsMemoryOptimized = false;
        staging.Durability = null;
        staging.DataCompression = null;

        staging.Columns = model.Columns
            .Select(column =>
            {
                var copy = column.Clone();

                if(ArchiveColumns.IsPeriodColumn(column))
                {
                    copy.GeneratedAlwaysType = 0;
                    copy.IsHidden = false;
                    copy.IsNullable = false;
                }

                // A default is an object with a name, and the destination's name would
                // collide. None is needed: the bulk copy keeps nulls, so no default can
                // ever fire on a staged row.
                copy.DefaultName = null;
                copy.DefaultDefinition = null;
                return copy;
            })
            .ToList();

        var quoted = SqlRender.Quote(schema, stagingName);

        await DestinationCatalog.ExecuteAsync(
            connection, SqlRender.BuildTableCreateOnly(staging), _options.CommandTimeoutSeconds, cancellationToken)
            .ConfigureAwait(false);

        return quoted;
    }

    private static string Hide(DestinationTemporalTable table, string column) =>
        $"ALTER TABLE {table.Quoted} ALTER COLUMN {SqlRender.Quote(column)} ADD HIDDEN;";

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

    private sealed record Staged(
        TemporalMember Member,
        string Staging,
        IReadOnlyList<DestinationColumn> DestinationColumns,
        bool IsCurrent);
}

/// <summary>One table of a temporal pair being published: the manifest's entry and the columns to decode it with.</summary>
internal sealed record TemporalMember(ArchiveTableEntry Entry, IReadOnlyList<ArchiveColumn> Columns);

/// <summary>
/// The refusals SQL Server makes when a timeline is handed back to it, each with what it
/// means for a restore - so the operator reads the cause and not only the rule.
/// </summary>
/// <remarks>
/// Every one of them was produced against SQL Server 2025 while this was written, and the
/// wording of the server's own message is kept beside the explanation rather than replaced
/// by it.
/// </remarks>
public static class TemporalRefusal
{
    /// <summary>An explanation for a temporal refusal, or null for any other error.</summary>
    public static string? Explain(int errorNumber) => errorNumber switch
    {
        13542 =>
            "A current row's period starts after this server's own clock. The archive carries the source's instants " +
            "and SQL Server compares them with the time here, so this server's clock is behind the source's by at " +
            "least the gap. Nothing in the archive is wrong: wait until this server's clock has passed the latest " +
            "period start in the archive, or put the clock right, and restore again.",

        13543 =>
            "A history row's period ends after this server's own clock. The archive carries the source's instants " +
            "and SQL Server compares them with the time here, so this server's clock is behind the source's by at " +
            "least the gap. Nothing in the archive is wrong: wait until this server's clock has passed it, or put " +
            "the clock right, and restore again.",

        13573 =>
            "The history's periods overlap for at least one key, and SQL Server will not adopt a history that " +
            "would answer FOR SYSTEM_TIME AS OF with two versions of one row. Either the archive's history does not " +
            "belong with these current rows, or the history kept is a mixture of two timelines.",

        13575 =>
            "A current row's period does not end at the largest value its datetime2 scale can hold, which every " +
            "current row of a table with a period does. The rows are not those of a table that had this period, " +
            "or the destination's column has a different scale from the archive's.",

        13525 =>
            "A column of the history table is not of the type of its counterpart in the current table, which " +
            "SQL Server requires of a history it adopts.",

        // Measured on 2025: the very first statement - SYSTEM_VERSIONING = OFF - is
        // refused inside a transaction when the table is memory-optimized.
        12331 =>
            "The table is memory-optimized, and SQL Server will not take its versioning or its period off inside a " +
            "transaction - which is the only way its rows can be replaced here and still leave it as it was if " +
            "anything failed. Its timeline cannot be restored over an existing copy of it; it can into a database " +
            "where the table does not exist yet, once that database has a MEMORY_OPTIMIZED_DATA filegroup.",

        _ => null
    };

    /// <summary>The explanation for the first temporal refusal in a message list, if any.</summary>
    public static string? Explain(IEnumerable<int> errorNumbers)
    {
        ArgumentNullException.ThrowIfNull(errorNumbers);

        foreach(var number in errorNumbers)
        {
            if(Explain(number) is { } why)
                return why;
        }

        return null;
    }
}
