using System.Data;
using Microsoft.Data.SqlClient;
using SqlArchive.Core.Format;

namespace SqlArchive.Core.Export;

/// <summary>
/// Decides what "at the same time" means for one export, and then hands out the
/// connections that mean it.
/// <para>
/// Without <c>--consistent</c> there is nothing to decide: each table is read on its own
/// connection and the manifest says <c>per-table</c>. With it, the three steps of
/// <c>DESIGN.md</c> are tried in order, and <b>the third one refuses</b>. It does not
/// quietly become <c>per-table</c>: somebody who asked for a single point in time and
/// received an archive that is not one, without being told, is worse off than somebody
/// whose export failed.
/// </para>
/// </summary>
public sealed class ConsistencySession : IAsyncDisposable
{
    /// <summary>
    /// SQL Server's <c>EngineEdition</c> for Azure SQL Database, which has no database
    /// snapshots at all. Worth knowing before trying, so the refusal can say why instead
    /// of quoting a syntax error.
    /// </summary>
    private const int AzureSqlDatabaseEngineEdition = 5;

    /// <summary>"Snapshot isolation transaction failed accessing database ... because snapshot isolation is not allowed".</summary>
    private const int SnapshotIsolationNotAllowed = 3952;

    private readonly string _sourceConnectionString;
    private readonly int _commandTimeoutSeconds;
    private readonly List<string> _notices = [];

    private readonly SqlConnection? _pinned;
    private readonly SqlTransaction? _transaction;
    private readonly SemaphoreSlim? _gate;

    private bool _disposed;

    private ConsistencySession(
        string sourceConnectionString,
        string readConnectionString,
        ArchiveConsistency consistency,
        int commandTimeoutSeconds,
        string? snapshotDatabase = null,
        SqlConnection? pinned = null,
        SqlTransaction? transaction = null)
    {
        _sourceConnectionString = sourceConnectionString;
        _commandTimeoutSeconds = commandTimeoutSeconds;
        _pinned = pinned;
        _transaction = transaction;
        _gate = pinned is null ? null : new SemaphoreSlim(1, 1);

        ReadConnectionString = readConnectionString;
        Consistency = consistency;
        SnapshotDatabase = snapshotDatabase;
    }

    /// <summary>What was actually achieved, which is what the manifest records.</summary>
    public ArchiveConsistency Consistency { get; }

    /// <summary>
    /// The connection string the rows come through. Under <see cref="ArchiveConsistency.Snapshot"/>
    /// it points at the database snapshot rather than at the source, which is what makes
    /// every table the same instant while several connections read at once.
    /// </summary>
    public string ReadConnectionString { get; }

    /// <summary>The database snapshot's name while it exists, so a caller can say where the rows came from.</summary>
    public string? SnapshotDatabase { get; }

    /// <summary>
    /// One under <see cref="ArchiveConsistency.SnapshotIsolation"/>, because a snapshot
    /// transaction belongs to a connection and a second connection would be a second
    /// point in time. That is what DESIGN.md means by "loses the parallelism".
    /// </summary>
    public bool SupportsParallelReads => _pinned is null;

    /// <summary>Anything worth telling the operator afterwards - a snapshot that would not drop, most of all.</summary>
    public IReadOnlyList<string> Notices => _notices;

    /// <summary>
    /// Establishes the point in time, or settles for none.
    /// </summary>
    /// <param name="connectionString">The source database.</param>
    /// <param name="consistent">Whether a single point in time was asked for.</param>
    /// <param name="commandTimeoutSeconds">Zero for no limit.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <exception cref="ExportConsistencyException">
    /// When <paramref name="consistent"/> is set and neither mechanism is available. The
    /// message names both conditions and which of them failed.
    /// </exception>
    public static async Task<ConsistencySession> OpenAsync(
        string connectionString,
        bool consistent,
        int commandTimeoutSeconds = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(connectionString);

        if(!consistent)
        {
            return new ConsistencySession(
                connectionString, connectionString, ArchiveConsistency.PerTable, commandTimeoutSeconds);
        }

        var (snapshot, snapshotRefusal) =
            await TrySnapshotAsync(connectionString, commandTimeoutSeconds, cancellationToken).ConfigureAwait(false);

        if(snapshot is not null)
            return snapshot;

        var (isolation, isolationRefusal) =
            await TrySnapshotIsolationAsync(connectionString, commandTimeoutSeconds, cancellationToken).ConfigureAwait(false);

        if(isolation is not null)
        {
            isolation._notices.Add(
                "A single point in time came from SNAPSHOT isolation rather than from a database snapshot, so " +
                $"every table is read through one connection and none of them in parallel. {snapshotRefusal}");

            return isolation;
        }

        throw new ExportConsistencyException(snapshotRefusal, isolationRefusal);
    }

    /// <summary>
    /// A connection to read one table range through, and the transaction it has to run
    /// in when there is one. Disposing the scope releases whatever it took.
    /// </summary>
    public async Task<ExportReadScope> AcquireAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if(_pinned is not null && _gate is not null)
        {
            // Serialised rather than merely documented as serial: one connection can
            // carry one reader, and a caller that ignored SupportsParallelReads would
            // otherwise get an error from the driver instead of an answer.
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new ExportReadScope(_pinned, _transaction, _commandTimeoutSeconds, _gate, ownsConnection: false);
        }

        var connection = new SqlConnection(ReadConnectionString);

        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return new ExportReadScope(connection, transaction: null, _commandTimeoutSeconds, gate: null, ownsConnection: true);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if(_disposed)
            return;

        _disposed = true;

        if(_transaction is not null)
        {
            // Committed rather than rolled back: nothing was written, and a commit is
            // what releases the version store the snapshot has been holding.
            try
            {
                await _transaction.CommitAsync().ConfigureAwait(false);
            }
            catch(SqlException)
            {
                // A transaction the server already killed is not a reason to fail an
                // export whose rows are all safely on disk.
            }

            await _transaction.DisposeAsync().ConfigureAwait(false);
        }

        if(_pinned is not null)
            await _pinned.DisposeAsync().ConfigureAwait(false);

        _gate?.Dispose();

        if(SnapshotDatabase is not null)
            await DropSnapshotAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Step one: a database snapshot. It is the only mechanism that gives every table the
    /// same instant <i>and</i> keeps several connections reading at once, because the
    /// instant belongs to a database rather than to a transaction.
    /// </summary>
    private static async Task<(ConsistencySession? Session, string Refusal)> TrySnapshotAsync(
        string connectionString,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            var edition = Convert.ToInt32(
                await ScalarAsync(connection, null, "SELECT SERVERPROPERTY('EngineEdition');", commandTimeoutSeconds, cancellationToken)
                    .ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture);

            if(edition == AzureSqlDatabaseEngineEdition)
            {
                return (null,
                    "A database snapshot could not be created: Azure SQL Database has no CREATE DATABASE ... AS " +
                    "SNAPSHOT OF at all.");
            }

            var source = connection.Database;
            var name = SnapshotName(source);
            var files = await ReadDataFilesAsync(connection, commandTimeoutSeconds, cancellationToken).ConfigureAwait(false);

            if(files.Count == 0)
            {
                return (null,
                    "A database snapshot could not be created: the connection cannot see sys.database_files, so " +
                    "the ROWS files a snapshot has to name are unknown.");
            }

            // The sparse file goes beside the data file it shadows. Not a configured
            // directory: this one certainly exists and the service account certainly
            // writes to it, which is not true of anywhere else we could pick.
            var clauses = files.Select(f =>
                $"(NAME = {Quote(f.Name)}, FILENAME = '{Escape(f.Path)}.{Guid.NewGuid():N}.ss')");

            var sql = $"CREATE DATABASE {Quote(name)} ON {string.Join(", ", clauses)} AS SNAPSHOT OF {Quote(source)};";

            await ExecuteAsync(connection, null, sql, commandTimeoutSeconds, cancellationToken).ConfigureAwait(false);

            var read = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = name }.ConnectionString;

            return (
                new ConsistencySession(
                    connectionString, read, ArchiveConsistency.Snapshot, commandTimeoutSeconds, snapshotDatabase: name),
                string.Empty);
        }
        catch(SqlException exception)
        {
            // Whatever the server said, quoted. Which of the many reasons it can be -
            // no CREATE DATABASE permission, no room for the sparse file, an edition
            // that does not have snapshots - is the server's to explain, and repeating
            // its own words is more use than a category of ours.
            return (null, $"A database snapshot could not be created: {exception.Message.TrimEnd()}");
        }
    }

    /// <summary>
    /// Step two: one connection in a <c>SNAPSHOT</c> transaction.
    /// </summary>
    /// <remarks>
    /// Two things here are not optional and neither is obvious.
    /// <para>
    /// <c>BeginTransaction()</c> with no argument sets <c>READ COMMITTED</c>, so issuing
    /// <c>SET TRANSACTION ISOLATION LEVEL SNAPSHOT</c> as a statement and then beginning
    /// a transaction gives a session that <i>reports</i> snapshot isolation in the
    /// manifest and does not have it. Verified against SQL Server 2025: the session's
    /// <c>transaction_isolation_level</c> comes back 2, and a row inserted by somebody
    /// else mid-export is visible. The isolation level has to be passed to
    /// <c>BeginTransaction</c>.
    /// </para>
    /// <para>
    /// And the point in time is taken at the first statement that reads <b>user</b> data,
    /// not when the transaction begins and not when a system catalog is read - also
    /// verified, and also silent. So one user table is touched here, before the caller is
    /// told the session is open. Without that, every row written between opening the
    /// session and reading the first table would be inside the "snapshot".
    /// </para>
    /// </remarks>
    private static async Task<(ConsistencySession? Session, string Refusal)> TrySnapshotIsolationAsync(
        string connectionString,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(connectionString);
        SqlTransaction? transaction = null;

        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            var state = await ScalarAsync(
                connection, null,
                "SELECT snapshot_isolation_state FROM sys.databases WHERE database_id = DB_ID();",
                commandTimeoutSeconds, cancellationToken).ConfigureAwait(false);

            // 1 is ON. 0 is OFF and 2 and 3 are the two transitions, in which a snapshot
            // transaction is refused just as it is when the setting is off.
            if(state is null or DBNull || Convert.ToInt32(state, System.Globalization.CultureInfo.InvariantCulture) != 1)
            {
                await connection.DisposeAsync().ConfigureAwait(false);

                return (null,
                    $"SNAPSHOT isolation is not available on [{connection.Database}]: ALLOW_SNAPSHOT_ISOLATION is " +
                    "not ON. It is deliberately not turned on here - that is a permanent change to somebody's " +
                    "database, and an export has no business making one.");
            }

            transaction = (SqlTransaction)await connection
                .BeginTransactionAsync(IsolationLevel.Snapshot, cancellationToken).ConfigureAwait(false);

            var pin = await FirstUserTableAsync(connection, transaction, commandTimeoutSeconds, cancellationToken)
                .ConfigureAwait(false);

            if(pin is not null)
            {
                // TOP(1) so this costs one row on a table of any size, and reads data
                // rather than a catalog so that it really does fix the instant.
                await ScalarAsync(connection, transaction, $"SELECT TOP(1) 1 FROM {pin};", commandTimeoutSeconds, cancellationToken)
                    .ConfigureAwait(false);
            }

            return (
                new ConsistencySession(
                    connectionString, connectionString, ArchiveConsistency.SnapshotIsolation, commandTimeoutSeconds,
                    pinned: connection, transaction: transaction),
                string.Empty);
        }
        catch(SqlException exception)
        {
            if(transaction is not null)
                await transaction.DisposeAsync().ConfigureAwait(false);

            await connection.DisposeAsync().ConfigureAwait(false);

            var detail = exception.Number == SnapshotIsolationNotAllowed
                ? exception.Message.TrimEnd()
                : $"the server refused it: {exception.Message.TrimEnd()}";

            return (null, $"SNAPSHOT isolation could not be entered: {detail}");
        }
        catch
        {
            if(transaction is not null)
                await transaction.DisposeAsync().ConfigureAwait(false);

            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<List<(string Name, string Path)>> ReadDataFilesAsync(
        SqlConnection connection,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        var files = new List<(string, string)>();

        // type 0 is ROWS. A snapshot names the data files and never the log, which it
        // does not have one of.
        await using var command = new SqlCommand(
            "SELECT name, physical_name FROM sys.database_files WHERE type = 0 ORDER BY file_id;", connection)
        {
            CommandTimeout = commandTimeoutSeconds
        };

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while(await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            files.Add((reader.GetString(0), reader.GetString(1)));

        return files;
    }

    /// <summary>The lowest-numbered user table, quoted, or null when the database has none.</summary>
    private static async Task<string?> FirstUserTableAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        var name = await ScalarAsync(
            connection, transaction,
            """
            SELECT TOP(1) QUOTENAME(SCHEMA_NAME(t.schema_id)) + '.' + QUOTENAME(t.name)
            FROM sys.tables AS t
            WHERE t.is_ms_shipped = 0
            ORDER BY t.object_id;
            """,
            commandTimeoutSeconds, cancellationToken).ConfigureAwait(false);

        return name as string;
    }

    private async Task DropSnapshotAsync()
    {
        try
        {
            // Only the pool for the snapshot's own connection string, not every pool the
            // process has: an export is a library call and clearing the world would close
            // connections belonging to whoever called it.
            await using(var pooled = new SqlConnection(ReadConnectionString))
                SqlConnection.ClearPool(pooled);

            await using var connection = new SqlConnection(_sourceConnectionString);
            await connection.OpenAsync().ConfigureAwait(false);

            await ExecuteAsync(connection, null, $"DROP DATABASE {Quote(SnapshotDatabase!)};", _commandTimeoutSeconds, default)
                .ConfigureAwait(false);
        }
        catch(SqlException exception)
        {
            // Loud rather than fatal. The archive is written; what is left behind is a
            // sparse file growing against the source database, and the one thing that
            // helps is the name.
            _notices.Add(
                $"The database snapshot [{SnapshotDatabase}] could not be dropped and is still on the server, " +
                $"holding a sparse file beside the source's data files: {exception.Message.TrimEnd()}");
        }
    }

    private static async Task ExecuteAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        string sql,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(sql, connection, transaction) { CommandTimeout = commandTimeoutSeconds };
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<object?> ScalarAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        string sql,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(sql, connection, transaction) { CommandTimeout = commandTimeoutSeconds };
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A name for the snapshot that says what made it and cannot collide with another
    /// run. A database name is at most 128 characters, so a long source name is what
    /// gives way; the suffix is what makes an orphan findable.
    /// </summary>
    private static string SnapshotName(string source)
    {
        const string marker = "_sqlarchive_";

        var suffix = marker + Guid.NewGuid().ToString("N")[..8];
        var room = 128 - suffix.Length;

        return (source.Length <= room ? source : source[..room]) + suffix;
    }

    private static string Quote(string name) => $"[{name.Replace("]", "]]", StringComparison.Ordinal)}]";

    private static string Escape(string literal) => literal.Replace("'", "''", StringComparison.Ordinal);
}

/// <summary>
/// A connection to read one table range through. Disposing it lets the next range have
/// the connection, or closes it, depending on which the session handed out.
/// </summary>
public sealed class ExportReadScope : IAsyncDisposable
{
    private readonly SemaphoreSlim? _gate;
    private readonly bool _ownsConnection;
    private readonly int _commandTimeoutSeconds;

    private bool _disposed;

    internal ExportReadScope(
        SqlConnection connection,
        SqlTransaction? transaction,
        int commandTimeoutSeconds,
        SemaphoreSlim? gate,
        bool ownsConnection)
    {
        Connection = connection;
        Transaction = transaction;
        _commandTimeoutSeconds = commandTimeoutSeconds;
        _gate = gate;
        _ownsConnection = ownsConnection;
    }

    public SqlConnection Connection { get; }

    /// <summary>The snapshot transaction, when the session is running in one. Every command has to join it.</summary>
    public SqlTransaction? Transaction { get; }

    /// <summary>A command on this scope's connection, already enlisted in its transaction.</summary>
    public SqlCommand Command(string sql)
    {
        ArgumentException.ThrowIfNullOrEmpty(sql);
        return new SqlCommand(sql, Connection, Transaction) { CommandTimeout = _commandTimeoutSeconds };
    }

    public async ValueTask DisposeAsync()
    {
        if(_disposed)
            return;

        _disposed = true;

        if(_ownsConnection)
            await Connection.DisposeAsync().ConfigureAwait(false);

        _gate?.Release();
    }
}

/// <summary>
/// <c>--consistent</c> was asked for and neither way of getting a single point in time was
/// available.
/// <para>
/// This is a refusal and not a fallback, and that is the decision: an operator who asked
/// for one instant and silently received an archive stitched together over an hour has
/// something they will trust and should not.
/// </para>
/// </summary>
public sealed class ExportConsistencyException : ExportException
{
    public ExportConsistencyException(string snapshotRefusal, string isolationRefusal)
        : base(
            "--consistent was asked for and neither of the two ways of reading a database at a single point in " +
            $"time is available.{Environment.NewLine}" +
            $"  1. {snapshotRefusal}{Environment.NewLine}" +
            $"  2. {isolationRefusal}{Environment.NewLine}" +
            "Grant CREATE DATABASE so a database snapshot can be taken, or set ALLOW_SNAPSHOT_ISOLATION ON for " +
            "the database, or export without --consistent and accept that the tables are read one after another.")
    {
        SnapshotRefusal = snapshotRefusal;
        IsolationRefusal = isolationRefusal;
    }

    /// <summary>Why a database snapshot could not be created, in the server's own words.</summary>
    public string SnapshotRefusal { get; }

    /// <summary>Why a SNAPSHOT transaction could not be entered.</summary>
    public string IsolationRefusal { get; }
}
