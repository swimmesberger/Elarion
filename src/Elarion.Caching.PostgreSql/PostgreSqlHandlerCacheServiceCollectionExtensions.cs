using Microsoft.Extensions.Caching.Postgres;
using Microsoft.Extensions.DependencyInjection;

namespace Elarion.Caching.PostgreSql;

/// <summary>
/// Service registration helpers that back Elarion handler caching with a PostgreSQL L2 distributed
/// cache. This is the recommended L2 store for most Elarion applications that already run PostgreSQL:
/// it reuses the database you already operate instead of standing up a separate Redis tier.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="HandlerCacheServiceCollectionExtensions.AddElarionHandlerCaching"/> is backed by
/// <c>HybridCache</c>, which is two-tier: an in-process L1 (<c>MemoryCache</c>) that absorbs the hot
/// path, plus an optional L2 <see cref="Microsoft.Extensions.Caching.Distributed.IDistributedCache"/>
/// that <c>HybridCache</c> auto-discovers from DI and uses for cross-instance coherence, warm-restart
/// survival, and stampede coordination. These helpers register the official
/// <c>Microsoft.Extensions.Caching.Postgres</c> distributed cache as that L2 and then call
/// <c>AddElarionHandlerCaching</c>, so a single call wires the whole stack.
/// </para>
/// <para>
/// The cache table is created as an <c>UNLOGGED</c> table (the package's <c>UseWAL = false</c>): writes
/// skip the write-ahead log, so they are cheaper and produce no WAL/replication traffic. The tradeoff —
/// the table is truncated on a crash or unclean shutdown and is not present on physical standby replicas —
/// is exactly the right one for a cache, whose contents are always reconstructible from the source of
/// truth: the worst case is a cold repopulate, never data loss. Reach for a dedicated tier such as Redis
/// only when the cache itself must be a high-throughput, independently-scaled, or multi-region store.
/// </para>
/// <example>
/// <code>
/// // Program.cs — the recommended one-liner for most applications:
/// builder.Services.AddElarionPostgreSqlHandlerCaching(
///     builder.Configuration.GetConnectionString("CacheDb")!);
/// </code>
/// </example>
/// </remarks>
public static class PostgreSqlHandlerCacheServiceCollectionExtensions {
    private const string DefaultSchemaName = "public";
    private const string DefaultTableName = "elarion_cache";

    /// <summary>
    /// Backs Elarion handler caching with a PostgreSQL L2 distributed cache using the supplied
    /// connection string and Elarion's defaults (an auto-created <c>UNLOGGED</c> cache table in the
    /// <c>public</c> schema). This is the recommended registration for most applications.
    /// </summary>
    /// <param name="services">The service collection to add the cache to.</param>
    /// <param name="connectionString">The PostgreSQL connection string for the cache database.</param>
    /// <param name="configure">
    /// Optional hook to override any default (schema, table name, expiration sweep, WAL behavior, and so on).
    /// It runs after Elarion's defaults are applied, so it always wins.
    /// </param>
    /// <returns>The same <see cref="IServiceCollection"/> so calls can be chained.</returns>
    public static IServiceCollection AddElarionPostgreSqlHandlerCaching(
        this IServiceCollection services,
        string connectionString,
        Action<PostgresCacheOptions>? configure = null) {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        return services.AddElarionPostgreSqlHandlerCaching(options => {
            options.ConnectionString = connectionString;
            configure?.Invoke(options);
        });
    }

    /// <summary>
    /// Backs Elarion handler caching with a PostgreSQL L2 distributed cache, configured entirely by the
    /// supplied delegate. Elarion's defaults (an auto-created <c>UNLOGGED</c> cache table in the
    /// <c>public</c> schema) are applied first; <paramref name="configure"/> runs last and must set at
    /// least the connection string (or a data source).
    /// </summary>
    /// <param name="services">The service collection to add the cache to.</param>
    /// <param name="configure">Configures the Postgres cache; runs after Elarion's defaults and overrides them.</param>
    /// <returns>The same <see cref="IServiceCollection"/> so calls can be chained.</returns>
    public static IServiceCollection AddElarionPostgreSqlHandlerCaching(
        this IServiceCollection services,
        Action<PostgresCacheOptions> configure) {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddDistributedPostgresCache(options => {
            // Elarion defaults: an auto-created UNLOGGED cache table. UseWAL = false makes the table
            // UNLOGGED — cache writes skip the write-ahead log (cheaper, no replication traffic) and the
            // table is truncated on crash, which is fine for a cache. The caller's delegate runs last and
            // can override any of these (including opting back into a WAL-logged, replicated table).
            options.SchemaName = DefaultSchemaName;
            options.TableName = DefaultTableName;
            options.CreateIfNotExists = true;
            options.UseWAL = false;
            configure(options);
        });

        // HybridCache (registered here) auto-discovers the IDistributedCache above and uses it as the L2.
        return services.AddElarionHandlerCaching();
    }
}
