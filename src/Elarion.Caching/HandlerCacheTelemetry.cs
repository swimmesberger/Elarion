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

    // OTel semconv duration buckets (seconds). Without explicit advice the SDK's default
    // boundaries are millisecond-scaled and useless for second-valued histograms.
    private static readonly InstrumentAdvice<double> DurationAdvice = new() {
        HistogramBucketBoundaries = [0.005, 0.01, 0.025, 0.05, 0.075, 0.1, 0.25, 0.5, 0.75, 1, 2.5, 5, 7.5, 10]
    };

    /// <summary>Counts handler cache operations by operation and outcome.</summary>
    public static readonly Counter<long> OperationCount =
        MeterInstance.CreateCounter<long>(
            "handler.cache.operation.count",
            description: "Total number of handler cache operations");

    /// <summary>Records handler cache operation duration in seconds.</summary>
    public static readonly Histogram<double> OperationDuration =
        MeterInstance.CreateHistogram<double>(
            "handler.cache.operation.duration",
            unit: "s",
            description: "Duration of handler cache operations",
            advice: DurationAdvice);

    internal static void RecordOperation(string operation, string outcome, TimeSpan elapsed) {
        var tags = new TagList {
            { "handler.cache.operation", operation },
            { "handler.cache.outcome", outcome }
        };
        OperationCount.Add(1, tags);
        OperationDuration.Record(elapsed.TotalSeconds, tags);
    }
}
