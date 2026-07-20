using System.Data.Common;

namespace Elarion.Sql;

/// <summary>
/// The <see cref="ISqlTransaction"/> behind <see cref="SqlDatabaseExtensions.BeginTransactionAsync"/>: owns both
/// the pooled connection and the transaction begun on it. Every session call enlists the transaction (it is the
/// <see cref="CurrentTransaction"/>); disposal without an explicit commit rolls back via ADO.NET's
/// dispose-uncommitted-transaction contract, then returns the connection.
/// </summary>
internal sealed class OwnedSqlTransaction(DbConnection connection, DbTransaction transaction) : ISqlTransaction {
    public DbTransaction? CurrentTransaction => transaction;

    public ValueTask<DbConnection> GetConnectionAsync(CancellationToken cancellationToken = default) {
        return new ValueTask<DbConnection>(connection);
    }

    public ValueTask CommitAsync(CancellationToken cancellationToken = default) {
        return new ValueTask(transaction.CommitAsync(cancellationToken));
    }

    public ValueTask RollbackAsync(CancellationToken cancellationToken = default) {
        return new ValueTask(transaction.RollbackAsync(cancellationToken));
    }

    public async ValueTask DisposeAsync() {
        // Disposing an uncommitted DbTransaction rolls it back (the dispose == rollback contract, same as the
        // unit-of-work root scope); after a commit it is a no-op. The connection is returned afterwards.
        await transaction.DisposeAsync().ConfigureAwait(false);
        await connection.DisposeAsync().ConfigureAwait(false);
    }
}
