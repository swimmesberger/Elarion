using System.Diagnostics;
using System.Text.Json;
using Elarion.Abstractions.Messaging;
using Elarion.Abstractions.Serialization;

namespace Elarion.Messaging.Outbox;

/// <summary>
/// Durable <see cref="IIntegrationEventBus"/>: records each integration event as an <see cref="OutboxMessage"/> in
/// the caller's unit of work, so it commits atomically with the business data and is delivered after commit by the
/// background worker.
/// </summary>
/// <remarks>
/// Recording does not call <c>SaveChanges</c>; the publisher's unit of work persists the row within the same
/// transaction. If the transaction rolls back, the event is discarded with every other change — the
/// per-scope buffer the in-memory tier needs is unnecessary here because the database transaction provides atomicity.
/// </remarks>
public sealed class OutboxIntegrationEventBus(
    IOutboxStore store,
    OutboxOptions options,
    IElarionJsonSerialization jsonSerialization,
    TimeProvider timeProvider)
    : IIntegrationEventBus
{
    /// <inheritdoc />
    public ValueTask PublishAsync<TEvent>(TEvent @event, CancellationToken ct = default)
        where TEvent : IIntegrationEvent
    {
        ArgumentNullException.ThrowIfNull(@event);

        EventTelemetry.RecordPublish(typeof(TEvent).Name, EventPlane.Integration);

        var payload = JsonSerializer.Serialize(@event, options.SerializerOptions ?? jsonSerialization.Options);
        store.Append(new OutboxMessage
        {
            Id = Guid.NewGuid(),
            OccurredOnUtc = timeProvider.GetUtcNow(),
            EventType = typeof(TEvent).FullName
                ?? throw new InvalidOperationException($"Integration event '{typeof(TEvent)}' has no full name and cannot be persisted."),
            Payload = payload,
            CorrelationId = Guid.NewGuid(),
            // Activity.Id is the W3C traceparent; persisting it keeps the after-commit delivery span
            // in the publishing operation's trace even across a restart or another worker instance.
            TraceParent = Activity.Current?.Id
        });

        return ValueTask.CompletedTask;
    }
}
