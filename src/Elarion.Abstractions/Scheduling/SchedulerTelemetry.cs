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

    // OTel semconv duration buckets (seconds). Without explicit advice the SDK's default
    // boundaries are millisecond-scaled and useless for second-valued histograms.
    private static readonly InstrumentAdvice<double> DurationAdvice = new() {
        HistogramBucketBoundaries = [0.005, 0.01, 0.025, 0.05, 0.075, 0.1, 0.25, 0.5, 0.75, 1, 2.5, 5, 7.5, 10]
    };

    /// <summary>Counts scheduled job executions by job and status.</summary>
    public static readonly Counter<long> JobRunCount =
        MeterInstance.CreateCounter<long>(
            "scheduler.job.run.count",
            description: "Total number of scheduled job runs");

    /// <summary>Records scheduled job execution duration in seconds.</summary>
    public static readonly Histogram<double> JobRunDuration =
        MeterInstance.CreateHistogram<double>(
            "scheduler.job.run.duration",
            "s",
            "Duration of scheduled job runs",
            advice: DurationAdvice);

    /// <summary>Records scheduling lag in seconds.</summary>
    public static readonly Histogram<double> JobRunLag =
        MeterInstance.CreateHistogram<double>(
            "scheduler.job.run.lag",
            "s",
            "Delay between due time and actual start time",
            advice: DurationAdvice);

    /// <summary>Tracks the number of currently executing scheduled jobs.</summary>
    public static readonly UpDownCounter<int> ActiveJobRuns =
        MeterInstance.CreateUpDownCounter<int>(
            "scheduler.job.active",
            description: "Number of scheduled jobs currently executing");

    /// <summary>Counts scheduler control-plane operations by operation and outcome.</summary>
    public static readonly Counter<long> OperationCount =
        MeterInstance.CreateCounter<long>(
            "scheduler.operation.count",
            description: "Total number of scheduler control-plane operations");

    /// <summary>Records scheduler control-plane operation duration in seconds.</summary>
    public static readonly Histogram<double> OperationDuration =
        MeterInstance.CreateHistogram<double>(
            "scheduler.operation.duration",
            "s",
            "Duration of scheduler control-plane operations",
            advice: DurationAdvice);

    /// <summary>Records a scheduler control-plane operation metric.</summary>
    public static void RecordOperation(string operation, string outcome, TimeSpan elapsed) {
        var tags = new TagList {
            { "scheduler.operation", operation },
            { "scheduler.operation.outcome", outcome }
        };
        OperationCount.Add(1, tags);
        OperationDuration.Record(elapsed.TotalSeconds, tags);
    }
}
