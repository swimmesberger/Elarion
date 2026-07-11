using System.Text.Json.Serialization.Metadata;
using Elarion.Abstractions.Modules;
using LiveQuotes.Api.Modules.Market.Feed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LiveQuotes.Api.Modules.Market;

/// <summary>
/// The market-data module. Everything realtime lives under it — the actors, the feed, the client-event
/// topic, the query handlers — so the <b>deployment topology is one config switch</b>: on a multi-node
/// setup, enable the module only on the worker instance (<c>Modules:Market:Enabled=false</c> on web
/// nodes) and the feed, the actors, and the <c>/quotes</c> endpoints exist only there.
/// </summary>
[AppModule("Market")]
public static partial class MarketModule {
    public static IJsonTypeInfoResolver GetJsonTypeInfoResolver() => MarketJsonContext.Default;

    /// <summary>Non-generated registrations: the simulated feed and its options.</summary>
    public static void ConfigureServices(IServiceCollection services, IConfiguration configuration) {
        var section = configuration.GetSection("Market");
        var symbols = section.GetSection(nameof(MarketFeedOptions.Symbols))
            .GetChildren()
            .Select(child => child.Value)
            .OfType<string>()
            .ToArray();
        var options = new MarketFeedOptions {
            Symbols = symbols.Length > 0 ? symbols : MarketFeedOptions.DefaultSymbols,
            TicksPerSecondPerSymbol =
                int.TryParse(section[nameof(MarketFeedOptions.TicksPerSecondPerSymbol)], out var rate)
                    ? rate
                    : new MarketFeedOptions().TicksPerSecondPerSymbol
        };

        services.AddSingleton(options);
        // The feed is a host-managed hosted service (never a fire-and-forget loop): it owns the
        // upstream connection and pumps ticks into the actors for the process lifetime.
        services.AddHostedService<MarketFeedService>();
    }
}
