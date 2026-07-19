using System.Data.Common;

namespace Elarion.Sql;

/// <summary>
/// The scoped <see cref="ISqlSession"/>: pins one pooled connection — from the data source the
/// <see cref="ISqlDatabase"/> hands out for the scope — and tracks the transaction the
/// <see cref="SqlUnitOfWork"/> opens on it. Registered as a scoped service and shared (by reference) with the
/// scope's <c>IUnitOfWork</c>, so the unit of work and the handler always act on the same physical connection.
/// </summary>
internal sealed class SqlSession(ISqlDatabase database) : ISqlSession {
    private DbConnection? _connection;

    /// <inheritdoc />
    public DbTransaction? CurrentTransaction { get; internal set; }

    // The unit of work owns the transaction lifetime; the session only carries the reference so the convenience
    // methods can enlist it. Kept as a field (not a property) so a nested savepoint counter can Interlocked on it.
    internal int NestingDepth;

    /// <inheritdoc />
    public async ValueTask<DbConnection> GetConnectionAsync(CancellationToken cancellationToken = default) {
        // Open once and cache: every later call returns the same open connection so a transaction begun on it
        // stays in effect for the whole scope. The provider decides which data source this scope opens from
        // (single, tenant-routed, replica). A pooled connection is cheap to hold for a request's duration.
        return _connection ??= await database.GetDataSource()
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync() {
        if (_connection is not null) await _connection.DisposeAsync().ConfigureAwait(false);
    }
}
