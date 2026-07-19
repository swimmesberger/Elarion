using Elarion.Migrations;
using Microsoft.Data.Sqlite;

namespace Elarion.Migrations.Sqlite;

/// <summary>
/// A SQLite migration session over one dedicated connection (ADR-0060). An exclusive session holds the
/// per-file in-process gate (<paramref name="gate"/>) so concurrent runners serialize; disposal releases
/// the gate after closing the connection. Read-only sessions pass a null gate.
/// </summary>
internal sealed class SqliteMigrationSession(
    SqliteConnection connection,
    SqliteSchemaHistory history,
    int commandTimeoutSeconds,
    SemaphoreSlim? gate) : IMigrationSession {
    public Task EnsureHistoryTableAsync(CancellationToken cancellationToken) {
        return history.EnsureTableAsync(cancellationToken);
    }

    public Task<bool> HistoryTableExistsAsync(CancellationToken cancellationToken) {
        return history.TableExistsAsync(cancellationToken);
    }

    public Task<IReadOnlyList<AppliedMigrationRow>> LoadHistoryAsync(CancellationToken cancellationToken) {
        return history.LoadAsync(cancellationToken);
    }

    public async Task ExecuteInTransactionAsync(string sql, Func<MigrationHistoryRecord> historyRowFactory,
        CancellationToken cancellationToken) {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using (var command = connection.CreateCommand()) {
            command.CommandText = sql;
            command.CommandTimeout = commandTimeoutSeconds;
            command.Transaction = transaction;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        // The history row commits atomically with the script — the no-repair invariant.
        await history.InsertAsync(historyRowFactory(), transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task ExecuteWithoutTransactionAsync(string sql, CancellationToken cancellationToken) {
        // SQLite has full transactional DDL and no CREATE INDEX CONCURRENTLY, so the no-transaction path
        // is rarely needed; when a script asks for it, run it in autocommit mode (each statement commits
        // on its own), matching the "may be half-applied on failure" semantics the directive documents.
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = commandTimeoutSeconds;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public Task InsertHistoryRowAsync(MigrationHistoryRecord historyRow, CancellationToken cancellationToken) {
        return history.InsertAsync(historyRow, null, cancellationToken);
    }

    public Task DeleteHistoryRowAsync(int installedRank, CancellationToken cancellationToken) {
        return history.DeleteAsync(installedRank, cancellationToken);
    }

    public Task MarkHistoryRowAppliedAsync(int installedRank, string? checksum, CancellationToken cancellationToken) {
        return history.MarkAppliedAsync(installedRank, checksum, cancellationToken);
    }

    public async ValueTask DisposeAsync() {
        try {
            await connection.DisposeAsync();
        }
        finally {
            gate?.Release();
        }
    }
}
