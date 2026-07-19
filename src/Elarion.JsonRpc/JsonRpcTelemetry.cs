using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Elarion.JsonRpc;

/// <summary>
/// Central OpenTelemetry instrumentation for JSON-RPC processing.
/// Register <see cref="ActivitySourceName"/> and <see cref="MeterName"/> with
/// the OTel tracer/meter providers to capture JSON-RPC spans and metrics.
/// </summary>
/// <remarks>
/// Follows the
/// <see href="https://opentelemetry.io/docs/specs/semconv/rpc/json-rpc/">OTel JSON-RPC semantic conventions</see>.
/// </remarks>
public static class JsonRpcTelemetry {
    /// <summary>The logical name used to register the trace source with OpenTelemetry.</summary>
    public const string ActivitySourceName = "JsonRpc";

    /// <summary>The logical name used to register the meter with OpenTelemetry.</summary>
    public const string MeterName = "JsonRpc";

    /// <summary>Shared <see cref="System.Diagnostics.ActivitySource"/> — thread-safe, singleton lifetime.</summary>
    public static readonly ActivitySource Source = new(ActivitySourceName);

    private static readonly Meter MeterInstance = new(MeterName);

    // OTel semconv duration buckets (seconds). Without explicit advice the SDK's default
    // boundaries are millisecond-scaled and useless for second-valued histograms.
    private static readonly InstrumentAdvice<double> DurationAdvice = new() {
        HistogramBucketBoundaries = [0.005, 0.01, 0.025, 0.05, 0.075, 0.1, 0.25, 0.5, 0.75, 1, 2.5, 5, 7.5, 10]
    };

    /// <summary>
    /// Counts the total number of JSON-RPC requests handled.
    /// Tagged with <c>rpc.system.name</c>, <c>rpc.method</c>, and <c>rpc.response.status_code</c>.
    /// </summary>
    public static readonly Counter<long> RequestCount =
        MeterInstance.CreateCounter<long>(
            "rpc.server.request.count",
            description: "Total number of JSON-RPC requests handled");

    /// <summary>
    /// Records the duration of JSON-RPC request handling in seconds.
    /// Tagged with <c>rpc.system.name</c>, <c>rpc.method</c>, and <c>rpc.response.status_code</c>.
    /// </summary>
    /// <remarks>
    /// Named per the current OTel RPC conventions (<c>rpc.server.call.duration</c>, seconds); the
    /// retired experimental name <c>rpc.server.duration</c> was defined in milliseconds and is not
    /// emitted anymore.
    /// </remarks>
    public static readonly Histogram<double> RequestDuration =
        MeterInstance.CreateHistogram<double>(
            "rpc.server.call.duration",
            "s",
            "Duration of JSON-RPC request handling",
            advice: DurationAdvice);

    /// <summary>Records the common request count and duration metrics for one JSON-RPC outcome.</summary>
    public static void RecordRequest(string method, string statusCode, TimeSpan elapsed) {
        RecordRequest(method, statusCode, elapsed, "jsonrpc");
    }

    /// <summary>
    /// Records the common request count and duration metrics for one RPC outcome, tagged with the given
    /// <c>rpc.system.name</c>. MCP tool calls share this meter with <c>system: "mcp"</c> so both adapters over the
    /// shared handler bus are collected under one registration.
    /// </summary>
    public static void RecordRequest(string method, string statusCode, TimeSpan elapsed, string system) {
        var tags = new TagList {
            { "rpc.system.name", system },
            { "rpc.method", NormalizeMethod(method) },
            { "rpc.response.status_code", statusCode }
        };
        RequestCount.Add(1, tags);
        RequestDuration.Record(elapsed.TotalSeconds, tags);
    }

    internal static string NormalizeMethod(string? method) {
        return string.IsNullOrWhiteSpace(method) ? "_unknown" : method;
    }
}
