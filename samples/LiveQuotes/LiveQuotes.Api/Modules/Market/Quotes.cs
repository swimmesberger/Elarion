using Elarion.Abstractions.ClientEvents;

namespace LiveQuotes.Api.Modules.Market;

/// <summary>One update from the (simulated) upstream feed. The feed's sequence number travels with the
/// price: the actor's mailbox serializes <em>processing</em>, the sequence number preserves the feed's
/// own <em>order</em> when ticks for one symbol can arrive over more than one channel.</summary>
public sealed record QuoteTick(long Seq, decimal Price, DateTimeOffset At);

/// <summary>The current state of one symbol — what queries return.</summary>
public sealed record Quote(string Symbol, decimal Price, decimal ChangePercent, long Seq, DateTimeOffset At);

/// <summary>
/// The realtime push contract, topic <c>market.quoteChanged</c> (inferred from module + type name).
/// Published <b>resource-scoped per symbol</b>, so a browser subscribes to exactly the symbols it
/// displays. This is the ephemeral client-event tier: the payload carries the value itself, because
/// there is deliberately nothing in a database to re-query — a missed event is superseded by the next
/// one (at-most-once by design). On (re)connect the client fetches current values from
/// <c>GET /quotes</c> and then applies pushes: converge, then stream.
/// </summary>
public sealed record QuoteChanged : IClientEvent {
    public required string Symbol { get; init; }

    public required decimal Price { get; init; }

    public required decimal ChangePercent { get; init; }

    public required long Seq { get; init; }

    public required DateTimeOffset At { get; init; }
}
