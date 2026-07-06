using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Elarion.Resilience;

/// <summary>
/// Central OpenTelemetry-compatible instrumentation for Elarion resilience policy execution.
/// </summary>
public static class ResilienceTelemetry {
    /// <summary>The logical activity source name to register with OpenTelemetry tracing.</summary>
    public const string ActivitySourceName = "Elarion.Resilience";

    /// <summary>The logical meter name to register with OpenTelemetry metrics.</summary>
    public const string MeterName = "Elarion.Resilience";

    /// <summary>Shared activity source used by resilience policy execution.</summary>
    public static readonly ActivitySource Source = new(ActivitySourceName);

    private static readonly Meter MeterInstance = new(MeterName);

    // OTel semconv duration buckets (seconds). Without explicit advice the SDK's default
    // boundaries are millisecond-scaled and useless for second-valued histograms.
    private static readonly InstrumentAdvice<double> DurationAdvice = new() {
        HistogramBucketBoundaries = [0.005, 0.01, 0.025, 0.05, 0.075, 0.1, 0.25, 0.5, 0.75, 1, 2.5, 5, 7.5, 10]
    };

    /// <summary>Counts resilience policy executions by policy and outcome.</summary>
    public static readonly Counter<long> ExecutionCount =
        MeterInstance.CreateCounter<long>(
            "resilience.policy.execution.count",
            description: "Total number of resilience policy executions");

    /// <summary>Records resilience policy execution duration in seconds.</summary>
    public static readonly Histogram<double> ExecutionDuration =
        MeterInstance.CreateHistogram<double>(
            "resilience.policy.execution.duration",
            unit: "s",
            description: "Duration of resilience policy executions",
            advice: DurationAdvice);

    internal static void RecordExecution(string policyName, string outcome, TimeSpan elapsed) {
        var tags = new TagList {
            { "resilience.policy.name", policyName },
            { "resilience.policy.outcome", outcome }
        };
        ExecutionCount.Add(1, tags);
        ExecutionDuration.Record(elapsed.TotalSeconds, tags);
    }
}
