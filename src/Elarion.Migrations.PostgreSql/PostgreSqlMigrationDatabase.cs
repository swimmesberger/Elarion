using Elarion.Migrations;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Elarion.Migrations.PostgreSql;

/// <summary>
/// The PostgreSQL <see cref="IMigrationDatabase"/> (ADR-0057): opens one dedicated connection per
/// migration operation and, for exclusive sessions, acquires a <em>session-level</em>
/// <c>pg_advisory_lock</c> — session scope means a crashed runner releases the lock with its connection,
/// and a transaction-scoped lock would hang <c>CREATE INDEX CONCURRENTLY</c> scripts.
/// </summary>
internal sealed class PostgreSqlMigrationDatabase : IMigrationDatabase {
    private readonly string? _connectionString;
    private readonly NpgsqlDataSource? _dataSource;
    private readonly PostgreSqlMigrationOptions _options;
    private readonly ILogger _logger;

    public PostgreSqlMigrationDatabase(string connectionString, PostgreSqlMigrationOptions options, ILogger? logger = null)
        : this(options, logger) {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        // A genuinely dedicated physical connection: closing it always releases the session advisory
        // lock server-side, so no error path can park a pooled connector that still holds the lock.
        _connectionString = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString;
    }

    public PostgreSqlMigrationDatabase(NpgsqlDataSource dataSource, PostgreSqlMigrationOptions options, ILogger? logger = null)
        : this(options, logger) {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    private PostgreSqlMigrationDatabase(PostgreSqlMigrationOptions options, ILogger? logger) {
        ArgumentNullException.ThrowIfNull(options);
        MigrationTableName.Validate(options.HistoryTableName);
        _options = options;
        _logger = logger ?? NullLogger.Instance;
    }

    private int CommandTimeoutSeconds => ToSeconds(_options.CommandTimeout);

    private int LockTimeoutSeconds => ToSeconds(_options.LockTimeout);

    public async Task<IMigrationSession> ConnectAsync(bool exclusive, CancellationToken cancellationToken) {
        var connection = await OpenConnectionAsync(cancellationToken);
        try {
            if (exclusive) {
                await AcquireLockAsync(connection, cancellationToken);
            }
        }
        catch {
            await connection.DisposeAsync();
            throw;
        }

        return new PostgreSqlMigrationSession(
            connection,
            _options.HistoryTableName,
            CommandTimeoutSeconds,
            holdsLock: exclusive,
            _options.AdvisoryLockKey,
            LockTimeoutSeconds,
            _logger);
    }

    private async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken) {
        if (_dataSource is not null) {
            return await _dataSource.OpenConnectionAsync(cancellationToken);
        }

        var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private async Task AcquireLockAsync(NpgsqlConnection connection, CancellationToken cancellationToken) {
        // Session-level, never transaction-level: the lock must span non-transactional scripts, and it
        // dies with the connection — no lock row to clean up after a crash.
        await using var command = new NpgsqlCommand("SELECT pg_advisory_lock($1)", connection) {
            CommandTimeout = LockTimeoutSeconds,
            Parameters = { new NpgsqlParameter<long> { TypedValue = _options.AdvisoryLockKey } },
        };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Npgsql command timeouts are integer seconds with 0 as "no timeout"; non-positive spans (e.g. <see cref="Timeout.InfiniteTimeSpan"/>) mean no timeout.</summary>
    private static int ToSeconds(TimeSpan? timeout) =>
        timeout is null || timeout.Value <= TimeSpan.Zero ? 0 : Math.Max(1, (int)timeout.Value.TotalSeconds);
}
