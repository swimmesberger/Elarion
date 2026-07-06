using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Elarion.Actors.Diagnostics;

/// <summary>
/// Central OpenTelemetry-compatible instrumentation for actor calls and activations.
/// </summary>
/// <remarks>
/// Signals are collected only when a host registers <see cref="ActivitySourceName"/> and
/// <see cref="MeterName"/> with its OpenTelemetry providers; without listeners every call site is a
/// null-activity no-op. The caller-side span (<c>actor.call</c>) parents the actor-side span
/// (<c>actor.process</c>), so a trace crosses the mailbox boundary like an RPC hop. Exceptions are
/// not wrapped by the runtime: a facade await rethrows the actor-side exception with its original
/// stack trace, and the actor-side span records it.
/// </remarks>
public static class ActorTelemetry {
    /// <summary>The activity source name to register with OpenTelemetry tracing.</summary>
    public const string ActivitySourceName = "Elarion.Actors";

    /// <summary>The meter name to register with OpenTelemetry metrics.</summary>
    public const string MeterName = "Elarion.Actors";

    /// <summary>Shared activity source used for actor call/process spans.</summary>
    public static readonly ActivitySource Source = new(ActivitySourceName);

    private static readonly Meter MeterInstance = new(MeterName);

    /// <summary>Counts processed actor messages by actor, method, and outcome.</summary>
    public static readonly Counter<long> MessageCount =
        MeterInstance.CreateCounter<long>(
            "actor.message.count",
            description: "Total number of processed actor messages");

    /// <summary>Records actor message execution duration in milliseconds (excluding queue wait).</summary>
    public static readonly Histogram<double> MessageDuration =
        MeterInstance.CreateHistogram<double>(
            "actor.message.duration",
            unit: "ms",
            description: "Execution duration of actor messages");

    /// <summary>Records time a message spent queued in the mailbox before execution started.</summary>
    public static readonly Histogram<double> MessageQueueWait =
        MeterInstance.CreateHistogram<double>(
            "actor.message.queue_wait",
            unit: "ms",
            description: "Time actor messages spent waiting in the mailbox");

    /// <summary>Tracks currently live activations by actor.</summary>
    public static readonly UpDownCounter<long> ActiveActivations =
        MeterInstance.CreateUpDownCounter<long>(
            "actor.activations.active",
            description: "Number of currently live actor activations");

    /// <summary>Counts activations by actor.</summary>
    public static readonly Counter<long> ActivationCount =
        MeterInstance.CreateCounter<long>(
            "actor.activations.count",
            description: "Total number of actor activations");

    /// <summary>Counts activations that failed to start (constructor or OnActivateAsync threw).</summary>
    public static readonly Counter<long> ActivationFailureCount =
        MeterInstance.CreateCounter<long>(
            "actor.activations.failed",
            description: "Total number of actor activations that failed to start");

    /// <summary>Tracks messages currently enqueued or executing per actor (mailbox depth — the backpressure signal).</summary>
    public static readonly UpDownCounter<long> MailboxPending =
        MeterInstance.CreateUpDownCounter<long>(
            "actor.mailbox.pending",
            description: "Number of actor messages currently enqueued or executing");

    // The key rides on spans only, never on metrics: span attributes tolerate high cardinality,
    // metric tags do not.
    internal static Activity? StartCall(string actor, string method, object key) {
        var activity = Source.StartActivity($"actor.call {actor}.{method}", ActivityKind.Client);
        if (activity is not null) {
            activity.SetTag("elarion.actor", actor);
            activity.SetTag("elarion.actor.method", method);
            activity.SetTag("elarion.actor.key", key.ToString());
        }

        return activity;
    }

    internal static Activity? StartProcess(string actor, string method, object? key, ActivityContext parent) {
        var activity = Source.StartActivity($"actor.process {actor}.{method}", ActivityKind.Internal, parent);
        if (activity is not null) {
            activity.SetTag("elarion.actor", actor);
            activity.SetTag("elarion.actor.method", method);
            if (key is not null) {
                activity.SetTag("elarion.actor.key", key.ToString());
            }
        }

        return activity;
    }

    internal static void RecordMessage(string actor, string method, string outcome, double elapsedMilliseconds) {
        var tags = new TagList {
            { "elarion.actor", actor },
            { "elarion.actor.method", method },
            { "elarion.actor.outcome", outcome }
        };
        MessageCount.Add(1, tags);
        MessageDuration.Record(elapsedMilliseconds, tags);
    }

    internal static void RecordQueueWait(string actor, string method, double elapsedMilliseconds) {
        var tags = new TagList {
            { "elarion.actor", actor },
            { "elarion.actor.method", method }
        };
        MessageQueueWait.Record(elapsedMilliseconds, tags);
    }

    internal static void RecordActivation(string actor) {
        var tags = new TagList { { "elarion.actor", actor } };
        ActivationCount.Add(1, tags);
        ActiveActivations.Add(1, tags);
    }

    internal static void RecordDeactivation(string actor) {
        var tags = new TagList { { "elarion.actor", actor } };
        ActiveActivations.Add(-1, tags);
    }

    internal static void RecordActivationFailure(string actor) {
        var tags = new TagList { { "elarion.actor", actor } };
        ActivationFailureCount.Add(1, tags);
    }

    internal static void RecordEnqueued(string actor) {
        var tags = new TagList { { "elarion.actor", actor } };
        MailboxPending.Add(1, tags);
    }

    internal static void RecordDequeued(string actor) {
        var tags = new TagList { { "elarion.actor", actor } };
        MailboxPending.Add(-1, tags);
    }
}
