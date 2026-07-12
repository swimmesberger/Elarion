using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Elarion.EntityFrameworkCore.BulkOperations;

/// <summary>
/// Bulk operations over a <see cref="DbSet{TEntity}"/>, following the shape sketched for EF Core
/// itself (dotnet/efcore #27333 / #29897): set-based, non-tracking, database-generated values are not
/// fetched back.
/// </summary>
/// <example>
/// <code>
/// await context.Orders.ExecuteInsertAsync(orders, cancellationToken: ct);
/// </code>
/// </example>
public static class ElarionBulkOperationsDbSetExtensions {
    /// <summary>
    /// Inserts <paramref name="entities"/> using the database's native bulk mechanism (PostgreSQL
    /// binary <c>COPY</c>), without tracking them in the change tracker.
    /// </summary>
    /// <remarks>
    /// Runs on the context's own connection, so it participates in
    /// <c>context.Database.CurrentTransaction</c> when one is open. Store-generated columns
    /// (identity, computed, store defaults) are filled by the database and deliberately not read back;
    /// client-side value generators do not run, so caller-assigned keys (e.g. v7 Guids) are required.
    /// </remarks>
    /// <returns>The number of rows written.</returns>
    public static Task<long> ExecuteInsertAsync<TEntity>(
        this DbSet<TEntity> set,
        IEnumerable<TEntity> entities,
        BulkInsertOptions? options = null,
        CancellationToken cancellationToken = default)
        where TEntity : class {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(entities);

        var context = set.GetService<ICurrentDbContext>().Context;
        return GetProvider(context).ExecuteInsertAsync(context, entities, options ?? BulkInsertOptions.Default, cancellationToken);
    }

    /// <summary>
    /// Inserts a streamed <paramref name="entities"/> sequence using the database's native bulk
    /// mechanism, without tracking or materializing the sequence.
    /// </summary>
    /// <returns>The number of rows written.</returns>
    /// <inheritdoc cref="ExecuteInsertAsync{TEntity}(DbSet{TEntity}, IEnumerable{TEntity}, BulkInsertOptions?, CancellationToken)" path="/remarks"/>
    public static Task<long> ExecuteInsertAsync<TEntity>(
        this DbSet<TEntity> set,
        IAsyncEnumerable<TEntity> entities,
        BulkInsertOptions? options = null,
        CancellationToken cancellationToken = default)
        where TEntity : class {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(entities);

        var context = set.GetService<ICurrentDbContext>().Context;
        return GetProvider(context).ExecuteInsertAsync(context, entities, options ?? BulkInsertOptions.Default, cancellationToken);
    }

    private static IBulkInsertProvider GetProvider(DbContext context) =>
        (IBulkInsertProvider?)context.GetInfrastructure().GetService(typeof(IBulkInsertProvider))
            ?? throw new InvalidOperationException(
                "No bulk operations provider is configured for this DbContext. Add one to the context options, " +
                "e.g. 'options.UseNpgsql(...).UseElarionPostgreSqlBulkOperations()' from Elarion.BulkOperations.PostgreSql.");
}
