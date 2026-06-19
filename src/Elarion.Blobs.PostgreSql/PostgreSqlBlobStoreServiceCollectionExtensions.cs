using Elarion.Blobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Elarion.Blobs.PostgreSql;

/// <summary>
/// Registers the PostgreSQL-backed blob storage implementation.
/// </summary>
public static class PostgreSqlBlobStoreServiceCollectionExtensions {
    /// <summary>
    /// Registers <see cref="IBlobStore"/> using <see cref="PostgreSqlBlobStore{TDbContext}"/>.
    /// </summary>
    /// <typeparam name="TDbContext">The EF Core context that owns the blob tables.</typeparam>
    /// <param name="services">The service collection to add blob storage to.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddPostgreSqlBlobStore<TDbContext>(this IServiceCollection services)
        where TDbContext : DbContext {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<IBlobStore, PostgreSqlBlobStore<TDbContext>>();
        return services;
    }
}
