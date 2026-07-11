using System.Collections.Concurrent;
using AwesomeAssertions;
using Elarion.Abstractions.ClientEvents;
using Elarion.Actors;
using LiveQuotes.Api.Modules.Market;
using LiveQuotes.Api.Modules.Market.Actors;
using LiveQuotes.Api.Modules.Market.Feed;
using LiveQuotes.Api.Modules.Market.Handlers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace LiveQuotes.Tests;

/// <summary>
/// The realtime slice, Docker-free and clock-deterministic: ticks flow through the generated actor
/// registration; the pushed events land in a fake publisher; time comes from a
/// <see cref="FakeTimeProvider"/> so the conflation window is exact.
/// </summary>
public sealed class StockQuoteActorTests {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Conflation_PublishesAtMostOncePerWindow_LatestValueWins() {
        var time = new FakeTimeProvider();
        var published = new RecordingPublisher();
        await using var provider = BuildHost(time, published);
        var quote = provider.GetRequiredService<IActorSystem>().Get<IStockQuote>("ELN");

        // A hot burst inside one conflation window: the first tick publishes, the rest only update
        // the in-memory value.
        for (var seq = 1; seq <= 10; seq++) {
            await quote.Apply(new QuoteTick(seq, 100m + seq, time.GetUtcNow()), Ct);
        }

        published.Events.Should().HaveCount(1);
        published.Events[0].Price.Should().Be(101m); // the first tick's value opened the window

        // The window elapses: the next tick publishes the CURRENT value — every burst tick was
        // applied, none was lost, only the pushes were conflated.
        time.Advance(StockQuoteActor.PublishInterval + TimeSpan.FromMilliseconds(1));
        await quote.Apply(new QuoteTick(11, 250m, time.GetUtcNow()), Ct);

        published.Events.Should().HaveCount(2);
        published.Events[1].Price.Should().Be(250m);
        published.Events[1].Seq.Should().Be(11);
        published.Scopes.Should().AllSatisfy(scope => scope.Should().Be(ClientEventScope.Resource("ELN")));
    }

    [Fact]
    public async Task OutOfOrderTicks_AreDroppedBySequenceGuard() {
        var time = new FakeTimeProvider();
        await using var provider = BuildHost(time, new RecordingPublisher());
        var quote = provider.GetRequiredService<IActorSystem>().Get<IStockQuote>("ELN");

        await quote.Apply(new QuoteTick(5, 100m, time.GetUtcNow()), Ct);
        // A stale delivery from another feed channel: lower sequence, must not win.
        await quote.Apply(new QuoteTick(3, 50m, time.GetUtcNow()), Ct);

        var current = await quote.GetQuote(Ct);
        current!.Price.Should().Be(100m);
        current.Seq.Should().Be(5);
    }

    [Fact]
    public async Task Queries_ServeTheCurrentValue_UnprimedSymbolIsNotFound() {
        var time = new FakeTimeProvider();
        await using var provider = BuildHost(time, new RecordingPublisher());
        var actors = provider.GetRequiredService<IActorSystem>();
        await actors.Get<IStockQuote>("ELN").Apply(new QuoteTick(1, 42m, time.GetUtcNow()), Ct);

        var handler = new GetQuote(actors);
        var found = await handler.HandleAsync(new GetQuote.Query("eln"), Ct); // symbol normalization
        found.IsSuccess.Should().BeTrue();
        found.Value.Quote.Price.Should().Be(42m);

        var missing = await handler.HandleAsync(new GetQuote.Query("NOPE"), Ct);
        missing.IsSuccess.Should().BeFalse();

        var list = await new ListQuotes(actors, new MarketFeedOptions { Symbols = ["ELN", "NOPE"] })
            .HandleAsync(new ListQuotes.Query(), Ct);
        list.Value.Quotes.Should().ContainSingle().Which.Symbol.Should().Be("ELN");
    }

    private static ServiceProvider BuildHost(FakeTimeProvider time, RecordingPublisher publisher) {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(time);
        services.AddSingleton<IClientEventPublisher>(publisher);
        // The generated per-module registration — the same wiring the host's AddElarion composes.
        MarketActorExtensions.AddMarketActors(services);
        return services.BuildServiceProvider();
    }

    private sealed class RecordingPublisher : IClientEventPublisher {
        private readonly ConcurrentQueue<(QuoteChanged Event, ClientEventScope Scope)> _events = new();

        public IReadOnlyList<QuoteChanged> Events => [.. _events.Select(e => e.Event)];

        public IReadOnlyList<ClientEventScope> Scopes => [.. _events.Select(e => e.Scope)];

        public ValueTask PublishAsync<TEvent>(TEvent @event, ClientEventScope scope, CancellationToken ct = default)
            where TEvent : class, IClientEvent {
            _events.Enqueue(((QuoteChanged)(object)@event, scope));
            return ValueTask.CompletedTask;
        }
    }
}
