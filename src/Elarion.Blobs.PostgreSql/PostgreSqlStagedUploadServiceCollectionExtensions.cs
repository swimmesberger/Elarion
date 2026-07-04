using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Elarion.Blobs.PostgreSql;

/// <summary>
/// Registers the durable PostgreSQL staged-upload store.
/// </summary>
public static class PostgreSqlStagedUploadServiceCollectionExtensions {
    /// <summary>
    /// Replaces the in-memory staged-upload store with the durable PostgreSQL staging store backed by
    /// <typeparamref name="TDbContext"/>, registers the background collector for expired and completed
    /// sessions, and wires the PostgreSQL blob lifecycle plus its pending-blob garbage collector.
    /// </summary>
    /// <typeparam name="TDbContext">The context whose model includes <see cref="StagedUploadRow"/> via <c>UseElarionStagedUploads</c>.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="configureGc">Optional configuration of the session <see cref="StagedUploadGcOptions"/>.</param>
    /// <param name="configureBlobGc">Optional configuration of the pending-blob <see cref="BlobGcOptions"/>.</param>
    /// <returns>The same service collection for chaining.</returns>
    /// <remarks>
    /// <para>
    /// A completed staged upload is written as a <b>pending</b> blob, so this method also calls
    /// <see cref="PostgreSqlBlobStoreServiceCollectionExtensions.AddElarionPostgreSqlBlobLifecycle{TDbContext}(IServiceCollection, Action{BlobGcOptions}?)"/>
    /// (idempotent) to register the blob store, the <see cref="IBlobLifecycle"/> commit path, and the
    /// <c>BlobGarbageCollector</c> that reclaims those pending blobs — otherwise an abandoned upload would
    /// leak its pending blob forever, since the session collector never touches the produced blob.
    /// </para>
    /// <para>
    /// The application still maps the tables in <c>OnModelCreating</c>: <c>UseElarionBlobStorage()</c> for
    /// the blob tables and <c>UseElarionStagedUploads()</c> for the staging table.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddElarionPostgreSqlStagedUploads<TDbContext>(
        this IServiceCollection services,
        Action<StagedUploadGcOptions>? configureGc = null,
        Action<BlobGcOptions>? configureBlobGc = null)
        where TDbContext : DbContext {
        ArgumentNullException.ThrowIfNull(services);

        // Replace the in-memory default registered by AddElarionStagedUploads.
        services.RemoveAll<IStagedUploadStore>();
        services.AddScoped<IStagedUploadStore, PostgreSqlStagedUploadStore<TDbContext>>();

        services.TryAddSingleton(TimeProvider.System);
        services.AddElarionStagedUploadGarbageCollection(configureGc);

        // A completed staged upload produces a pending blob; without the blob lifecycle and its collector
        // an abandoned upload would leak that blob forever. AddElarionPostgreSqlBlobLifecycle is idempotent
        // if the host already wired it.
        services.AddElarionPostgreSqlBlobLifecycle<TDbContext>(configureBlobGc);

        return services;
    }

    /// <summary>
    /// The <c>connectionString</c> overload of
    /// <see cref="AddElarionPostgreSqlStagedUploads{TDbContext}(IServiceCollection, Action{StagedUploadGcOptions}?, Action{BlobGcOptions}?)"/>:
    /// also registers a shared <c>NpgsqlDataSource</c> (via <c>TryAdd</c>) that the blob store's streaming
    /// reads draw dedicated connections from.
    /// </summary>
    /// <typeparam name="TDbContext">The context whose model includes <see cref="StagedUploadRow"/> via <c>UseElarionStagedUploads</c>.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="connectionString">The connection string of the database the staging and blob tables live in.</param>
    /// <param name="configureGc">Optional configuration of the session <see cref="StagedUploadGcOptions"/>.</param>
    /// <param name="configureBlobGc">Optional configuration of the pending-blob <see cref="BlobGcOptions"/>.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddElarionPostgreSqlStagedUploads<TDbContext>(
        this IServiceCollection services,
        string connectionString,
        Action<StagedUploadGcOptions>? configureGc = null,
        Action<BlobGcOptions>? configureBlobGc = null)
        where TDbContext : DbContext {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.TryAddSingleton(_ => NpgsqlDataSource.Create(connectionString));
        return services.AddElarionPostgreSqlStagedUploads<TDbContext>(configureGc, configureBlobGc);
    }
}
