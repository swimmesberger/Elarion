using Elarion.Abstractions.ClientEvents;

namespace Elarion.ClientEvents;

/// <summary>The outcome kind of resolving requested subscriptions; see <see cref="ClientEventSubscriptionResolver"/>.</summary>
public enum ClientEventSubscriptionStatus {
    /// <summary>Every request passed; <see cref="ClientEventSubscriptionResolution.Subscriptions"/> holds the
    /// authorized (topic, scope) pairs ready for <see cref="IClientEventSubscriptionSource.Subscribe"/>.</summary>
    Resolved,

    /// <summary>The caller is not authenticated (HTTP: 401).</summary>
    Unauthenticated,

    /// <summary>The request set is empty or malformed (HTTP: 400).</summary>
    InvalidRequest,

    /// <summary>An unknown topic, a failed topic requirement, or a denied resource scope — deliberately
    /// indistinguishable so a topic's existence is never leaked (HTTP: 404).</summary>
    NotFound,
}

/// <summary>
/// The result of the fail-closed subscribe pipeline: either the fully authorized subscription list, or the
/// first failure — resolution is all-or-nothing, one bad request rejects the whole set.
/// </summary>
public sealed record ClientEventSubscriptionResolution {
    /// <summary>The outcome kind.</summary>
    public required ClientEventSubscriptionStatus Status { get; init; }

    /// <summary>The authorized subscriptions; empty unless <see cref="Status"/> is
    /// <see cref="ClientEventSubscriptionStatus.Resolved"/>.</summary>
    public IReadOnlyList<ClientEventSubscription> Subscriptions { get; init; } = [];

    /// <summary>The shared unauthenticated failure.</summary>
    public static ClientEventSubscriptionResolution Unauthenticated { get; } =
        new() { Status = ClientEventSubscriptionStatus.Unauthenticated };

    /// <summary>The shared malformed-request failure.</summary>
    public static ClientEventSubscriptionResolution InvalidRequest { get; } =
        new() { Status = ClientEventSubscriptionStatus.InvalidRequest };

    /// <summary>The shared not-found failure (unknown topic and denied topic alike).</summary>
    public static ClientEventSubscriptionResolution NotFound { get; } =
        new() { Status = ClientEventSubscriptionStatus.NotFound };

    /// <summary>A successful resolution carrying <paramref name="subscriptions"/>.</summary>
    public static ClientEventSubscriptionResolution Resolved(IReadOnlyList<ClientEventSubscription> subscriptions) =>
        new() { Status = ClientEventSubscriptionStatus.Resolved, Subscriptions = subscriptions };
}
