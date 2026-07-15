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
    IEnumerable<EventSubscriptionDescriptor> descriptors,
    IServiceProvider serviceProvider,
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

        var consumers = descriptors
            .Where(descriptor => descriptor.Plane is EventPlane.Integration
                && descriptor.EventType == typeof(TEvent)
                && descriptor.InvokeAsync is not null)
            .OrderBy(descriptor => descriptor.Order)
            .ToArray();
        if (consumers.Length == 0) {
            throw new InvalidOperationException(
                $"Integration event '{typeof(TEvent)}' has no registered consumers and cannot be written to the outbox.");
        }

        var duplicate = consumers
            .Where(descriptor => !string.IsNullOrWhiteSpace(descriptor.ConsumerId))
            .GroupBy(descriptor => descriptor.ConsumerId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null) {
            throw new InvalidOperationException(
                $"Integration-event consumer id '{duplicate.Key}' is registered more than once.");
        }

        var missingId = consumers.FirstOrDefault(descriptor => string.IsNullOrWhiteSpace(descriptor.ConsumerId));
        if (missingId is not null) {
            throw new InvalidOperationException(
                $"Integration-event consumer '{missingId.ServiceType}' has no stable ConsumerId.");
        }

        var payload = JsonSerializer.Serialize(@event, options.SerializerOptions ?? jsonSerialization.Options);
        var messageId = Guid.CreateVersion7();
        var message = new OutboxMessage
        {
            Id = messageId,
            OccurredOnUtc = timeProvider.GetUtcNow(),
            EventType = typeof(TEvent).FullName
                ?? throw new InvalidOperationException($"Integration event '{typeof(TEvent)}' has no full name and cannot be persisted."),
            Payload = payload,
            CorrelationId = Guid.CreateVersion7(),
            // Activity.Id is the W3C traceparent; persisting it keeps the after-commit delivery span
            // in the publishing operation's trace even across a restart or another worker instance.
            TraceParent = Activity.Current?.Id
        };

        foreach (var descriptor in consumers) {
            var targetRole = descriptor.ResolveDeliveryRole?.Invoke(serviceProvider, @event);
            if (targetRole is not null) {
                ArgumentException.ThrowIfNullOrWhiteSpace(targetRole);
            }

            message.Deliveries.Add(new OutboxDelivery {
                Id = Guid.CreateVersion7(),
                MessageId = messageId,
                ConsumerId = descriptor.ConsumerId,
                TargetRole = targetRole,
                Message = message
            });
        }

        store.Append(message);

        return ValueTask.CompletedTask;
    }
}
