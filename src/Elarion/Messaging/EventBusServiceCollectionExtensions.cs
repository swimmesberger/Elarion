using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Elarion.Abstractions.Messaging;

namespace Elarion.Messaging;

/// <summary>
/// Registers the in-memory event bus runtime.
/// </summary>
public static class EventBusServiceCollectionExtensions {
    /// <summary>
    /// Adds the in-memory domain and integration event buses and the after-commit delivery pump
    /// using explicit options.
    /// </summary>
    /// <remarks>
    /// Registers <see cref="IDomainEventBus"/>, <see cref="IIntegrationEventBus"/>,
    /// <see cref="IEventDispatchScope"/>, and the hosted delivery pump. Generated event consumer
    /// descriptor registration must still be called separately.
    /// </remarks>
    public static IServiceCollection AddInMemoryEventBus(
        this IServiceCollection services,
        EventBusOptions? options = null) {
        options ??= new EventBusOptions();
        services.TryAddSingleton(options);
        services.TryAddSingleton<EventSubscriptionRegistry>();
        services.TryAddSingleton<EventDispatchPump>();
        services.AddHostedService(sp => sp.GetRequiredService<EventDispatchPump>());
        services.TryAddScoped<EventDispatchScope>();
        services.TryAddScoped<IEventDispatchScope>(sp => sp.GetRequiredService<EventDispatchScope>());
        services.TryAddScoped<IDomainEventBus, InMemoryDomainEventBus>();
        services.TryAddScoped<IIntegrationEventBus, InMemoryIntegrationEventBus>();
        return services;
    }

    /// <summary>
    /// Adds the in-memory event bus using values from the <c>EventBus</c> configuration section.
    /// </summary>
    /// <remarks>
    /// Reads <c>EventBus:Enabled</c> and <c>EventBus:DeliveryChannelCapacity</c>. Invalid boolean or
    /// integer values throw during service registration.
    /// </remarks>
    public static IServiceCollection AddInMemoryEventBus(
        this IServiceCollection services,
        IConfiguration configuration) {
        var options = new EventBusOptions {
            Enabled = ReadBool(configuration, "EventBus:Enabled", true),
            DeliveryChannelCapacity = Math.Max(1, ReadInt(configuration, "EventBus:DeliveryChannelCapacity", 1024))
        };

        return services.AddInMemoryEventBus(options);
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
