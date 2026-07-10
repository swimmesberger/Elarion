using Elarion.Abstractions.Messaging;
using Elarion.Abstractions.Serialization;
using Elarion.Idempotency;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Elarion.Messaging.Outbox;

/// <summary>
/// Registers the EF Core transactional outbox as the integration-event (Plane B) delivery tier.
/// </summary>
public static class OutboxServiceCollectionExtensions
{
    /// <summary>
    /// Registers the durable outbox <see cref="IIntegrationEventBus"/> backed by <typeparamref name="TDbContext"/>,
    /// plus its storage, dispatcher, and background delivery worker.
    /// </summary>
    /// <typeparam name="TDbContext">The context whose model includes <see cref="OutboxMessage"/> via <c>UseElarionOutbox</c>.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional configuration of <see cref="OutboxOptions"/>.</param>
    /// <returns>The same service collection for chaining.</returns>
    /// <remarks>
    /// This replaces the in-memory integration tier; the domain plane and the generated consumer descriptors are
    /// registered separately (for example via <c>AddElarionDomainEventBus</c> for Plane A and the generated
    /// <c>Add{Assembly}EventConsumers</c>). Consumers must be idempotent because delivery is at-least-once.
    /// Set <see cref="OutboxOptions.RunDeliveryWorker"/> to <see langword="false"/> on publisher-only instances
    /// so another node hosting all the consumers runs the delivery worker instead.
    /// </remarks>
    public static IServiceCollection AddElarionOutbox<TDbContext>(
        this IServiceCollection services,
        Action<OutboxOptions>? configure = null)
        where TDbContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new OutboxOptions();
        configure?.Invoke(options);

        services.AddElarionJson();
        // The inbox (ADR-0022) is default-on for handler-form integration consumers, and the outbox is the tier
        // where it matters (at-least-once). TryAdd-based, so a host that already wired the durable EF store via
        // AddElarionIdempotencyEntityFrameworkCore keeps it; without one, the in-memory store gives process-local
        // dedup (pair it with the EF store for an inbox that survives restarts).
        services.AddElarionIdempotency();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(options);
        services.TryAddSingleton<OutboxEventDispatcher>();

        services.TryAddScoped<IOutboxStore, EfCoreOutboxStore<TDbContext>>();

        // The outbox owns the integration plane: register last-wins so it is authoritative even if an in-memory
        // integration bus was registered for the domain plane's sake.
        services.AddScoped<IIntegrationEventBus, OutboxIntegrationEventBus>();

        // Publisher-only nodes (heterogeneous topologies where another instance hosts all the consumers) opt out
        // of the delivery loop entirely — otherwise this node would claim messages it cannot resolve and park them.
        if (options.RunDeliveryWorker)
        {
            services.AddHostedService<OutboxDeliveryService>();
        }

        return services;
    }
}
