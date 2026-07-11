using System.Text.Json.Serialization;
using LiveQuotes.Application.Modules.Market.Handlers;

namespace LiveQuotes.Application.Modules.Market;

// Reflection-free serialization for everything this module puts on a wire: the handler DTOs and the
// client-event payload (QuoteChanged rides the SSE stream as canonical JSON).
[JsonSerializable(typeof(GetQuote.Query), TypeInfoPropertyName = "GetQuoteQuery")]
[JsonSerializable(typeof(GetQuote.Response), TypeInfoPropertyName = "GetQuoteResponse")]
[JsonSerializable(typeof(ListQuotes.Query), TypeInfoPropertyName = "ListQuotesQuery")]
[JsonSerializable(typeof(ListQuotes.Response), TypeInfoPropertyName = "ListQuotesResponse")]
[JsonSerializable(typeof(QuoteChanged))]
public sealed partial class MarketJsonContext : JsonSerializerContext;
