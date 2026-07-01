using Elarion.Settings.InProcess;
using Microsoft.EntityFrameworkCore;
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
    /// The durable EF Core store composed with the shipped <b>in-process</b> change source means a settings
    /// change written on one node is not observed by another node's watchers or scheduler until that node
    /// restarts. When both are active, a <see cref="MultiInstanceChangeNotificationWarning"/> hosted service
    /// logs a startup Warning so the single-instance limitation is visible at runtime, not only in the docs.
    /// Replace <see cref="ISettingsChangeSource"/>/<see cref="ISettingsChangePublisher"/> with a cross-instance
    /// backend (Postgres <c>LISTEN/NOTIFY</c>, Redis) to propagate changes across nodes.
    /// </remarks>
    /// <typeparam name="TDbContext">The context whose model includes <see cref="Setting"/>.</typeparam>
    public static IServiceCollection AddElarionSettingsEntityFrameworkCore<TDbContext>(this IServiceCollection services)
        where TDbContext : DbContext {
        ArgumentNullException.ThrowIfNull(services);

        services.AddElarionSettings();
        services.RemoveAll<ISettingsStore>();
        services.AddScoped<ISettingsStore, EfCoreSettingsStore<TDbContext>>();

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
public sealed class MultiInstanceChangeNotificationWarning(
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
