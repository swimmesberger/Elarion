using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Elarion.Diagnostics;

/// <summary>
/// Central OpenTelemetry-compatible instrumentation for opt-in handler execution tracing.
/// </summary>
/// <remarks>
/// The handler registration generator wraps every generated handler in a
/// <see cref="TracingDecorator{TRequest,TResponse}"/>, but signals are collected only when a host
/// registers <see cref="ActivitySourceName"/> and <see cref="MeterName"/> with the OpenTelemetry
/// providers. Runtime packages do not depend on the OpenTelemetry SDK.
/// </remarks>
public static class HandlerTelemetry {
    /// <summary>The logical activity source name to register with OpenTelemetry tracing.</summary>
    public const string ActivitySourceName = "Elarion.Handlers";

    /// <summary>The logical meter name to register with OpenTelemetry metrics.</summary>
    public const string MeterName = "Elarion.Handlers";

    /// <summary>Shared activity source used by traced handler invocations.</summary>
    public static readonly ActivitySource Source = new(ActivitySourceName);

    private static readonly Meter MeterInstance = new(MeterName);

    // OTel semconv duration buckets (seconds). Without explicit advice the SDK's default
    // boundaries are millisecond-scaled and useless for second-valued histograms.
    private static readonly InstrumentAdvice<double> DurationAdvice = new() {
        HistogramBucketBoundaries = [0.005, 0.01, 0.025, 0.05, 0.075, 0.1, 0.25, 0.5, 0.75, 1, 2.5, 5, 7.5, 10]
    };

    /// <summary>Counts traced handler executions by handler and outcome.</summary>
    public static readonly Counter<long> ExecutionCount =
        MeterInstance.CreateCounter<long>(
            "handler.execution.count",
            description: "Total number of traced handler executions");

    /// <summary>Records traced handler execution duration in seconds.</summary>
    public static readonly Histogram<double> ExecutionDuration =
        MeterInstance.CreateHistogram<double>(
            "handler.execution.duration",
            unit: "s",
            description: "Duration of traced handler executions",
            advice: DurationAdvice);

    /// <summary>Counts authorization denials by handler and outcome (<c>unauthorized</c>/<c>forbidden</c>).</summary>
    public static readonly Counter<long> AuthorizationDeniedCount =
        MeterInstance.CreateCounter<long>(
            "handler.authorization.denied.count",
            description: "Total number of handler executions denied by the authorization gate");

    /// <summary>Counts handler executions short-circuited by a closed feature gate.</summary>
    public static readonly Counter<long> FeatureGateClosedCount =
        MeterInstance.CreateCounter<long>(
            "handler.feature_gate.closed.count",
            description: "Total number of handler executions short-circuited by a closed feature gate");

    /// <summary>Counts idempotency-key resolutions by request type and outcome.</summary>
    public static readonly Counter<long> IdempotencyCount =
        MeterInstance.CreateCounter<long>(
            "handler.idempotency.count",
            description: "Total number of idempotent handler executions by key outcome");

    /// <summary>Records a traced handler execution metric tagged with bounded handler name and outcome.</summary>
    public static void RecordExecution(string handler, string outcome, TimeSpan elapsed) {
        var tags = new TagList {
            { "elarion.handler", handler },
            { "elarion.handler.outcome", outcome }
        };
        ExecutionCount.Add(1, tags);
        ExecutionDuration.Record(elapsed.TotalSeconds, tags);
    }

    /// <summary>Records one authorization denial tagged with the bounded handler name and outcome.</summary>
    public static void RecordAuthorizationDenied(string handler, string outcome) =>
        AuthorizationDeniedCount.Add(1, new TagList {
            { "elarion.handler", handler },
            { "elarion.authorization.outcome", outcome }
        });

    /// <summary>Records one feature-gate short-circuit tagged with the bounded handler name.</summary>
    public static void RecordFeatureGateClosed(string handler) =>
        FeatureGateClosedCount.Add(1, new TagList { { "elarion.handler", handler } });

    /// <summary>Records one idempotency-key resolution tagged with the bounded request type name and outcome.</summary>
    public static void RecordIdempotency(string requestType, string outcome) =>
        IdempotencyCount.Add(1, new TagList {
            { "elarion.handler.request_type", requestType },
            { "elarion.idempotency.outcome", outcome }
        });
}
