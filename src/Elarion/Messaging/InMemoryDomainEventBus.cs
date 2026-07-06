using System.Diagnostics;
using Elarion.Abstractions.Messaging;

namespace Elarion.Messaging;

/// <summary>
/// In-process <see cref="IDomainEventBus"/>: dispatches domain events inline within the caller's
/// DI scope so consumers share the caller's scoped services and transaction.
/// </summary>
/// <remarks>
/// Registered scoped; the injected <see cref="IServiceProvider"/> is the caller's scope, so every
/// consumer resolved here observes the same scoped <c>DbContext</c> as the publisher.
/// </remarks>
internal sealed class InMemoryDomainEventBus(
    IServiceProvider serviceProvider,
    EventSubscriptionRegistry registry
) : IDomainEventBus {
    /// <summary>
    /// Maximum nested inline domain-event publishes in one publish chain. Because Plane A dispatches
    /// consumers inline on the caller's stack, a consumer that (transitively) re-publishes an event
    /// recurses; without a bound a publish cycle overflows the stack (an unhandleable crash). The depth
    /// is tracked per async flow so it fences genuine re-entrancy, not concurrent unrelated publishes.
    /// </summary>
    private const int MaxPublishDepth = 32;

    private static readonly AsyncLocal<int> PublishDepth = new();

    public async ValueTask PublishAsync<TEvent>(TEvent @event, CancellationToken ct = default)
        where TEvent : IDomainEvent {
        ArgumentNullException.ThrowIfNull(@event);

        var eventName = typeof(TEvent).Name;
        EventTelemetry.RecordPublish(eventName, EventPlane.Domain);

        var subscribers = registry.GetDomainSubscribers(typeof(TEvent));
        if (subscribers.Length == 0) {
            return;
        }

        var depth = PublishDepth.Value + 1;
        if (depth > MaxPublishDepth) {
            throw new InvalidOperationException(
                $"Domain event publish exceeded the maximum inline re-entrancy depth of {MaxPublishDepth} " +
                $"while publishing '{typeof(TEvent)}'. A domain-event consumer is re-publishing an event " +
                "that (transitively) triggers itself; break the publish cycle or move the follow-up to an " +
                "integration event (Plane B), which is delivered after commit rather than inline.");
        }

        PublishDepth.Value = depth;
        try {
            // The publish span parents each inline consumer's own handler span (same scope, same trace).
            using var activity = EventTelemetry.Source.StartActivity($"publish {eventName}", ActivityKind.Internal);
            if (activity is not null) {
                activity.SetTag("messaging.event.type", eventName);
                activity.SetTag("messaging.event.plane", "domain");
                activity.SetTag("messaging.subscriber.count", subscribers.Length);
            }

            var context = new EventContext<TEvent>(@event, Guid.CreateVersion7(), EventPlane.Domain);
            List<Exception>? failures = null;
            foreach (var descriptor in subscribers) {
                var startTimestamp = Stopwatch.GetTimestamp();
                try {
                    await descriptor.InvokeAsync!(serviceProvider, @event, context, ct).ConfigureAwait(false);
                    EventTelemetry.RecordConsumer(
                        eventName, descriptor.ServiceType.Name, "ok",
                        Stopwatch.GetElapsedTime(startTimestamp));
                }
                catch (Exception ex) when (ex is not OperationCanceledException) {
                    // Run every subscriber, then surface all failures so one publish fails atomically.
                    (failures ??= []).Add(ex);
                    EventTelemetry.RecordConsumer(
                        eventName, descriptor.ServiceType.Name, "exception",
                        Stopwatch.GetElapsedTime(startTimestamp));
                    activity?.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection {
                        { "exception.type", ex.GetType().FullName },
                        { "exception.message", ex.Message },
                        { "messaging.consumer", descriptor.ServiceType.Name }
                    }));
                }
            }

            if (failures is not null) {
                activity?.SetStatus(ActivityStatusCode.Error, "One or more domain event consumers failed.");
                throw failures.Count == 1 ? failures[0] : new AggregateException(failures);
            }
        }
        finally {
            PublishDepth.Value = depth - 1;
        }
    }
}
