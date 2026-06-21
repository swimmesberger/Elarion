using Elarion.Abstractions.Messaging;
using Elarion.Messaging;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Elarion.Messaging.InMemory;

/// <summary>
/// Registers the in-memory integration-event (Plane B) tier, commit-gated by the EF Core DbContext transaction.
/// </summary>
/// <remarks>
/// This is the simple, best-effort sibling of the EF Core transactional outbox: integration events are buffered per
/// scope, flushed to an in-process delivery pump after commit and discarded on rollback by EF Core interceptors. Events
/// flushed but undelivered at process exit are lost; use the outbox for at-least-once durability.
/// </remarks>
public static class EventBusServiceCollectionExtensions {
    /// <summary>
    /// Adds the in-memory domain (Plane A) and integration (Plane B) event buses and the after-commit delivery tier.
    /// </summary>
    /// <remarks>
    /// Combines <see cref="EventBusServiceCollectionExtensions.AddInMemoryIntegrationEventBus(IServiceCollection, EventBusOptions?)"/>
    /// with <see cref="EventBusServiceCollectionExtensions.AddInMemoryDomainEventBus"/> from <c>Elarion</c>. Generated
    /// event consumer descriptor registration must still be called separately.
    /// </remarks>
    public static IServiceCollection AddInMemoryEventBus(
        this IServiceCollection services,
        EventBusOptions? options = null) {
        services.AddInMemoryDomainEventBus();
        return services.AddInMemoryIntegrationEventBus(options);
    }

    /// <summary>
    /// Adds the in-memory domain and integration event buses using values from the <c>EventBus</c> configuration section.
    /// </summary>
    public static IServiceCollection AddInMemoryEventBus(
        this IServiceCollection services,
        IConfiguration configuration) {
        services.AddInMemoryDomainEventBus();
        return services.AddInMemoryIntegrationEventBus(configuration);
    }

    /// <summary>
    /// Adds the in-memory integration-event (Plane B) bus, the hosted delivery pump, and the EF Core interceptors that
    /// flush after commit and discard on rollback, using explicit options.
    /// </summary>
    /// <remarks>
    /// Registers <see cref="IIntegrationEventBus"/>, the scoped buffer, the hosted delivery pump, and the
    /// commit-gating EF Core interceptors. Use alongside an EF Core context registered with <c>AddDbContext</c>, which
    /// resolves DI-registered interceptors per scope. The shared <c>EventSubscriptionRegistry</c> is registered
    /// idempotently so this may be combined with the domain bus.
    /// </remarks>
    public static IServiceCollection AddInMemoryIntegrationEventBus(
        this IServiceCollection services,
        EventBusOptions? options = null) {
        options ??= new EventBusOptions();
        services.TryAddSingleton(options);
        services.TryAddSingleton<EventSubscriptionRegistry>();
        services.TryAddSingleton<EventDispatchPump>();
        services.AddHostedService(sp => sp.GetRequiredService<EventDispatchPump>());
        services.TryAddScoped<EventDispatchScope>();
        services.TryAddScoped<IIntegrationEventBus, InMemoryIntegrationEventBus>();
        services.AddScoped<IInterceptor, EventDispatchSaveChangesInterceptor>();
        services.AddScoped<IInterceptor, EventDispatchTransactionInterceptor>();
        return services;
    }

    /// <summary>
    /// Adds the in-memory integration-event bus using values from the <c>EventBus</c> configuration section.
    /// </summary>
    /// <remarks>
    /// Reads <c>EventBus:Enabled</c> and <c>EventBus:DeliveryChannelCapacity</c>. Invalid boolean or integer values
    /// throw during service registration.
    /// </remarks>
    public static IServiceCollection AddInMemoryIntegrationEventBus(
        this IServiceCollection services,
        IConfiguration configuration) {
        var options = new EventBusOptions {
            Enabled = ReadBool(configuration, "EventBus:Enabled", true),
            DeliveryChannelCapacity = Math.Max(1, ReadInt(configuration, "EventBus:DeliveryChannelCapacity", 1024))
        };

        return services.AddInMemoryIntegrationEventBus(options);
    }

    private static bool ReadBool(IConfiguration configuration, string key, bool defaultValue) {
        var value = configuration[key];
        if (string.IsNullOrWhiteSpace(value)) {
            return defaultValue;
        }

        if (bool.TryParse(value, out var parsed)) {
            return parsed;
        }

        throw new InvalidOperationException($"Configuration value '{key}' must be a boolean.");
    }

    private static int ReadInt(IConfiguration configuration, string key, int defaultValue) {
        var value = configuration[key];
        if (string.IsNullOrWhiteSpace(value)) {
            return defaultValue;
        }

        if (int.TryParse(value, out var parsed)) {
            return parsed;
        }

        throw new InvalidOperationException($"Configuration value '{key}' must be an integer.");
    }
}
