using System.Data.Common;

namespace Elarion.Sql;

/// <summary>
/// The one public bridge from a caller-owned <see cref="DbConnection"/> to the SQL tier's single convenience
/// surface (<see cref="SqlSessionExtensions"/> on <see cref="ISqlSession"/>). Inside a handler, inject the scoped
/// <see cref="ISqlSession"/> instead — this bridge is for code that manages its own connection: DI-free /
/// NativeAOT hosts, tooling, tests, and singleton-eligible handlers that hold a <see cref="DbDataSource"/> and
/// open a connection per operation.
/// </summary>
public static class SqlConnectionSessionExtensions {
    /// <summary>
    /// Wraps the connection as a lightweight, non-owning <see cref="ISqlSession"/> so the session convenience
    /// surface applies to it. The transaction decision is made <b>once, here</b>: pass the caller's open
    /// <paramref name="transaction"/> and every write on the view enlists it; pass none and every call runs
    /// autonomously (per-call auto-commit). There is deliberately no per-call transaction parameter anywhere on
    /// the surface — a write can never accidentally run outside the transaction its scope declared.
    /// </summary>
    /// <param name="connection">The caller-owned connection; the view never disposes it.</param>
    /// <param name="transaction">The caller's open transaction to enlist every write in, if any.</param>
    /// <example>
    /// <code>
    /// // Singleton-eligible handler (no scoped dependencies): per-call autonomous semantics, no unit of work.
    /// await using var connection = await dataSource.OpenConnectionAsync(ct);
    /// var db = connection.AsSqlSession();
    /// var rows = await db.QueryAsync&lt;Order&gt;($"{Order.Select} WHERE status = {status}", ct);
    ///
    /// // Tooling that owns a transaction: wrap once, every write enlists.
    /// await using var transaction = await connection.BeginTransactionAsync(ct);
    /// var tx = connection.AsSqlSession(transaction);
    /// await tx.InsertAsync(order, ct);
    /// await transaction.CommitAsync(ct);
    /// </code>
    /// </example>
    public static ISqlSession AsSqlSession(this DbConnection connection, DbTransaction? transaction = null) {
        ArgumentNullException.ThrowIfNull(connection);
        return new ConnectionSqlSession(connection, transaction);
    }
}
