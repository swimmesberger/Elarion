namespace Elarion.Messaging.Outbox;

/// <summary>
/// A persisted integration event awaiting after-commit delivery.
/// </summary>
/// <remarks>
/// One row is written into the caller's <see cref="Microsoft.EntityFrameworkCore.DbContext"/> per
/// <see cref="Elarion.Abstractions.Messaging.IIntegrationEventBus.PublishAsync{TEvent}"/> call and committed
/// atomically with the business data. One <see cref="OutboxDelivery"/> child is recorded per
/// consumer; workers claim and retry those deliveries independently.
/// </remarks>
public sealed class OutboxMessage
{
    /// <summary>The unique message identifier (primary key).</summary>
    public required Guid Id { get; init; }

    /// <summary>When the event was published, in UTC.</summary>
    public required DateTimeOffset OccurredOnUtc { get; init; }

    /// <summary>
    /// The published event's <see cref="Type.FullName"/>, used to resolve the CLR type and its consumers at delivery.
    /// </summary>
    public required string EventType { get; init; }

    /// <summary>The JSON-serialized event payload.</summary>
    public required string Payload { get; init; }

    /// <summary>Correlation identifier flowed to consumers via <see cref="Elarion.Abstractions.Messaging.IEventContext.CorrelationId"/>.</summary>
    public required Guid CorrelationId { get; init; }

    /// <summary>
    /// The publisher's W3C <c>traceparent</c> captured at publish time, or <c>null</c> when no trace was active.
    /// Delivery parents its consume span on it so the after-commit consumers stay in the publishing operation's trace.
    /// </summary>
    public string? TraceParent { get; init; }

    /// <summary>The independently leased deliveries, one for each integration-event consumer.</summary>
    public ICollection<OutboxDelivery> Deliveries { get; init; } = [];
}
