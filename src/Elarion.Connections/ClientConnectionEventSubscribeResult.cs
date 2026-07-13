using Elarion.ClientEvents;

namespace Elarion.Connections;

/// <summary>
/// The outcome of <see cref="ClientConnectionEventBridge.SubscribeAsync"/>: the resolver's status, plus the
/// live subscription when it resolved. Adapters map the failure statuses onto their protocol the way the
/// SSE endpoint maps them onto HTTP (401 / 400 / 404) — keeping the not-found indistinguishability intact.
/// </summary>
public sealed record ClientConnectionEventSubscribeResult {
    /// <summary>The subscribe pipeline's outcome.</summary>
    public required ClientEventSubscriptionStatus Status { get; init; }

    /// <summary>The live subscription; non-null exactly when <see cref="Status"/> is
    /// <see cref="ClientEventSubscriptionStatus.Resolved"/>.</summary>
    public ClientConnectionEventSubscription? Subscription { get; init; }
}
