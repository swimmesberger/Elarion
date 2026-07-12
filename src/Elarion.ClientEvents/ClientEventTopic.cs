using System.Diagnostics.CodeAnalysis;
using Elarion.Abstractions.Authorization;

namespace Elarion.ClientEvents;

/// <summary>
/// A registered client-event topic: the wire name clients subscribe to, the <c>IClientEvent</c> contract type
/// published on it, and the requirements evaluated when a client subscribes.
/// </summary>
public sealed record ClientEventTopic {
    /// <summary>The topic name on the wire (recommended shape: <c>{module}.{event}</c>, e.g.
    /// <c>"invoicing.invoiceChanged"</c>).</summary>
    public required string Name { get; init; }

    /// <summary>The client-event contract type serialized as the payload.</summary>
    public required Type EventType { get; init; }

    /// <summary>Subscribe-time authorization requirements (always at least "authenticated"; see
    /// <see cref="ClientEventTopicOptions"/>). A denied or unknown topic is reported as not found so the
    /// topic's existence is never leaked.</summary>
    public required AuthorizationRequirements Requirements { get; init; }

    /// <summary>Whether the resource segment is a routing key rather than an entitlement: resource-scoped
    /// subscriptions skip the <c>IClientEventSubscriptionAuthorizer</c> seam once the topic's requirements
    /// pass. Defaults to <see langword="false"/> (fail-closed).</summary>
    public bool AllowAnyResource { get; init; }

    /// <summary>The topic's <c>IClientEventSubscriptionObserver</c> implementation, or <see langword="null"/>
    /// when the topic does not observe its subscriptions. Resolved from a fresh DI scope per callback.</summary>
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
    public Type? ObserverType { get; init; }

    /// <summary>The last-subscriber linger before the observer sees interest go inactive (the
    /// reload-debounce). Defaults to 5 seconds.</summary>
    public TimeSpan InterestLinger { get; init; } = DefaultInterestLinger;

    /// <summary>The default <see cref="InterestLinger"/>.</summary>
    public static TimeSpan DefaultInterestLinger { get; } = TimeSpan.FromSeconds(5);
}
