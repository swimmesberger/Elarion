using Elarion.Abstractions.ClientEvents;
using Elarion.Actors;
using LiveQuotes.Api.Modules.Market.Actors;

namespace LiveQuotes.Api.Modules.Market;

/// <summary>
/// The producer-side lifecycle for <c>market.quoteChanged</c>: when a browser subscribes to a symbol, ask
/// the owning actor for the current value (a normal request/reply turn) and greet exactly that subscriber
/// with it — the <c>BehaviorSubject</c> pattern, with the actor deciding (an unprimed symbol greets with
/// nothing). Runs on the node holding the subscription, which in the recommended topology is the actor
/// home (the live prefixes — including <c>/events</c> — route there), so the facade call is local.
/// </summary>
/// <remarks>
/// The lazy-compute counterpart is the <b>pull</b> check: the actor's publish path asks
/// <c>IClientEventInterest.HasSubscribers</c> and skips the wire for unwatched symbols. This sample needs no
/// <c>OnInterestChangedAsync</c> — the simulated feed is shared across all symbols; implement it when the
/// first/last watcher should open/close a per-resource upstream (its departure signal is linger-debounced,
/// so a browser reload never bounces the connection).
/// </remarks>
public sealed class MarketSubscriptionObserver(IActorSystem actors) : IClientEventSubscriptionObserver {
    public async ValueTask OnSubscribedAsync(
        ClientEventSubscription subscription, IClientEventSubscriberSink sink, CancellationToken ct) {
        // Only symbol subscriptions get a greeting; the endpoint's implicit global/user entries carry none.
        if (subscription.Scope is not { Kind: ClientEventScopeKind.Resource, Value: { Length: > 0 } symbol }) return;

        var quote = await actors.Get<IStockQuote>(symbol.ToUpperInvariant()).GetQuote(ct);
        if (quote is null) return;

        await sink.PublishAsync(new QuoteChanged {
            Symbol = quote.Symbol,
            Price = quote.Price,
            ChangePercent = quote.ChangePercent,
            Seq = quote.Seq,
            At = quote.At
        }, ct);
    }
}
