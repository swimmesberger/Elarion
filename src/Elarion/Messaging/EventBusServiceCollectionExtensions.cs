using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Elarion.Abstractions.Messaging;

namespace Elarion.Messaging;

/// <summary>
/// Registers the in-memory domain event (Plane A) bus runtime.
/// </summary>
public static class EventBusServiceCollectionExtensions {
    /// <summary>
    /// Adds the in-memory <see cref="IDomainEventBus"/> and the shared subscription registry.
    /// </summary>
    /// <remarks>
    /// Domain events are dispatched inline within the caller's DI scope, so this tier is EF-agnostic. The integration
    /// (Plane B) tier is registered separately — by the in-memory integration bus in
    /// <c>Elarion.Messaging.InMemory</c> or the EF Core transactional outbox. Generated event consumer
    /// descriptor registration must still be called separately.
    /// </remarks>
    public static IServiceCollection AddInMemoryDomainEventBus(this IServiceCollection services) {
        services.TryAddSingleton<EventSubscriptionRegistry>();
        services.TryAddScoped<IDomainEventBus, InMemoryDomainEventBus>();
        return services;
    }
}
