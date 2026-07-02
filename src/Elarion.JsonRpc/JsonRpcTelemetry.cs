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

    /// <summary>
    /// Counts the total number of JSON-RPC requests handled.
    /// Tagged with <c>rpc.system.name</c>, <c>rpc.method</c>, and <c>rpc.response.status_code</c>.
    /// </summary>
    public static readonly Counter<long> RequestCount =
        MeterInstance.CreateCounter<long>(
            "rpc.server.request.count",
            description: "Total number of JSON-RPC requests handled");

    /// <summary>
    /// Records the duration of JSON-RPC request handling in milliseconds.
    /// Tagged with <c>rpc.system.name</c>, <c>rpc.method</c>, and <c>rpc.response.status_code</c>.
    /// </summary>
    public static readonly Histogram<double> RequestDuration =
        MeterInstance.CreateHistogram<double>(
            "rpc.server.duration",
            unit: "ms",
            description: "Duration of JSON-RPC request handling");

    /// <summary>Records the common request count and duration metrics for one JSON-RPC outcome.</summary>
    public static void RecordRequest(string method, string statusCode, double elapsedMilliseconds) =>
        RecordRequest(method, statusCode, elapsedMilliseconds, system: "jsonrpc");

    /// <summary>
    /// Records the common request count and duration metrics for one RPC outcome, tagged with the given
    /// <c>rpc.system.name</c>. MCP tool calls share this meter with <c>system: "mcp"</c> so both adapters over the
    /// shared handler bus are collected under one registration.
    /// </summary>
    public static void RecordRequest(string method, string statusCode, double elapsedMilliseconds, string system) {
        var tags = new TagList {
            { "rpc.system.name", system },
            { "rpc.method", NormalizeMethod(method) },
            { "rpc.response.status_code", statusCode }
        };
        RequestCount.Add(1, tags);
        RequestDuration.Record(elapsedMilliseconds, tags);
    }

    internal static string NormalizeMethod(string? method) =>
        string.IsNullOrWhiteSpace(method) ? "_unknown" : method;
}
