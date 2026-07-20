namespace Elarion.Sql;

/// <summary>
/// Session access directly on the <see cref="ISqlDatabase"/> handle. An extension rather than an interface
/// member, so a custom database implementation (a tenant or replica router) only supplies
/// <see cref="ISqlDatabase.GetDataSource"/> and inherits the ergonomics.
/// </summary>
public static class SqlDatabaseExtensions {
    /// <summary>
    /// Opens an <b>owning</b> one-shot session over a fresh pooled connection from the database — disposing
    /// the session returns the connection. Calls run autonomously (per-call auto-commit, no unit of work): the
    /// pattern for singleton-eligible handlers, which cannot inject the scoped <see cref="ISqlSession"/> —
    /// hold the singleton <see cref="ISqlDatabase"/> and open a session per operation. Because the session owns
    /// its connection, it can also begin a deferred transaction later on the <b>same</b> connection —
    /// <see cref="ISqlOwnedSession.BeginTransactionAsync"/> — for the read-first-then-commit-atomically shape.
    /// Inside a scoped handler, inject <see cref="ISqlSession"/> instead so writes join the framework unit of
    /// work.
    /// </summary>
    /// <example>
    /// <code>
    /// public sealed class WorldTick(ISqlDatabase db) {   // singleton-eligible: no scoped dependencies
    ///     public async ValueTask&lt;Result&gt; HandleAsync(Tick tick, CancellationToken ct) {
    ///         await using var session = await db.OpenSessionAsync(ct);
    ///         var rows = await session.QueryAsync&lt;Reading&gt;($"{Reading.Select} WHERE cell = {tick.Cell}", ct);
    ///         // …
    ///     }
    /// }
    /// </code>
    /// </example>
    public static async ValueTask<ISqlOwnedSession> OpenSessionAsync(
        this ISqlDatabase database, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(database);
        var connection = await database.GetDataSource().OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return new OwnedSqlSession(connection);
    }

    /// <summary>
    /// Opens an <b>owning</b>, <b>transactional</b> one-shot session: a fresh pooled connection with a
    /// transaction already begun, so several statements commit or roll back together <b>outside</b> the
    /// framework unit of work. Commit explicitly on the returned <see cref="ISqlTransaction"/>; disposing
    /// without a commit rolls back, and disposal returns the connection either way. Inside a scoped handler,
    /// inject the scoped <see cref="ISqlSession"/> instead — the framework transaction decorator owns
    /// commit/rollback there.
    /// </summary>
    /// <param name="database">The database handle to open from.</param>
    /// <param name="isolationLevel">
    /// Optional isolation level for the transaction; omit for the provider's default.
    /// </param>
    /// <param name="cancellationToken">Cancels opening the connection and beginning the transaction.</param>
    /// <example>
    /// <code>
    /// // Two writes that must commit together (e.g. write the new key AND clear the ticket):
    /// await using var tx = await db.BeginTransactionAsync(ct);
    /// await tx.ExecuteAsync($"UPDATE {Account.Table} SET session_key = {key} WHERE id = {id}", ct);
    /// await tx.ExecuteAsync($"UPDATE {Ticket.Table} SET ticket = NULL WHERE account_id = {id}", ct);
    /// await tx.CommitAsync(ct);
    /// </code>
    /// </example>
    public static async ValueTask<ISqlTransaction> BeginTransactionAsync(
        this ISqlDatabase database, System.Data.IsolationLevel? isolationLevel = null,
        CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(database);
        var connection = await database.GetDataSource().OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try {
            var transaction = isolationLevel is { } level
                ? await connection.BeginTransactionAsync(level, cancellationToken).ConfigureAwait(false)
                : await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            return new OwnedSqlTransaction(connection, transaction);
        }
        catch {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc cref="BeginTransactionAsync(ISqlDatabase, System.Data.IsolationLevel?, CancellationToken)"/>
    public static ValueTask<ISqlTransaction> BeginTransactionAsync(
        this ISqlDatabase database, CancellationToken cancellationToken) {
        return database.BeginTransactionAsync(isolationLevel: null, cancellationToken);
    }
}
