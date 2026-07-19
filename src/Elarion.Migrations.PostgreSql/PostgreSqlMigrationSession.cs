using Elarion.Migrations;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Elarion.Migrations.PostgreSql;

/// <summary>
/// A PostgreSQL migration session over one dedicated connection (ADR-0057). When it holds the
/// session-level <c>pg_advisory_lock</c>, disposal releases it before closing the connection — though a
/// broken connection releases it server-side anyway (session scope, no lock row to clean up).
/// </summary>
internal sealed class PostgreSqlMigrationSession : IMigrationSession {
    private readonly NpgsqlConnection _connection;
    private readonly SchemaHistory _history;
    private readonly int _commandTimeoutSeconds;
    private readonly bool _holdsLock;
    private readonly long _advisoryLockKey;
    private readonly int _lockTimeoutSeconds;
    private readonly ILogger _logger;

    internal PostgreSqlMigrationSession(
        NpgsqlConnection connection,
        string historyTableName,
        int commandTimeoutSeconds,
        bool holdsLock,
        long advisoryLockKey,
        int lockTimeoutSeconds,
        ILogger logger) {
        _connection = connection;
        _commandTimeoutSeconds = commandTimeoutSeconds;
        _holdsLock = holdsLock;
        _advisoryLockKey = advisoryLockKey;
        _lockTimeoutSeconds = lockTimeoutSeconds;
        _logger = logger;
        _history = new SchemaHistory(connection, historyTableName, commandTimeoutSeconds);
    }

    public Task EnsureHistoryTableAsync(CancellationToken cancellationToken) {
        return _history.EnsureTableAsync(cancellationToken);
    }

    public Task<bool> HistoryTableExistsAsync(CancellationToken cancellationToken) {
        return _history.TableExistsAsync(cancellationToken);
    }

    public Task<IReadOnlyList<AppliedMigrationRow>> LoadHistoryAsync(CancellationToken cancellationToken) {
        return _history.LoadAsync(cancellationToken);
    }

    public async Task ExecuteInTransactionAsync(string sql, Func<MigrationHistoryRecord> historyRowFactory,
        CancellationToken cancellationToken) {
        await using var transaction = await _connection.BeginTransactionAsync(cancellationToken);
        await using (var command = new NpgsqlCommand(sql, _connection, transaction)
                         { CommandTimeout = _commandTimeoutSeconds }) {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        // The history row commits atomically with the script — the no-repair invariant.
        await _history.InsertAsync(historyRowFactory(), transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task ExecuteWithoutTransactionAsync(string sql, CancellationToken cancellationToken) {
        // Statement by statement: a multi-statement command travels as one simple-query message, which
        // PostgreSQL wraps in a single implicit transaction and which therefore breaks
        // CREATE INDEX CONCURRENTLY, the very statement the no-transaction directive exists for.
        foreach (var statement in SqlStatementSplitter.Split(sql)) {
            await using var command = new NpgsqlCommand(statement, _connection)
                { CommandTimeout = _commandTimeoutSeconds };
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public Task InsertHistoryRowAsync(MigrationHistoryRecord historyRow, CancellationToken cancellationToken) {
        return _history.InsertAsync(historyRow, null, cancellationToken);
    }

    public Task DeleteHistoryRowAsync(int installedRank, CancellationToken cancellationToken) {
        return _history.DeleteAsync(installedRank, cancellationToken);
    }

    public Task MarkHistoryRowAppliedAsync(int installedRank, string? checksum, CancellationToken cancellationToken) {
        return _history.MarkAppliedAsync(installedRank, checksum, cancellationToken);
    }

    public async ValueTask DisposeAsync() {
        if (_holdsLock) await ReleaseLockAsync();

        await _connection.DisposeAsync();
    }

    private async Task ReleaseLockAsync() {
        // Deliberately not the caller's token: a cancelled startup must still unlock, because a pooled
        // connection (the data-source overload) returned with the session lock held would block every
        // later runner until the pool prunes it. The unlock itself is instantaneous.
        try {
            await using var command = new NpgsqlCommand("SELECT pg_advisory_unlock($1)", _connection) {
                CommandTimeout = _lockTimeoutSeconds,
                Parameters = { new NpgsqlParameter<long> { TypedValue = _advisoryLockKey } }
            };
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }
        catch (Exception ex) {
            // A broken connection releases the session lock by itself.
            _logger.LogDebug(ex, "Releasing the migration advisory lock failed; the connection will release it.");
        }
    }
}
