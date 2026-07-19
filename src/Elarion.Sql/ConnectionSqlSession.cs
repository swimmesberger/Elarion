using System.Data.Common;

namespace Elarion.Sql;

/// <summary>
/// An <see cref="ISqlSession"/> over one specific connection. Two ownership modes: the non-owning view over a
/// caller-owned connection (and optionally the caller's transaction) that
/// <see cref="SqlConnectionSessionExtensions.AsSqlSession"/> creates — disposing it disposes nothing — and the
/// owning one-shot session <see cref="SqlDatabaseExtensions.OpenSessionAsync"/> opens from an
/// <see cref="ISqlDatabase"/>, whose disposal returns the pooled connection. Either way the transaction decision
/// is made once at creation: every write enlists <see cref="CurrentTransaction"/> automatically, exactly like the
/// scoped session does inside a handler.
/// </summary>
internal sealed class ConnectionSqlSession(DbConnection connection, DbTransaction? transaction, bool ownsConnection)
    : ISqlSession {
    public DbTransaction? CurrentTransaction => transaction;

    public ValueTask<DbConnection> GetConnectionAsync(CancellationToken cancellationToken = default) {
        return new ValueTask<DbConnection>(connection);
    }

    public ValueTask DisposeAsync() {
        // Owning (OpenSessionAsync): dispose the pooled connection with the session. Non-owning (AsSqlSession):
        // the caller opened the connection (and began the transaction) — the caller disposes them.
        return ownsConnection ? connection.DisposeAsync() : default;
    }
}
