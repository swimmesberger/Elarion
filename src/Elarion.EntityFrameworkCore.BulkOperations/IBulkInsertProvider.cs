using Microsoft.EntityFrameworkCore;

namespace Elarion.EntityFrameworkCore.BulkOperations;

/// <summary>
/// Provider seam behind <c>ExecuteInsertAsync</c>: implementations translate a stream of entities into
/// the database's native bulk mechanism (PostgreSQL binary <c>COPY</c>, SQL Server <c>SqlBulkCopy</c>, …).
/// </summary>
/// <remarks>
/// Registered into the EF Core internal service provider by a provider package's
/// <c>DbContextOptionsBuilder</c> extension (e.g. <c>UseElarionPostgreSqlBulkOperations()</c>), the same
/// way EF provider plugins ship their services. Implementations must run on the context's own
/// connection so the insert participates in <c>Database.CurrentTransaction</c> when one is open.
/// Both overloads exist so a materialized collection is not forced through an
/// <see cref="IAsyncEnumerable{T}"/> adapter.
/// </remarks>
public interface IBulkInsertProvider {
    /// <summary>Inserts <paramref name="entities"/> into <paramref name="context"/>'s database without tracking them.</summary>
    /// <returns>The number of rows written.</returns>
    Task<long> ExecuteInsertAsync<TEntity>(
        DbContext context,
        IEnumerable<TEntity> entities,
        BulkInsertOptions options,
        CancellationToken cancellationToken)
        where TEntity : class;

    /// <summary>Inserts a streamed <paramref name="entities"/> sequence into <paramref name="context"/>'s database without tracking.</summary>
    /// <returns>The number of rows written.</returns>
    Task<long> ExecuteInsertAsync<TEntity>(
        DbContext context,
        IAsyncEnumerable<TEntity> entities,
        BulkInsertOptions options,
        CancellationToken cancellationToken)
        where TEntity : class;
}
