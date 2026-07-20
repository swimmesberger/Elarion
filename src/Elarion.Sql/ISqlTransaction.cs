namespace Elarion.Sql;

/// <summary>
/// An owning, transactional one-shot session opened by
/// <see cref="SqlDatabaseExtensions.BeginTransactionAsync"/>: a fresh pooled connection with a transaction
/// already begun, so every call on it — the full <see cref="SqlSessionExtensions"/> surface — runs inside that
/// transaction. Commit is explicit; disposing without a commit rolls back (the same contract as the framework
/// unit-of-work scope), and disposal returns the connection either way.
/// </summary>
/// <remarks>
/// This is the atomic multi-write path for code <b>outside</b> the framework unit of work — singleton-eligible
/// handlers and tooling. Inside a scoped handler, inject the scoped <see cref="ISqlSession"/> instead: the
/// framework transaction decorator owns commit/rollback there, which is exactly why <see cref="ISqlSession"/>
/// itself deliberately has no commit method.
/// </remarks>
/// <example>
/// <code>
/// // Two writes that must commit together, outside any unit of work:
/// await using var tx = await db.BeginTransactionAsync(ct);
/// await tx.ExecuteAsync($"UPDATE {Account.Table} SET session_key = {key} WHERE id = {id}", ct);
/// await tx.ExecuteAsync($"UPDATE {Ticket.Table} SET ticket = NULL WHERE account_id = {id}", ct);
/// await tx.CommitAsync(ct);
/// </code>
/// </example>
public interface ISqlTransaction : ISqlSession {
    /// <summary>Commits all work done in the session's transaction.</summary>
    ValueTask CommitAsync(CancellationToken cancellationToken = default);

    /// <summary>Rolls back all work done in the session's transaction (disposing without a commit does too).</summary>
    ValueTask RollbackAsync(CancellationToken cancellationToken = default);
}
