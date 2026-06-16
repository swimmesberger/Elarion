using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Elarion.Caching;

/// <summary>
/// Central OpenTelemetry-compatible instrumentation for handler cache operations.
/// </summary>
public static class HandlerCacheTelemetry {
    /// <summary>The logical activity source name to register with OpenTelemetry tracing.</summary>
    public const string ActivitySourceName = "Elarion.Caching";

    /// <summary>The logical meter name to register with OpenTelemetry metrics.</summary>
    public const string MeterName = "Elarion.Caching";

    /// <summary>Shared activity source used by handler cache operations.</summary>
    public static readonly ActivitySource Source = new(ActivitySourceName);

    private static readonly Meter MeterInstance = new(MeterName);

    /// <summary>Counts handler cache operations by operation and outcome.</summary>
    public static readonly Counter<long> OperationCount =
        MeterInstance.CreateCounter<long>(
            "handler.cache.operation.count",
            description: "Total number of handler cache operations");

    /// <summary>Records handler cache operation duration in milliseconds.</summary>
    public static readonly Histogram<double> OperationDuration =
        MeterInstance.CreateHistogram<double>(
            "handler.cache.operation.duration",
            unit: "ms",
            description: "Duration of handler cache operations");

    internal static void RecordOperation(string operation, string outcome, double elapsedMilliseconds) {
        var tags = new TagList {
            { "handler.cache.operation", operation },
            { "handler.cache.outcome", outcome }
        };
        OperationCount.Add(1, tags);
        OperationDuration.Record(elapsedMilliseconds, tags);
    }
}
