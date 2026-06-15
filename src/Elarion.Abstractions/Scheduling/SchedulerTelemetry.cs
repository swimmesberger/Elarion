using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Elarion.Abstractions.Scheduling;

/// <summary>
/// Central OpenTelemetry instrumentation for scheduled job execution.
/// </summary>
public static class SchedulerTelemetry {
    /// <summary>The logical activity source name to register with OpenTelemetry tracing.</summary>
    public const string ActivitySourceName = "Elarion.Scheduling";

    /// <summary>The logical meter name to register with OpenTelemetry metrics.</summary>
    public const string MeterName = "Elarion.Scheduling";

    /// <summary>Shared activity source used by scheduler job runs.</summary>
    public static readonly ActivitySource Source = new(ActivitySourceName);

    private static readonly Meter MeterInstance = new(MeterName);

    /// <summary>Counts scheduled job executions by job and status.</summary>
    public static readonly Counter<long> JobRunCount =
        MeterInstance.CreateCounter<long>(
            "scheduler.job.run.count",
            description: "Total number of scheduled job runs");

    /// <summary>Records scheduled job execution duration in milliseconds.</summary>
    public static readonly Histogram<double> JobRunDuration =
        MeterInstance.CreateHistogram<double>(
            "scheduler.job.run.duration",
            unit: "ms",
            description: "Duration of scheduled job runs");

    /// <summary>Records scheduling lag in milliseconds.</summary>
    public static readonly Histogram<double> JobRunLag =
        MeterInstance.CreateHistogram<double>(
            "scheduler.job.run.lag",
            unit: "ms",
            description: "Delay between due time and actual start time");

    /// <summary>Tracks the number of currently executing scheduled jobs.</summary>
    public static readonly UpDownCounter<int> ActiveJobRuns =
        MeterInstance.CreateUpDownCounter<int>(
            "scheduler.job.active",
            description: "Number of scheduled jobs currently executing");
}
