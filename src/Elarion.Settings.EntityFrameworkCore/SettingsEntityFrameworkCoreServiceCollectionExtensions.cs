using Elarion.Settings.InProcess;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Elarion.Settings.EntityFrameworkCore;

/// <summary>Registers the EF Core database backend for Elarion settings.</summary>
public static class SettingsEntityFrameworkCoreServiceCollectionExtensions {
    /// <summary>
    /// Registers the settings foundation (change source, manager, time provider) and uses
    /// <see cref="EfCoreSettingsStore{TDbContext}"/> as the <see cref="ISettingsStore"/>, replacing the
    /// in-process default so this call is authoritative regardless of order. The context must map
    /// <see cref="Setting"/> via <c>UseElarionSettings</c> in its <c>OnModelCreating</c>.
    /// </summary>
    /// <remarks>
    /// On the writing node the in-process change source observes every successful write, including one made inside
    /// a caller-owned transaction (deferred and announced on commit, dropped on rollback), so single-node live
    /// reload of watchers, the settings <c>IConfiguration</c> provider and the scheduler works out of the box.
    /// What it cannot do is cross process boundaries: a settings change written on one node is not observed by
    /// another node's watchers or scheduler until that node restarts. When the durable store and the in-process
    /// source are both active, a <see cref="MultiInstanceChangeNotificationWarning"/> hosted service logs a startup
    /// Warning so the single-instance limitation is visible at runtime, not only in the docs.
    /// Register the PostgreSQL <c>LISTEN/NOTIFY</c> source (<c>AddElarionPostgreSqlSettingsChanges</c> in
    /// <c>Elarion.Settings.PostgreSql</c>) — or another cross-instance
    /// <see cref="ISettingsChangeSource"/>/<see cref="ISettingsChangePublisher"/> backend — to propagate
    /// changes across nodes.
    /// </remarks>
    /// <typeparam name="TDbContext">The context whose model includes <see cref="Setting"/>.</typeparam>
    public static IServiceCollection AddElarionSettingsEntityFrameworkCore<TDbContext>(this IServiceCollection services)
        where TDbContext : DbContext {
        ArgumentNullException.ThrowIfNull(services);

        services.AddElarionSettings();
        services.RemoveAll<ISettingsStore>();
        services.AddScoped<ISettingsStore, EfCoreSettingsStore<TDbContext>>();

        // How the store signals a successful write. The default publishes through the in-process publisher:
        // immediately for a non-transactional write, and — via the dispatch scope + transaction interceptor below —
        // after commit for a write inside a caller-owned transaction (dropped on rollback). A backend-aware source
        // (PostgreSQL LISTEN/NOTIFY) replaces the notifier with one whose delivery the database commit-gates and
        // which also crosses process boundaries. Scoped so it shares the per-scope dispatch buffer with the store.
        services.TryAddScoped<IEfCoreSettingsChangeNotifier, ChangePublisherSettingsChangeNotifier>();

        // The per-scope buffer the notifier defers transactional writes into, plus the interceptor that flushes it
        // after the caller's transaction commits (and drops it on rollback), auto-attached to TDbContext via an
        // IDbContextOptionsConfiguration so the host needs no manual AddInterceptors wiring.
        services.TryAddScoped<SettingsChangeDispatchScope>();
        services.TryAddScoped<SettingsChangeDispatchTransactionInterceptor>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDbContextOptionsConfiguration<TDbContext>,
            SettingsChangeDispatchOptionsConfiguration<TDbContext>>());

        // Surface the single-instance-notification limitation of the durable store + in-process source at runtime.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, MultiInstanceChangeNotificationWarning>());

        return services;
    }
}

/// <summary>
/// Logs a startup Warning when the durable EF Core settings store is paired with the in-process change source,
/// because a settings change on one node is then never observed by another node's watchers or scheduler until
/// that node restarts. This is a diagnostic only — it never fails startup.
/// </summary>
internal sealed class MultiInstanceChangeNotificationWarning(
    ISettingsChangeSource changeSource,
    ILogger<MultiInstanceChangeNotificationWarning> logger) : IHostedService {
    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken) {
        if (changeSource is InProcessSettingsChangeSource) {
            logger.LogWarning(
                "Elarion settings use the durable EF Core store with the in-process change source: change " +
                "notifications are single-instance, so in a multi-node deployment a settings change on one node " +
                "will not reach other nodes' watchers or scheduler until they restart. Register a cross-instance " +
                "ISettingsChangeSource (Postgres LISTEN/NOTIFY, Redis) to propagate changes across nodes.");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
