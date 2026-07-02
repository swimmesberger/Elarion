using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Elarion.Abstractions.Messaging;

/// <summary>
/// Central OpenTelemetry-compatible instrumentation for event publishing and consumer dispatch.
/// </summary>
/// <remarks>
/// Both planes and every delivery tier (inline domain dispatch, the in-memory integration pump, the
/// transactional outbox) emit through this one source/meter, so a host registers
/// <see cref="ActivitySourceName"/> and <see cref="MeterName"/> once. Publish-time trace context is
/// carried across the commit boundary, so an after-commit consumer span stays parented to the
/// operation that published the event. Tags are bounded: event type name, plane, consumer service
/// type name, and outcome — never event payloads. Runtime packages do not depend on the
/// OpenTelemetry SDK.
/// </remarks>
public static class EventTelemetry {
    /// <summary>The logical activity source name to register with OpenTelemetry tracing.</summary>
    public const string ActivitySourceName = "Elarion.Messaging";

    /// <summary>The logical meter name to register with OpenTelemetry metrics.</summary>
    public const string MeterName = "Elarion.Messaging";

    /// <summary>Shared activity source used by event publish and consumer dispatch spans.</summary>
    public static readonly ActivitySource Source = new(ActivitySourceName);

    private static readonly Meter MeterInstance = new(MeterName);

    /// <summary>Counts published events by event type and plane.</summary>
    public static readonly Counter<long> PublishCount =
        MeterInstance.CreateCounter<long>(
            "messaging.event.publish.count",
            description: "Total number of events published");

    /// <summary>Counts consumer invocations by event type, consumer, and outcome.</summary>
    public static readonly Counter<long> ConsumerCount =
        MeterInstance.CreateCounter<long>(
            "messaging.consumer.invocation.count",
            description: "Total number of event consumer invocations");

    /// <summary>Records consumer invocation duration in milliseconds.</summary>
    public static readonly Histogram<double> ConsumerDuration =
        MeterInstance.CreateHistogram<double>(
            "messaging.consumer.invocation.duration",
            unit: "ms",
            description: "Duration of event consumer invocations");

    /// <summary>Counts outbox message deliveries by event type and outcome.</summary>
    public static readonly Counter<long> DeliveryCount =
        MeterInstance.CreateCounter<long>(
            "messaging.delivery.count",
            description: "Total number of after-commit message deliveries");

    /// <summary>Records outbox message delivery duration in milliseconds.</summary>
    public static readonly Histogram<double> DeliveryDuration =
        MeterInstance.CreateHistogram<double>(
            "messaging.delivery.duration",
            unit: "ms",
            description: "Duration of after-commit message deliveries");

    /// <summary>Records one published event tagged with bounded event type and plane names.</summary>
    public static void RecordPublish(string eventType, EventPlane plane) {
        PublishCount.Add(1, new TagList {
            { "messaging.event.type", eventType },
            { "messaging.event.plane", plane == EventPlane.Domain ? "domain" : "integration" }
        });
    }

    /// <summary>Records one consumer invocation tagged with bounded event type, consumer, and outcome names.</summary>
    public static void RecordConsumer(string eventType, string consumer, string outcome, double elapsedMilliseconds) {
        var tags = new TagList {
            { "messaging.event.type", eventType },
            { "messaging.consumer", consumer },
            { "messaging.consumer.outcome", outcome }
        };
        ConsumerCount.Add(1, tags);
        ConsumerDuration.Record(elapsedMilliseconds, tags);
    }

    /// <summary>Records one after-commit message delivery tagged with bounded event type and outcome names.</summary>
    public static void RecordDelivery(string eventType, string outcome, double elapsedMilliseconds) {
        var tags = new TagList {
            { "messaging.event.type", eventType },
            { "messaging.delivery.outcome", outcome }
        };
        DeliveryCount.Add(1, tags);
        DeliveryDuration.Record(elapsedMilliseconds, tags);
    }
}
