using Elarion.Abstractions.ClientEvents;
using Elarion.Abstractions.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Elarion.ClientEvents;

/// <summary>Registers the client-event runtime: topic catalog, publisher, and the in-process fan-out default.</summary>
public static class ClientEventsServiceCollectionExtensions {
    /// <summary>
    /// Adds the client-event services and declares topics. Additive: call once per module (a future
    /// generator emits these per-module calls); the runtime registrations are idempotent and the declared
    /// topics compose into one catalog.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Declares this call's topics.</param>
    /// <example>
    /// <code>
    /// services.AddElarionClientEvents(events => events
    ///     .AddTopic&lt;InvoiceChanged&gt;("invoicing.invoiceChanged", t => t.RequirePermission("invoices", Verbs.Read)));
    /// </code>
    /// </example>
    public static IServiceCollection AddElarionClientEvents(
        this IServiceCollection services, Action<ClientEventsBuilder>? configure = null) {
        ArgumentNullException.ThrowIfNull(services);

        services.AddElarionJson();

        if (configure is not null) {
            var builder = new ClientEventsBuilder();
            configure(builder);
            services.AddSingleton(new ClientEventTopicRegistration(builder.Build()));
        }

        services.TryAddSingleton(static sp => new ClientEventTopicCatalog(
            sp.GetServices<ClientEventTopicRegistration>().SelectMany(static r => r.Topics)));
        // The export-facing manifest (Abstractions) the build-time schema tool resolves to emit the schema's
        // `events` block — the ClientCapabilityManifest pattern (ADR-0032).
        services.TryAddSingleton(static sp => new ClientEventTopicManifest {
            Topics = [
                .. sp.GetRequiredService<ClientEventTopicCatalog>().Topics
                    .OrderBy(static t => t.Name, StringComparer.Ordinal)
                    .Select(static t => new ClientEventTopicManifestEntry { Name = t.Name, EventType = t.EventType })
            ]
        });
        services.TryAddSingleton<ClientEventSubscriptionLifecycle>();
        services.TryAddSingleton<IClientEventInterest>(static sp =>
            sp.GetRequiredService<ClientEventSubscriptionLifecycle>());
        services.TryAddSingleton<ClientEventSubscriberRegistry>();
        services.TryAddSingleton<IClientEventLocalDelivery>(static sp =>
            sp.GetRequiredService<ClientEventSubscriberRegistry>());
        services.TryAddSingleton<IClientEventSubscriptionSource>(static sp =>
            sp.GetRequiredService<ClientEventSubscriberRegistry>());
        services.TryAddSingleton<IClientEventBroadcaster, InProcessClientEventBroadcaster>();
        services.TryAddSingleton<IClientEventPublisher, ClientEventPublisher>();
        // Scoped: reads the caller from the scope's ICurrentUser (the HTTP request scope, or a dispatch
        // scope a connection adapter seeds with the connection's principal).
        services.TryAddScoped<ClientEventSubscriptionResolver>();
        return services;
    }
}

/// <summary>The topics one <c>AddElarionClientEvents</c> call declared; the catalog composes all of them.</summary>
internal sealed record ClientEventTopicRegistration(IReadOnlyList<ClientEventTopic> Topics);
