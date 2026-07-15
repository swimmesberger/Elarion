using System.Collections.Concurrent;
using System.Globalization;
using Elarion.Migrations;
using Microsoft.Data.Sqlite;

namespace Elarion.Migrations.Sqlite;

/// <summary>
/// The SQLite <see cref="IMigrationDatabase"/> (ADR-0060): opens one dedicated, pooling-disabled
/// connection per operation. SQLite is single-node by design — one database file per node, never shared
/// across nodes — so the migration lock's correct scope is <em>this process</em>: an exclusive session
/// acquires a per-file in-process lock so concurrent runners serialize deterministically (the PostgreSQL
/// session advisory lock's closest SQLite analogue). Cross-process contention on one file (an unsupported
/// SQLite topology) is bounded by <c>busy_timeout</c> and the history table's <c>version</c> uniqueness,
/// not this lock. A whole-connection <c>locking_mode = EXCLUSIVE</c> is deliberately avoided: two
/// connections under it each retain a shared lock and deadlock trying to promote to exclusive.
/// </summary>
internal sealed class SqliteMigrationDatabase : IMigrationDatabase {
    // Per-file gate, keyed by the normalized data source. SQLite migrations run single-process, so a
    // process-wide semaphore is the right scope; two runners on one file serialize on it, deadlock-free.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(StringComparer.Ordinal);

    private readonly string _connectionString;
    private readonly string _lockKey;
    private readonly SqliteMigrationOptions _options;

    public SqliteMigrationDatabase(string connectionString, SqliteMigrationOptions options) {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(options);
        MigrationTableName.Validate(options.HistoryTableName);
        _options = options;
        // Pooling must be off: a pooled connection returned to the pool keeps the native handle alive, so
        // an exclusive lock or session state could outlive the session. Disposing an unpooled connection
        // closes the handle immediately.
        var builder = new SqliteConnectionStringBuilder(connectionString) { Pooling = false };
        _connectionString = builder.ConnectionString;
        _lockKey = NormalizeLockKey(builder.DataSource);
    }

    private int CommandTimeoutSeconds => LockTimeoutSeconds;

    private int LockTimeoutSeconds => _options.LockTimeout is not { } t || t <= TimeSpan.Zero ? 0 : Math.Max(1, (int)t.TotalSeconds);

    private long LockTimeoutMilliseconds =>
        _options.LockTimeout is not { } t || t <= TimeSpan.Zero ? int.MaxValue : (long)Math.Clamp(t.TotalMilliseconds, 1, int.MaxValue);

    private TimeSpan LockWait => _options.LockTimeout is { } t && t > TimeSpan.Zero ? t : Timeout.InfiniteTimeSpan;

    public async Task<IMigrationSession> ConnectAsync(bool exclusive, CancellationToken cancellationToken) {
        var gate = exclusive ? await AcquireGateAsync(cancellationToken) : null;
        SqliteConnection connection;
        try {
            connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await ExecutePragmaAsync(connection, $"PRAGMA busy_timeout = {LockTimeoutMilliseconds.ToString(CultureInfo.InvariantCulture)}", cancellationToken);
        }
        catch {
            gate?.Release();
            throw;
        }

        var history = new SqliteSchemaHistory(connection, _options.HistoryTableName, CommandTimeoutSeconds);
        return new SqliteMigrationSession(connection, history, CommandTimeoutSeconds, gate);
    }

    private async Task<SemaphoreSlim> AcquireGateAsync(CancellationToken cancellationToken) {
        var gate = Locks.GetOrAdd(_lockKey, static _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(LockWait, cancellationToken)) {
            throw new MigrationException(
                $"Timed out waiting {_options.LockTimeout} for the SQLite migration lock on '{_lockKey}'. "
                + "Another migration runner in this process holds it; increase LockTimeout or serialize startup.");
        }

        return gate;
    }

    private async Task ExecutePragmaAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken) {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = CommandTimeoutSeconds;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Normalizes the data source to a stable lock key: a full path for file databases, the raw value otherwise.</summary>
    private static string NormalizeLockKey(string dataSource) {
        if (string.IsNullOrEmpty(dataSource) || dataSource.Equals(":memory:", StringComparison.OrdinalIgnoreCase)) {
            return dataSource;
        }

        try {
            return Path.GetFullPath(dataSource);
        }
        catch (ArgumentException) {
            return dataSource;
        }
    }
}
