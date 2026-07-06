namespace Elarion.Abstractions.ClientEvents;

/// <summary>The audience kind of a <see cref="ClientEventScope"/>.</summary>
public enum ClientEventScopeKind {
    /// <summary>Every subscriber of the topic (that passed the topic's subscribe-time authorization).</summary>
    Global = 0,

    /// <summary>Subscribers whose authenticated user id equals the scope value.</summary>
    User = 1,

    /// <summary>
    /// Subscribers of an application-defined resource key (e.g. <c>"customer:42"</c>); subscribing requires an
    /// <see cref="IClientEventSubscriptionAuthorizer"/> and is denied without one (fail-closed).
    /// </summary>
    Resource = 2,
}
