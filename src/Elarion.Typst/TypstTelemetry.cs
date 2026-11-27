using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Elarion.Typst;

/// <summary>OpenTelemetry-compatible instrumentation for Typst compilations.</summary>
/// <remarks>
/// Metric tags stay bounded: the outcome only. The document (package function) is recorded on the span, where its
/// cardinality does not matter.
/// </remarks>
public static class TypstTelemetry {
    /// <summary>The activity source name to register with OpenTelemetry tracing.</summary>
    public const string ActivitySourceName = "Elarion.Typst";

    /// <summary>The meter name to register with OpenTelemetry metrics.</summary>
    public const string MeterName = "Elarion.Typst";

    internal const string OutcomeTag = "elarion.typst.outcome";
    internal const string DocumentTag = "elarion.typst.document";

    internal static readonly ActivitySource Source = new(ActivitySourceName);

    private static readonly Meter MeterInstance = new(MeterName);

    // Typst compiles a typical business document in well under a second, a large report in a few seconds; the timeout
    // defaults to a minute. Explicit second-scaled buckets: the SDK defaults are millisecond-scaled.
    private static readonly InstrumentAdvice<double> DurationAdvice = new() {
        HistogramBucketBoundaries = [0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 0.75, 1, 2.5, 5, 7.5, 10, 30, 60]
    };

    /// <summary>Duration of a Typst compilation in seconds, from process start to exit, by outcome.</summary>
    public static readonly Histogram<double> CompileDuration = MeterInstance.CreateHistogram<double>(
        "elarion.typst.compile.duration", "s", "Duration of Typst compilations", advice: DurationAdvice);

    /// <summary>
    /// Time a compilation waited for a free slot (<see cref="TypstOptions.MaxConcurrency"/>), in seconds. A rising
    /// value means documents are requested faster than the configured concurrency renders them.
    /// </summary>
    public static readonly Histogram<double> QueueDuration = MeterInstance.CreateHistogram<double>(
        "elarion.typst.compile.queue.duration", "s", "Time Typst compilations waited for a concurrency slot",
        advice: DurationAdvice);

    internal static void RecordCompile(string outcome, TimeSpan elapsed) =>
        CompileDuration.Record(elapsed.TotalSeconds, new KeyValuePair<string, object?>(OutcomeTag, outcome));

    internal static void RecordQueue(TimeSpan elapsed) => QueueDuration.Record(elapsed.TotalSeconds);
}

internal static class TypstOutcome {
    public const string Success = "success";
    public const string CompilationError = "compilation_error";
    public const string Timeout = "timeout";
    public const string Unavailable = "unavailable";
    public const string PackageNotFound = "package_not_found";
    public const string Cancelled = "cancelled";
}
