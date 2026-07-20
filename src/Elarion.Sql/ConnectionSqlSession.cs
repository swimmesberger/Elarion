using System.Data.Common;

namespace Elarion.Sql;

/// <summary>
/// The non-owning <see cref="ISqlSession"/> view over a caller-owned connection (and optionally the caller's
/// transaction) that <see cref="SqlConnectionSessionExtensions.AsSqlSession"/> creates. The caller keeps full
/// ownership: disposing this view disposes nothing, and the transaction decision is made once at wrap time —
/// every write on the view enlists <see cref="CurrentTransaction"/> automatically, exactly like the scoped
/// session does inside a handler. (Owning sessions come from <c>OpenSessionAsync</c>/<c>BeginTransactionAsync</c>
/// on the <see cref="ISqlDatabase"/> handle.)
/// </summary>
internal sealed class ConnectionSqlSession(DbConnection connection, DbTransaction? transaction) : ISqlSession {
    public DbTransaction? CurrentTransaction => transaction;

    public ValueTask<DbConnection> GetConnectionAsync(CancellationToken cancellationToken = default) {
        return new ValueTask<DbConnection>(connection);
    }

    public ValueTask DisposeAsync() {
        // Non-owning: the caller opened the connection (and began the transaction) — the caller disposes them.
        return default;
    }
}
