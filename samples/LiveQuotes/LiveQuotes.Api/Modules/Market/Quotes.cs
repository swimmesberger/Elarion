using Elarion.Abstractions.ClientEvents;

namespace LiveQuotes.Api.Modules.Market;

/// <summary>One update from the (simulated) upstream feed. The feed's sequence number travels with the
/// price: the actor's mailbox serializes <em>processing</em>, the sequence number preserves the feed's
/// own <em>order</em> when ticks for one symbol can arrive over more than one channel.</summary>
public sealed record QuoteTick(long Seq, decimal Price, DateTimeOffset At);

/// <summary>The current state of one symbol — what queries return.</summary>
public sealed record Quote(string Symbol, decimal Price, decimal ChangePercent, long Seq, DateTimeOffset At);

/// <summary>
/// The realtime push contract. Published <b>resource-scoped per symbol</b>, so a browser subscribes to
/// exactly the symbols it displays. <c>[AllowAnyResource]</c> declares the symbol a routing key, not an
/// entitlement: any authenticated user may watch any symbol, and no
/// <c>IClientEventSubscriptionAuthorizer</c> is needed — that seam stays reserved for topics whose
/// resource genuinely gates access (per-tenant data). <c>[SubscriptionObserver]</c> wires the
/// producer-side lifecycle: every new subscriber is greeted with the symbol's current value (the stream
/// is self-converging — "last known value + everything since", including after a reconnect), and the
/// actor's publish path checks <c>IClientEventInterest</c> so unwatched symbols cost nothing on the wire.
/// This is the ephemeral client-event tier: the payload carries the value itself; a missed event is
/// superseded by the next one (at-most-once by design), and the <c>Seq</c> the client guards on resolves
/// any greeting-vs-live-push interleaving.
/// </summary>
[ClientEvent(Topic)]
[AllowAnyResource]
[SubscriptionObserver<MarketSubscriptionObserver>]
public sealed record QuoteChanged : IClientEvent {
    /// <summary>The wire topic; the <c>[ClientEvent]</c> override pins it to this const so the actor's
    /// interest check and the generated registration can never drift apart.</summary>
    public const string Topic = "market.quoteChanged";

    public required string Symbol { get; init; }

    public required decimal Price { get; init; }

    public required decimal ChangePercent { get; init; }

    public required long Seq { get; init; }

    public required DateTimeOffset At { get; init; }
}
