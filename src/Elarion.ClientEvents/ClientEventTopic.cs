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
}
