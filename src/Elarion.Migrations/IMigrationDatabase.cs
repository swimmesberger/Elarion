namespace Elarion.Migrations;

/// <summary>
/// The database-specific half of the migration engine (ADR-0060): opens locked, single-threaded sessions
/// for the neutral <see cref="MigrationRunner"/> to orchestrate. One provider per database engine
/// (<c>Elarion.Migrations.PostgreSql</c>, <c>Elarion.Migrations.Sqlite</c>); the runner supplies the
/// roll-forward policy, the provider supplies the SQL and the lock.
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
/// A single migration session over one dedicated connection: the history-table operations and script
/// execution the <see cref="MigrationRunner"/> drives. All calls run sequentially on the one connection —
/// never concurrently. Disposal releases any exclusive lock the session holds and closes the connection.
/// </summary>
public interface IMigrationSession : IAsyncDisposable {
    /// <summary>Creates the history table and its version uniqueness constraint if they do not exist.</summary>
    Task EnsureHistoryTableAsync(CancellationToken cancellationToken);

    /// <summary>Whether the history table already exists (read-only sessions use this before loading).</summary>
    Task<bool> HistoryTableExistsAsync(CancellationToken cancellationToken);

    /// <summary>Loads every history row in ascending <c>installed_rank</c> order.</summary>
    Task<IReadOnlyList<AppliedMigrationRow>> LoadHistoryAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Runs <paramref name="sql"/> in one transaction, then inserts the row from
    /// <paramref name="historyRowFactory"/> in that <em>same</em> transaction and commits — so a failure
    /// rolls back the script and its history row together (the roll-forward, no-repair invariant). The
    /// factory is invoked just before the insert (after the script ran) so the runner can stamp the
    /// measured execution duration onto the row. Provider exceptions propagate; the runner wraps them in
    /// <see cref="MigrationExecutionException"/>.
    /// </summary>
    Task ExecuteInTransactionAsync(string sql, Func<MigrationHistoryRecord> historyRowFactory,
        CancellationToken cancellationToken);

    /// <summary>
    /// Runs <paramref name="sql"/> outside any transaction (for <c>-- elarion: no-transaction</c>
    /// scripts) and writes no history row — the runner records the applied or failed row separately
    /// afterwards. Provider exceptions propagate.
    /// </summary>
    Task ExecuteWithoutTransactionAsync(string sql, CancellationToken cancellationToken);

    /// <summary>Inserts a standalone history row (a no-transaction applied/failed row, or a baseline row).</summary>
    Task InsertHistoryRowAsync(MigrationHistoryRecord historyRow, CancellationToken cancellationToken);

    /// <summary>Deletes the history row with the given rank (resolving a failed migration as retry).</summary>
    Task DeleteHistoryRowAsync(int installedRank, CancellationToken cancellationToken);

    /// <summary>
    /// Marks the row with the given rank as <c>applied</c>, setting its checksum when
    /// <paramref name="checksum"/> is non-null (resolving a failed migration as mark-applied).
    /// </summary>
    Task MarkHistoryRowAppliedAsync(int installedRank, string? checksum, CancellationToken cancellationToken);
}
