using Elarion.Blobs.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Elarion.Blobs.Tus.PostgreSql;

/// <summary>
/// Registers the durable PostgreSQL tus staging store.
/// </summary>
public static class PostgreSqlTusServiceCollectionExtensions {
    /// <summary>
    /// Replaces the in-memory tus store with the durable PostgreSQL staging store backed by
    /// <typeparamref name="TDbContext"/>, registers the background collector for expired and completed
    /// sessions, and wires the PostgreSQL blob lifecycle plus its pending-blob garbage collector.
    /// </summary>
    /// <typeparam name="TDbContext">The context whose model includes <see cref="TusUploadRow"/> via <c>UseElarionTusStorage</c>.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional configuration of <see cref="TusGcOptions"/>.</param>
    /// <param name="configureBlobGc">Optional configuration of the pending-blob <see cref="BlobGcOptions"/>.</param>
    /// <returns>The same service collection for chaining.</returns>
    /// <remarks>
    /// <para>
    /// Call after <c>AddElarionTus</c> (which registers <c>TusOptions</c> and the in-memory default this
    /// replaces). The completed tus upload is written as a <b>pending</b> blob, so this method also calls
    /// <see cref="PostgreSqlBlobStoreServiceCollectionExtensions.AddElarionPostgreSqlBlobLifecycle{TDbContext}"/>
    /// (idempotent) to register the blob store, the <see cref="IBlobLifecycle"/> commit path, and the
    /// <c>BlobGarbageCollector</c> that reclaims those pending blobs — otherwise an abandoned upload would
    /// leak its pending blob forever, since the tus session collector never touches the produced blob.
    /// </para>
    /// <para>
    /// The application still maps the tables in <c>OnModelCreating</c>: <c>UseElarionBlobStorage()</c> for
    /// the blob tables and <c>UseElarionTusStorage()</c> for the staging table.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddElarionTusPostgreSql<TDbContext>(
        this IServiceCollection services,
        Action<TusGcOptions>? configure = null,
        Action<BlobGcOptions>? configureBlobGc = null)
        where TDbContext : DbContext {
        ArgumentNullException.ThrowIfNull(services);

        // Replace the in-memory default registered by AddElarionTus.
        services.RemoveAll<ITusUploadStore>();
        services.AddScoped<ITusUploadStore, PostgreSqlTusUploadStore<TDbContext>>();

        var options = new TusGcOptions();
        configure?.Invoke(options);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(options);
        services.AddHostedService<TusUploadGarbageCollector>();

        // A completed tus upload produces a pending blob; without the blob lifecycle and its collector an
        // abandoned upload would leak that blob forever. AddElarionPostgreSqlBlobLifecycle is TryAdd-based, so this
        // is idempotent if the host already wired it.
        services.AddElarionPostgreSqlBlobLifecycle<TDbContext>(configureBlobGc);

        return services;
    }
}
