namespace Elarion.Messaging.Outbox;

/// <summary>
/// A persisted integration event awaiting after-commit delivery.
/// </summary>
/// <remarks>
/// One row is written into the caller's <see cref="Microsoft.EntityFrameworkCore.DbContext"/> per
/// <see cref="Elarion.Abstractions.Messaging.IIntegrationEventBus.PublishAsync{TEvent}"/> call and committed
/// atomically with the business data. A background delivery worker later claims, dispatches, and finalizes each row.
/// The payload is immutable once written; only the delivery-tracking columns are updated.
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

    /// <summary>The number of delivery attempts made so far.</summary>
    public int Attempts { get; set; }

    /// <summary>When the message was delivered, in UTC, or <c>null</c> while pending.</summary>
    public DateTimeOffset? ProcessedOnUtc { get; set; }

    /// <summary>The identifier of the worker currently holding the delivery lease, or <c>null</c> when unclaimed.</summary>
    public Guid? LockId { get; set; }

    /// <summary>When the current delivery lease expires, in UTC, after which another worker may reclaim the message.</summary>
    public DateTimeOffset? LockedUntilUtc { get; set; }

    /// <summary>The last delivery error, truncated for diagnostics, or <c>null</c> when never failed.</summary>
    public string? Error { get; set; }
}
