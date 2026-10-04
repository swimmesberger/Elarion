using System.Data.Common;

namespace Elarion.Migrations;

/// <summary>
/// The database-specific half of the migration engine (ADR-0060): opens locked, single-threaded sessions
/// for the neutral <see cref="MigrationRunner"/> to orchestrate. One provider per database engine
/// (<c>Elarion.Sql.PostgreSql</c>, <c>Elarion.Sql.Sqlite</c>); the runner supplies the plan, the ordering and
/// the roll-forward policy — transactions included — and the provider supplies the lock, the history-table SQL
/// and SQL execution.
/// </summary>
public interface IMigrationDatabase {
    /// <summary>
    /// Opens a dedicated connection for one migration operation. When <paramref name="exclusive"/> is
    /// <see langword="true"/> the session also acquires the engine's exclusive migration lock (waiting up
    /// to the options' lock timeout) so concurrent runners serialize; read-only operations
    /// (<see cref="IMigrationRunner.ValidateAsync"/>, <see cref="IMigrationRunner.GetPendingAsync"/>) pass
    /// <see langword="false"/>. Disposing the session releases the lock and closes the connection.
    /// </summary>
    Task<IMigrationSession> ConnectAsync(bool exclusive, CancellationToken cancellationToken);
}

/// <summary>
/// A single migration session over one dedicated connection: the history-table operations the
/// <see cref="MigrationRunner"/> drives plus SQL execution. The runner owns transaction scope — it begins a
/// transaction on <see cref="Connection"/> and passes it to <see cref="InsertHistoryRowAsync"/> and
/// <see cref="ExecuteSqlAsync"/> — so a step's writes and its history row commit or roll back together. All
/// calls run sequentially on the one connection, never concurrently. Disposal releases any exclusive lock
/// the session holds and closes the connection.
/// </summary>
public interface IMigrationSession : IAsyncDisposable {
    /// <summary>The session's open connection, which holds the exclusive lock for an exclusive session.</summary>
    DbConnection Connection { get; }

    /// <summary>
    /// Creates the history table and its version uniqueness constraint if they do not exist, and upgrades a
    /// table written by the script-only runner (ADR-0057/0060) in place to the step-shaped layout.
    /// </summary>
    Task EnsureHistoryTableAsync(CancellationToken cancellationToken);

    /// <summary>Whether the history table already exists (read-only sessions use this before loading).</summary>
    Task<bool> HistoryTableExistsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Loads every history row in ascending <c>installed_rank</c> order. A table of the pre-step layout is read
    /// as step-shaped rows (kind <c>sql</c>) without being modified, so read-only sessions work before the
    /// first exclusive run upgrades it.
    /// </summary>
    Task<IReadOnlyList<AppliedMigrationRow>> LoadHistoryAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Inserts a history row, in <paramref name="transaction"/> when one is given — that is how a transactional
    /// step's row commits atomically with its writes (the roll-forward, no-repair invariant).
    /// </summary>
    Task InsertHistoryRowAsync(MigrationHistoryRecord historyRow, DbTransaction? transaction,
        CancellationToken cancellationToken);

    /// <summary>Deletes the history row with the given rank (resolving a failed migration as retry).</summary>
    Task DeleteHistoryRowAsync(int installedRank, CancellationToken cancellationToken);

    /// <summary>
    /// Marks the row with the given rank as <c>applied</c>, setting its checksum when
    /// <paramref name="checksum"/> is non-null (resolving a failed migration as mark-applied).
    /// </summary>
    Task MarkHistoryRowAppliedAsync(int installedRank, string? checksum, CancellationToken cancellationToken);

    /// <summary>
    /// Executes <paramref name="sql"/>: as one command in <paramref name="transaction"/> when given, otherwise
    /// outside any transaction with the engine's autocommit rules (PostgreSQL runs the script statement by
    /// statement so <c>CREATE INDEX CONCURRENTLY</c> works). Provider exceptions propagate; the runner wraps
    /// them in <see cref="MigrationExecutionException"/>.
    /// </summary>
    Task ExecuteSqlAsync(string sql, DbTransaction? transaction, CancellationToken cancellationToken);
}
