using Elarion.Abstractions;
using Elarion.Actors;
using LiveQuotes.Api.Modules.Market.Actors;
using LiveQuotes.Api.Modules.Market.Feed;

namespace LiveQuotes.Api.Modules.Market.Handlers;

/// <summary>All configured symbols' current values — the initial page load. Each read is one mailbox
/// hop; at a configuration-sized symbol list that is exactly as cheap as it sounds.</summary>
[Handler("market.quotes")]
[HttpEndpoint("quotes")]
public sealed class ListQuotes(IActorSystem actors, MarketFeedOptions options)
    : IHandler<ListQuotes.Query, Result<ListQuotes.Response>> {
    public sealed record Query : IQuery;
    public sealed record Response(IReadOnlyList<Quote> Quotes);

    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        var quotes = new List<Quote>(options.Symbols.Length);
        foreach (var symbol in options.Symbols) {
            if (await actors.Get<IStockQuote>(symbol).GetQuote(ct) is { } quote) {
                quotes.Add(quote);
            }
        }

        return new Response(quotes);
    }
}
