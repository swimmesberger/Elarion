using System.Data;
using System.Data.Common;

namespace Elarion.Sql;

/// <summary>
/// The <see cref="ISqlOwnedSession"/> behind <see cref="SqlDatabaseExtensions.OpenSessionAsync"/>: owns one
/// pooled connection for its lifetime and supports deferred transactions on it. Beginning one sets
/// <see cref="CurrentTransaction"/> (so the session's own calls enlist too, exactly like the scoped session
/// under the unit of work); the returned scope's commit/rollback/dispose clears it, returning the session to
/// autonomous mode. Disposal disposes any still-open transaction (rolling it back) and then the connection.
/// </summary>
internal sealed class OwnedSqlSession(DbConnection connection) : ISqlOwnedSession {
    private DbTransaction? _transaction;

    public DbTransaction? CurrentTransaction => _transaction;

    public ValueTask<DbConnection> GetConnectionAsync(CancellationToken cancellationToken = default) {
        return new ValueTask<DbConnection>(connection);
    }

    public async ValueTask<ISqlTransaction> BeginTransactionAsync(
        IsolationLevel? isolationLevel = null, CancellationToken cancellationToken = default) {
        if (_transaction is not null)
            throw new InvalidOperationException(
                "A transaction is already open on this session; commit or dispose it before beginning another. "
                + "Nested transactions belong to the framework unit of work, not the one-shot session.");

        var transaction = isolationLevel is { } level
            ? await connection.BeginTransactionAsync(level, cancellationToken).ConfigureAwait(false)
            : await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        _transaction = transaction;
        return new SessionTransaction(this, transaction);
    }

    public async ValueTask DisposeAsync() {
        // A still-open transaction dies first (ADO.NET dispose-uncommitted rolls back), then the connection
        // returns to the pool.
        if (_transaction is not null) await _transaction.DisposeAsync().ConfigureAwait(false);

        await connection.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// A deferred transaction begun on the owning session: it never owns the connection — commit, rollback, or
    /// dispose-without-commit ends only the transaction and detaches it from the session, which continues
    /// autonomously. While open, it IS the session's <see cref="ISqlSession.CurrentTransaction"/>, so calls on
    /// either object run in the same transaction on the same connection.
    /// </summary>
    private sealed class SessionTransaction(OwnedSqlSession owner, DbTransaction transaction) : ISqlTransaction {
        private bool _completed;

        public DbTransaction? CurrentTransaction => transaction;

        public ValueTask<DbConnection> GetConnectionAsync(CancellationToken cancellationToken = default) {
            return owner.GetConnectionAsync(cancellationToken);
        }

        public async ValueTask CommitAsync(CancellationToken cancellationToken = default) {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            Detach();
        }

        public async ValueTask RollbackAsync(CancellationToken cancellationToken = default) {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            Detach();
        }

        public async ValueTask DisposeAsync() {
            if (_completed) return;

            // Dispose without an explicit commit rolls back (the dispose == rollback contract); the session's
            // connection stays open and the session returns to autonomous mode.
            await transaction.DisposeAsync().ConfigureAwait(false);
            Detach();
        }

        private void Detach() {
            _completed = true;
            if (ReferenceEquals(owner._transaction, transaction)) owner._transaction = null;
        }
    }
}
