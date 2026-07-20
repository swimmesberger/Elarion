using System.Data;

namespace Elarion.Sql;

/// <summary>
/// An <see cref="ISqlSession"/> that <b>owns its connection exclusively</b> — what
/// <see cref="SqlDatabaseExtensions.OpenSessionAsync"/> returns. Exclusive ownership is what makes deferred
/// transactions safe, so this is the one session that can begin one mid-session:
/// <see cref="BeginTransactionAsync"/> opens a transaction on the <b>same</b> connection (no second connection),
/// the session's calls enlist it while it is open, and after commit/rollback the session continues autonomously
/// and may begin another.
/// </summary>
/// <remarks>
/// This capability is deliberately absent from <see cref="ISqlSession"/> itself: the scoped session is shared
/// with the framework unit of work, whose decorator owns the transaction lifecycle there — the same
/// compile-time rule that keeps commit off <see cref="ISqlSession"/>.
/// </remarks>
/// <example>
/// <code>
/// await using var session = await db.OpenSessionAsync(ct);
/// var account = await session.QueryFirstOrDefaultAsync&lt;Account&gt;($"{Account.Select} WHERE id = {id}", ct);
///
/// await using (var tx = await session.BeginTransactionAsync(ct)) {   // same connection
///     await tx.ExecuteAsync($"UPDATE {Account.Table} SET session_key = {key} WHERE id = {id}", ct);
///     await tx.ExecuteAsync($"UPDATE {Ticket.Table} SET ticket = NULL WHERE account_id = {id}", ct);
///     await tx.CommitAsync(ct);
/// }
/// // session usable again, autonomous.
/// </code>
/// </example>
public interface ISqlOwnedSession : ISqlSession {
    /// <summary>
    /// Begins a transaction on the session's own connection and returns it as an <see cref="ISqlTransaction"/>:
    /// commit explicitly; disposing without a commit rolls back and the session continues autonomously (the
    /// connection stays open — only <see cref="SqlDatabaseExtensions.BeginTransactionAsync"/>-opened
    /// transactions own their connection). While the transaction is open, calls on this session enlist it too.
    /// </summary>
    /// <param name="isolationLevel">Optional isolation level; omit for the provider's default.</param>
    /// <param name="cancellationToken">Cancels beginning the transaction.</param>
    /// <exception cref="InvalidOperationException">
    /// A transaction is already open on this session — commit or dispose it first. Nested transactions are the
    /// framework unit of work's domain (savepoints), not this one-shot surface's.
    /// </exception>
    ValueTask<ISqlTransaction> BeginTransactionAsync(
        IsolationLevel? isolationLevel = null, CancellationToken cancellationToken = default);
}
