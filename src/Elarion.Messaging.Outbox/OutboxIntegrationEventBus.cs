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
    OutboxConsumerCatalog consumerCatalog,
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

        var consumers = consumerCatalog.GetConsumerArray(typeof(TEvent));
        if (consumers.Length == 0) {
            throw new InvalidOperationException(
                $"Integration event '{typeof(TEvent)}' has no registered consumers and cannot be written to the outbox.");
        }

        var payload = JsonSerializer.Serialize(@event, options.SerializerOptions ?? jsonSerialization.Options);
        var messageId = Guid.CreateVersion7();
        var occurredOnUtc = timeProvider.GetUtcNow();
        var eventType = typeof(TEvent).FullName
            ?? throw new InvalidOperationException(
                $"Integration event '{typeof(TEvent)}' has no full name and cannot be persisted.");
        var correlationId = Guid.CreateVersion7();
        var traceParent = Activity.Current?.Id;

        // Most events have no role-routed consumers. The catalog records that once at startup so this path remains
        // one envelope, one append and O(1) with respect to consumer count.
        if (!consumerCatalog.HasDeliveryRoleResolvers(typeof(TEvent))) {
            store.Append(new OutboxMessage {
                Id = messageId,
                MessageId = messageId,
                OccurredOnUtc = occurredOnUtc,
                EventType = eventType,
                Payload = payload,
                CorrelationId = correlationId,
                TraceParent = traceParent
            });
            return ValueTask.CompletedTask;
        }

        var groups = new List<DeliveryGroup>();
        var groupByRole = new Dictionary<string, int>(StringComparer.Ordinal);
        var unboundGroup = -1;
        foreach (var descriptor in consumers) {
            var targetRole = descriptor.ResolveDeliveryRole?.Invoke(serviceProvider, @event);
            if (targetRole is not null) {
                ArgumentException.ThrowIfNullOrWhiteSpace(targetRole);
            }

            int groupIndex;
            if (targetRole is null) {
                if (unboundGroup < 0) {
                    unboundGroup = groups.Count;
                    groups.Add(new DeliveryGroup(null));
                }

                groupIndex = unboundGroup;
            }
            else if (!groupByRole.TryGetValue(targetRole, out groupIndex)) {
                groupIndex = groups.Count;
                groupByRole.Add(targetRole, groupIndex);
                groups.Add(new DeliveryGroup(targetRole));
            }

            groups[groupIndex].ConsumerIds.Add(descriptor.ConsumerId);
        }

        for (var index = 0; index < groups.Count; index++) {
            var group = groups[index];
            store.Append(new OutboxMessage {
                // Keep the common one-group shape identical to the historical outbox: its row id is
                // also the logical message id. Additional target groups get their own lease identity.
                Id = index == 0 ? messageId : Guid.CreateVersion7(),
                MessageId = messageId,
                OccurredOnUtc = occurredOnUtc,
                EventType = eventType,
                Payload = payload,
                CorrelationId = correlationId,
                TraceParent = traceParent,
                ConsumerIdsJson = OutboxConsumerIds.Serialize(group.ConsumerIds),
                TargetRole = group.TargetRole
            });
        }

        return ValueTask.CompletedTask;
    }

    private sealed class DeliveryGroup(string? targetRole) {
        public string? TargetRole { get; } = targetRole;

        public List<string> ConsumerIds { get; } = [];
    }
}
