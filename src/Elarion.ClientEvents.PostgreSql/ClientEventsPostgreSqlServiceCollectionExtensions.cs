using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Elarion.ClientEvents.PostgreSql;

/// <summary>Registers the PostgreSQL <c>LISTEN/NOTIFY</c> broadcaster for Elarion client events.</summary>
public static class ClientEventsPostgreSqlServiceCollectionExtensions {
    /// <summary>
    /// Replaces the in-process client-event broadcaster with the cross-node PostgreSQL <c>LISTEN/NOTIFY</c>
    /// one, so a publish on one node reaches the browsers connected to every node. Every node — including the
    /// publishing one — receives the event through its listen connection, keeping a single delivery path.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="connectionString">
    /// The connection string of the application's PostgreSQL database; the broadcaster opens its own dedicated
    /// listen connection there.
    /// </param>
    /// <param name="configure">Optional configuration of <see cref="PostgreSqlClientEventOptions"/>.</param>
    /// <returns>The same service collection for chaining.</returns>
    /// <remarks>
    /// Composes with <c>AddElarionClientEvents</c> in either order (this call registers the runtime too, so
    /// generated per-module topic registrations keep working unchanged). Delivery to the browser stays
    /// at-most-once: after a dropped listen connection every local subscriber receives
    /// <see cref="ClientEventControlEvents.Connected"/> and re-queries.
    /// </remarks>
    public static IServiceCollection AddElarionPostgreSqlClientEvents(
        this IServiceCollection services,
        string connectionString,
        Action<PostgreSqlClientEventOptions>? configure = null) {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        return AddCore(
            services,
            configure,
            provider => new PostgreSqlClientEventBroadcaster(
                NpgsqlDataSource.Create(connectionString),
                ownsDataSource: true,
                provider.GetRequiredService<PostgreSqlClientEventOptions>(),
                provider.GetRequiredService<ILogger<PostgreSqlClientEventBroadcaster>>()));
    }

    /// <summary>
    /// The <see cref="NpgsqlDataSource"/> overload of
    /// <see cref="AddElarionPostgreSqlClientEvents(IServiceCollection, string, Action{PostgreSqlClientEventOptions}?)"/>
    /// for hosts that already manage a data source; the broadcaster borrows it and does not dispose it.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="dataSource">The data source of the application's PostgreSQL database.</param>
    /// <param name="configure">Optional configuration of <see cref="PostgreSqlClientEventOptions"/>.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddElarionPostgreSqlClientEvents(
        this IServiceCollection services,
        NpgsqlDataSource dataSource,
        Action<PostgreSqlClientEventOptions>? configure = null) {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(dataSource);

        return AddCore(
            services,
            configure,
            provider => new PostgreSqlClientEventBroadcaster(
                dataSource,
                ownsDataSource: false,
                provider.GetRequiredService<PostgreSqlClientEventOptions>(),
                provider.GetRequiredService<ILogger<PostgreSqlClientEventBroadcaster>>()));
    }

    private static IServiceCollection AddCore(
        IServiceCollection services,
        Action<PostgreSqlClientEventOptions>? configure,
        Func<IServiceProvider, PostgreSqlClientEventBroadcaster> broadcasterFactory) {
        var options = new PostgreSqlClientEventOptions();
        configure?.Invoke(options);

        services.AddElarionClientEvents();
        services.TryAddSingleton(options);
        services.TryAddSingleton(broadcasterFactory);

        // Replace (not TryAdd) the broadcaster seam so this call is authoritative regardless of whether
        // AddElarionClientEvents ran first.
        services.RemoveAll<IClientEventBroadcaster>();
        services.AddSingleton<IClientEventBroadcaster>(
            static provider => provider.GetRequiredService<PostgreSqlClientEventBroadcaster>());

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, PostgreSqlClientEventListener>());

        return services;
    }
}
