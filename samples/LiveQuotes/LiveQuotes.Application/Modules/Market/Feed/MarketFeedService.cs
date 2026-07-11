using Elarion.Actors;
using LiveQuotes.Application.Modules.Market.Actors;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LiveQuotes.Application.Modules.Market.Feed;

/// <summary>Configures the simulated feed (read from the <c>Market</c> configuration section).</summary>
public sealed class MarketFeedOptions {
    /// <summary>The default symbol set, applied when configuration names none.</summary>
    public static readonly string[] DefaultSymbols = ["ELN", "ACME", "INIT", "MBOX", "SNAP"];

    /// <summary>The symbols the feed produces.</summary>
    public string[] Symbols { get; set; } = [];

    /// <summary>Ticks produced per symbol per second. At the default the whole feed runs well past
    /// what any dashboard needs — which is the point: the actor conflates it back down.</summary>
    public int TicksPerSecondPerSymbol { get; set; } = 20;
}

/// <summary>
/// The feed. In a real system this class owns the vendor connection — the proprietary TCP client, the
/// login/heartbeat, the snapshot-then-increments protocol — and this is the <b>only</b> file that would
/// change: parse a tick, call the typed facade. Here it simulates one with a random walk per symbol.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ticks are in-process facade calls, deliberately not integration events.</b> The outbox writes
/// every event to the database — exactly the per-tick write load this design exists to avoid — and adds
/// delivery latency. Integration events are for durable business facts; a market tick is neither. The
/// feed and the actors are pinned to the same instance by module gating, so the call is always local.
/// </para>
/// <para>
/// Each symbol carries its own monotonically increasing sequence number, the property a multi-channel
/// vendor feed provides: the actor's mailbox serializes processing, the sequence number lets the actor
/// drop anything delivered out of order.
/// </para>
/// </remarks>
internal sealed class MarketFeedService(
    IActorSystem actors,
    MarketFeedOptions options,
    TimeProvider timeProvider,
    ILogger<MarketFeedService> logger) : BackgroundService {
    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        logger.LogInformation(
            "Simulated market feed: {Symbols} at {Rate} ticks/s each.",
            string.Join(", ", options.Symbols), options.TicksPerSecondPerSymbol);

        var prices = options.Symbols.ToDictionary(s => s, _ => 50m + (decimal)(Random.Shared.NextDouble() * 200));
        var sequences = options.Symbols.ToDictionary(s => s, _ => 0L);
        var interval = TimeSpan.FromSeconds(1.0 / Math.Max(1, options.TicksPerSecondPerSymbol));
        using var timer = new PeriodicTimer(interval, timeProvider);

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false)) {
            foreach (var symbol in options.Symbols) {
                // Random walk: ±0.2% per tick.
                var price = prices[symbol];
                price = Math.Round(Math.Max(0.01m, price * (1m + (decimal)(Random.Shared.NextDouble() - 0.5) * 0.004m)), 2);
                prices[symbol] = price;

                var tick = new QuoteTick(++sequences[symbol], price, timeProvider.GetUtcNow());
                await actors.Get<IStockQuote>(symbol).Apply(tick, stoppingToken).ConfigureAwait(false);
            }
        }
    }
}
