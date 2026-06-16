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

    /// <summary>Counts resilience policy executions by policy and outcome.</summary>
    public static readonly Counter<long> ExecutionCount =
        MeterInstance.CreateCounter<long>(
            "resilience.policy.execution.count",
            description: "Total number of resilience policy executions");

    /// <summary>Records resilience policy execution duration in milliseconds.</summary>
    public static readonly Histogram<double> ExecutionDuration =
        MeterInstance.CreateHistogram<double>(
            "resilience.policy.execution.duration",
            unit: "ms",
            description: "Duration of resilience policy executions");

    internal static void RecordExecution(string policyName, string outcome, double elapsedMilliseconds) {
        var tags = new TagList {
            { "resilience.policy.name", policyName },
            { "resilience.policy.outcome", outcome }
        };
        ExecutionCount.Add(1, tags);
        ExecutionDuration.Record(elapsedMilliseconds, tags);
    }
}
