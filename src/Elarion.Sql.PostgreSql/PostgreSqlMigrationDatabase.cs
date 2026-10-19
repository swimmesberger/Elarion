using Elarion.Migrations;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Elarion.Sql.PostgreSql;

/// <summary>
/// The PostgreSQL <see cref="IMigrationDatabase"/> (ADR-0057): opens one dedicated connection per
/// migration operation and, for exclusive sessions, acquires a <em>session-level</em>
/// <c>pg_advisory_lock</c> — session scope means a crashed runner releases the lock with its connection,
/// and a transaction-scoped lock would hang <c>CREATE INDEX CONCURRENTLY</c> scripts. The advisory-lock key is
/// a provider-registration argument (see <c>AddElarionPostgreSql</c>), not a migration option. When
/// the connection carries a <c>Search Path</c> the session also creates that schema if it is missing, so
/// prefix-free scripts have somewhere to land — the connection stays the single source of truth for
/// <em>which</em> schema, shared with the application's own queries.
/// </summary>
internal sealed class PostgreSqlMigrationDatabase : IMigrationDatabase {
    private readonly string? _connectionString;
    private readonly NpgsqlDataSource? _dataSource;
    private readonly MigrationOptions _options;
    private readonly string? _schema;
    private readonly long _advisoryLockKey;
    private readonly ILogger _logger;

    public PostgreSqlMigrationDatabase(string connectionString, MigrationOptions options,
        long advisoryLockKey, ILogger? logger = null)
        : this(options, advisoryLockKey, logger) {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _schema = ResolveSchema(new NpgsqlConnectionStringBuilder(connectionString).SearchPath);
        _connectionString = connectionString;
    }

    public PostgreSqlMigrationDatabase(NpgsqlDataSource dataSource, MigrationOptions options,
        long advisoryLockKey, ILogger? logger = null)
        : this(options, advisoryLockKey, logger) {
        ArgumentNullException.ThrowIfNull(dataSource);
        _schema = ResolveSchema(new NpgsqlConnectionStringBuilder(dataSource.ConnectionString).SearchPath);
        _dataSource = dataSource;
    }

    private PostgreSqlMigrationDatabase(MigrationOptions options, long advisoryLockKey, ILogger? logger) {
        ArgumentNullException.ThrowIfNull(options);
        MigrationTableName.Validate(options.HistoryTableName);
        _options = options;
        _advisoryLockKey = advisoryLockKey;
        _logger = logger ?? NullLogger.Instance;
    }

    private int CommandTimeoutSeconds => ToSeconds(_options.CommandTimeout);

    private int LockTimeoutSeconds => ToSeconds(_options.LockTimeout);

    public async Task<IMigrationSession> ConnectAsync(bool exclusive, CancellationToken cancellationToken) {
        var (connection, lifetime) = await OpenConnectionAsync(cancellationToken);
        try {
            if (exclusive) {
                await AcquireLockAsync(connection, cancellationToken);
                if (_schema is not null) await EnsureSchemaAsync(connection, _schema, cancellationToken);
            }
        }
        catch {
            await lifetime.DisposeAsync();
            throw;
        }

        return new PostgreSqlMigrationSession(
            connection,
            lifetime,
            _schema,
            _options.HistoryTableName,
            CommandTimeoutSeconds,
            exclusive,
            _advisoryLockKey,
            LockTimeoutSeconds,
            _logger);
    }

    /// <summary>
    /// Opens the session's connection and returns what disposing ends it. From a borrowed data source that is the
    /// pooled connection itself. From a connection string it is an <see cref="UnpooledNpgsqlSession"/>: a genuinely
    /// dedicated physical connection, so closing it always releases the session advisory lock server-side and no
    /// error path can park a pooled connector that still holds the lock. Unlike
    /// <c>new NpgsqlConnection(connectionString)</c>, it also leaves no data source behind in Npgsql's static pool
    /// registry, which matters to a test suite that migrates one fresh database per test.
    /// </summary>
    private async Task<(NpgsqlConnection Connection, IAsyncDisposable Lifetime)> OpenConnectionAsync(
        CancellationToken cancellationToken) {
        if (_dataSource is not null) {
            var pooled = await _dataSource.OpenConnectionAsync(cancellationToken);
            return (pooled, pooled);
        }

        var session = await UnpooledNpgsqlSession.OpenAsync(_connectionString!, cancellationToken);
        return (session.Connection, session);
    }

    private async Task AcquireLockAsync(NpgsqlConnection connection, CancellationToken cancellationToken) {
        // Session-level, never transaction-level: the lock must span non-transactional scripts, and it
        // dies with the connection — no lock row to clean up after a crash.
        await using var command = new NpgsqlCommand("SELECT pg_advisory_lock($1)", connection) {
            CommandTimeout = LockTimeoutSeconds,
            Parameters = { new NpgsqlParameter<long> { TypedValue = _advisoryLockKey } }
        };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// The schema prefix-free scripts land in is whatever the connection's <c>Search Path</c> names first —
    /// the same setting the application's own queries resolve through, so migrations and runtime cannot
    /// drift apart. Nothing to resolve when the connection leaves it at the server default, and
    /// <c>public</c>/<c>$user</c> are never candidates for creation: the first exists on every stock
    /// database and the second is not a literal schema name.
    /// </summary>
    private static string? ResolveSchema(string? searchPath) {
        if (string.IsNullOrWhiteSpace(searchPath)) return null;

        var first = searchPath.Split(',')[0].Trim().Trim('"');
        if (first.Length == 0 || first is "public" or "$user") return null;

        // The schema name reaches DDL unparameterized (CREATE SCHEMA, the qualified history table), so it
        // is checked here rather than trusted from the connection string.
        var valid = (char.IsAsciiLetter(first[0]) || first[0] == '_')
                    && first.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
        if (!valid)
            throw new MigrationException(
                $"Search path schema '{first}' is not a plain identifier (letters, digits, underscores, not starting with a digit).");

        return first;
    }

    /// <summary>
    /// Creates the connection's schema when it does not exist yet, so a fresh database needs no manual
    /// preparation step before the first migration. Exclusive sessions only: a read-only session must not
    /// write, and it correctly finds no history table when the schema is absent — everything is pending.
    /// <c>CREATE SCHEMA IF NOT EXISTS</c> can still race two concurrent creators into a unique violation,
    /// so it runs under the advisory lock the caller has already acquired.
    /// </summary>
    private async Task EnsureSchemaAsync(NpgsqlConnection connection, string schema,
        CancellationToken cancellationToken) {
        await using var command =
            new NpgsqlCommand($"CREATE SCHEMA IF NOT EXISTS \"{schema}\"", connection)
                { CommandTimeout = CommandTimeoutSeconds };
        await command.ExecuteNonQueryAsync(cancellationToken);
        _logger.LogDebug("Ensured migration schema {Schema} exists.", schema);
    }

    /// <summary>Npgsql command timeouts are integer seconds with 0 as "no timeout"; non-positive spans (e.g. <see cref="Timeout.InfiniteTimeSpan"/>) mean no timeout.</summary>
    private static int ToSeconds(TimeSpan? timeout) {
        return timeout is null || timeout.Value <= TimeSpan.Zero ? 0 : Math.Max(1, (int)timeout.Value.TotalSeconds);
    }
}
