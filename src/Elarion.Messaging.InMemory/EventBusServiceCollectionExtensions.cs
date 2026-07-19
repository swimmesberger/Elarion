using Elarion.Abstractions.Messaging;
using Elarion.Idempotency;
using Elarion.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

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
    /// Adds the in-memory domain (Plane A) and integration (Plane B) event buses and the after-commit delivery tier,
    /// auto-attaching the commit-gating interceptors to <typeparamref name="TContext"/>.
    /// </summary>
    /// <typeparam name="TContext">The application's EF Core context whose transaction gates Plane B delivery.</typeparam>
    /// <remarks>
    /// Combines <see cref="AddElarionDomainEventBus"/> (from <c>Elarion</c>) with
    /// <see cref="AddElarionInMemoryIntegrationEventBus{TContext}(IServiceCollection, EventBusOptions?)"/>. Generated event
    /// consumer descriptor registration must still be called separately.
    /// </remarks>
    public static IServiceCollection AddElarionInMemoryEventBus<TContext>(
        this IServiceCollection services,
        EventBusOptions? options = null)
        where TContext : DbContext {
        services.AddElarionDomainEventBus();
        return services.AddElarionInMemoryIntegrationEventBus<TContext>(options);
    }

    /// <summary>
    /// Adds the in-memory domain and integration event buses (interceptors auto-attached to
    /// <typeparamref name="TContext"/>) using values from the <c>EventBus</c> configuration section.
    /// </summary>
    public static IServiceCollection AddElarionInMemoryEventBus<TContext>(
        this IServiceCollection services,
        IConfiguration configuration)
        where TContext : DbContext {
        services.AddElarionDomainEventBus();
        return services.AddElarionInMemoryIntegrationEventBus<TContext>(configuration);
    }

    /// <summary>
    /// Adds the in-memory integration-event (Plane B) bus and auto-attaches the commit-gating interceptors to
    /// <typeparamref name="TContext"/>, so buffered events are flushed after that context commits and discarded on
    /// rollback — no manual <c>AddInterceptors</c> wiring required.
    /// </summary>
    /// <typeparam name="TContext">The application's EF Core context whose transaction gates Plane B delivery.</typeparam>
    public static IServiceCollection AddElarionInMemoryIntegrationEventBus<TContext>(
        this IServiceCollection services,
        EventBusOptions? options = null)
        where TContext : DbContext {
        services.AddElarionInMemoryIntegrationEventBus(options);
        services.TryAddEnumerable(
            ServiceDescriptor
                .Singleton<IDbContextOptionsConfiguration<TContext>, EventDispatchOptionsConfiguration<TContext>>());
        return services;
    }

    /// <summary>
    /// Adds the in-memory integration-event bus (interceptors auto-attached to <typeparamref name="TContext"/>) using
    /// values from the <c>EventBus</c> configuration section.
    /// </summary>
    public static IServiceCollection AddElarionInMemoryIntegrationEventBus<TContext>(
        this IServiceCollection services,
        IConfiguration configuration)
        where TContext : DbContext {
        return services.AddElarionInMemoryIntegrationEventBus<TContext>(ReadOptions(configuration));
    }

    /// <summary>
    /// Registers the integration-event bus building blocks — the bus, the hosted delivery pump, the per-scope buffer,
    /// and the commit-gating EF Core interceptors — <b>without</b> attaching the interceptors to any context.
    /// </summary>
    /// <remarks>
    /// This is the low-level primitive. Prefer <see cref="AddElarionInMemoryIntegrationEventBus{TContext}(IServiceCollection, EventBusOptions?)"/>,
    /// which also auto-attaches the interceptors so delivery is commit-gated out of the box. Use this overload only
    /// when you attach the interceptors yourself (e.g. <c>AddDbContext((sp, o) =&gt; o.AddInterceptors(sp.GetServices&lt;IInterceptor&gt;()))</c>)
    /// or when exercising the bus without a database.
    /// </remarks>
    public static IServiceCollection AddElarionInMemoryIntegrationEventBus(
        this IServiceCollection services,
        EventBusOptions? options = null) {
        options ??= new EventBusOptions();
        services.TryAddSingleton(options);
        // The inbox (ADR-0022) is default-on for handler-form integration consumers; the delivery tier wires the
        // idempotency building blocks so their pipelines resolve (TryAdd-based — a durable EF store registration
        // wins). On this best-effort tier the inbox only guards in-process multi-delivery.
        services.AddElarionIdempotency();
        services.TryAddSingleton<EventSubscriptionRegistry>();
        services.TryAddSingleton<EventDispatchPump>();
        // TryAddEnumerable keyed on the concrete factory-target keeps a second registration call from adding a
        // second reader to the SingleReader channel (undefined behaviour). Every registration below is idempotent
        // so AddElarionInMemoryIntegrationEventBus is safe to call more than once (e.g. via AddElarionInMemoryEventBus).
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, EventDispatchPump>(sp =>
                sp.GetRequiredService<EventDispatchPump>()));
        services.TryAddScoped<EventDispatchScope>();
        services.TryAddScoped<IIntegrationEventBus, InMemoryIntegrationEventBus>();
        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<IInterceptor, EventDispatchSaveChangesInterceptor>());
        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<IInterceptor, EventDispatchTransactionInterceptor>());
        return services;
    }

    /// <summary>
    /// Registers the integration-event bus building blocks (without attaching interceptors) using values from the
    /// <c>EventBus</c> configuration section. Prefer the <typeparamref name="TContext"/> overload; see
    /// <see cref="AddElarionInMemoryIntegrationEventBus(IServiceCollection, EventBusOptions?)"/>.
    /// </summary>
    public static IServiceCollection AddElarionInMemoryIntegrationEventBus(
        this IServiceCollection services,
        IConfiguration configuration) {
        return services.AddElarionInMemoryIntegrationEventBus(ReadOptions(configuration));
    }

    private static EventBusOptions ReadOptions(IConfiguration configuration) {
        return new EventBusOptions {
            Enabled = ReadBool(configuration, "EventBus:Enabled", true),
            DeliveryChannelCapacity = Math.Max(1, ReadInt(configuration, "EventBus:DeliveryChannelCapacity", 1024))
        };
    }

    private static bool ReadBool(IConfiguration configuration, string key, bool defaultValue) {
        var value = configuration[key];
        if (string.IsNullOrWhiteSpace(value)) return defaultValue;

        if (bool.TryParse(value, out var parsed)) return parsed;

        throw new InvalidOperationException($"Configuration value '{key}' must be a boolean.");
    }

    private static int ReadInt(IConfiguration configuration, string key, int defaultValue) {
        var value = configuration[key];
        if (string.IsNullOrWhiteSpace(value)) return defaultValue;

        if (int.TryParse(value, out var parsed)) return parsed;

        throw new InvalidOperationException($"Configuration value '{key}' must be an integer.");
    }
}
