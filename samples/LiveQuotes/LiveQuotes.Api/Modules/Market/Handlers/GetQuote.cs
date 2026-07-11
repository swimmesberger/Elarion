using Elarion.Abstractions;
using Elarion.Actors;
using LiveQuotes.Api.Modules.Market.Actors;

namespace LiveQuotes.Api.Modules.Market.Handlers;

/// <summary>
/// The pull-based read of one symbol — the "converge" half of converge-then-stream: the demo page calls
/// it on load and on the SSE <c>elarion.connected</c> reconnect hint, then applies pushed updates. The
/// facade read crosses the actor's mailbox, so it is serialized with the feed like everything else.
/// Because this handler lives in the Market module, on a multi-node deployment it exists only on the
/// worker instance — route <c>/quotes/*</c> there at the ingress; web nodes serve everything else.
/// </summary>
[Handler("market.quote")]
[HttpEndpoint("quotes/{symbol}")]
public sealed class GetQuote(IActorSystem actors) : IHandler<GetQuote.Query, Result<GetQuote.Response>> {
    public sealed record Query(string Symbol) : IQuery;
    public sealed record Response(Quote Quote);

    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        var quote = await actors.Get<IStockQuote>(query.Symbol.ToUpperInvariant()).GetQuote(ct);
        return quote is null
            ? AppError.NotFound($"No live quote for '{query.Symbol}' (unknown symbol, or the feed has not primed it yet).")
            : new Response(quote);
    }
}
