using System.Data.Common;

namespace Elarion.Sql;

/// <summary>
/// The scoped ambient database session for the EF-free SQL tier: one <see cref="DbConnection"/> shared for the
/// lifetime of a request scope, plus the transaction (if any) currently open on it. A handler injects
/// <see cref="ISqlSession"/> instead of a raw <see cref="DbDataSource"/> so its reads and writes run on the same
/// physical connection the <c>SqlUnitOfWork</c> opened a transaction on — that shared connection is what lets the
/// framework <c>TransactionDecorator</c> commit or roll back a handler's SQL work atomically (the SQL-tier
/// analogue of EF Core's shared scoped <c>DbContext</c>).
/// </summary>
/// <remarks>
/// <para>
/// Without a session every convenience call on a <see cref="DbDataSource"/> opens and disposes its own pooled
/// connection, so no transaction can span more than one statement. The session pins a single connection for the
/// scope; the <see cref="SqlSessionExtensions">session convenience methods</see> run against it and enlist
/// <see cref="CurrentTransaction"/> automatically.
/// </para>
/// <para>
/// The connection is opened lazily on first use and disposed when the scope is disposed. A scope is not
/// thread-safe: like a scoped <c>DbContext</c>, one request drives one session on one connection at a time.
/// </para>
/// </remarks>
public interface ISqlSession : IAsyncDisposable {
    /// <summary>
    /// Returns the session's connection, opening it from the underlying data source on first call and returning
    /// the same open connection thereafter so a transaction opened on it stays in effect for the whole scope.
    /// </summary>
    ValueTask<DbConnection> GetConnectionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The transaction currently open on the session's connection, or <see langword="null"/> when no unit of work
    /// is active. The session convenience methods pass it through so statements enlist without the handler
    /// threading a <see cref="DbTransaction"/> by hand.
    /// </summary>
    DbTransaction? CurrentTransaction { get; }
}
