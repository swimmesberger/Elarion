namespace Elarion.Messaging.Outbox;

/// <summary>
/// One persisted integration-event delivery group awaiting after-commit execution.
/// </summary>
/// <remarks>
/// Consumers resolving to the same target role share one row and one retry boundary. The common
/// unbound case therefore remains one row per publish regardless of consumer count. A publish that
/// resolves consumers to several roles writes one envelope per distinct role, all sharing
/// <see cref="MessageId"/> as their inbox/idempotency identity.
/// </remarks>
public sealed class OutboxMessage
{
    /// <summary>The unique delivery-group identifier (primary key and lease/finalize identity).</summary>
    public required Guid Id { get; init; }

    /// <summary>
    /// The logical published-message identifier, shared by every target group produced by one publish.
    /// This is the stable <c>IEventContext.MessageId</c> and inbox deduplication key.
    /// </summary>
    public required Guid MessageId { get; init; }

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

    /// <summary>
    /// Fixed JSON array of stable consumer identities, in invocation order, for role-routed publishes.
    /// <see langword="null"/> means no consumer for <see cref="EventType"/> declares role routing, so this
    /// envelope targets the catalog's complete ordered consumer array without per-consumer metadata.
    /// Infrastructure-owned;
    /// applications should not construct or edit this payload.
    /// </summary>
    public string? ConsumerIdsJson { get; init; }

    /// <summary>
    /// The role that must execute this group, or <see langword="null"/> when any worker may claim it.
    /// </summary>
    public string? TargetRole { get; init; }

    /// <summary>The number of failed delivery attempts made so far.</summary>
    public int Attempts { get; set; }

    /// <summary>When this delivery group completed, or <see langword="null"/> while pending.</summary>
    public DateTimeOffset? ProcessedOnUtc { get; set; }

    /// <summary>The current worker lease token.</summary>
    public Guid? LockId { get; set; }

    /// <summary>Lease expiry or retry visibility deadline.</summary>
    public DateTimeOffset? LockedUntilUtc { get; set; }

    /// <summary>The latest delivery error, retained for diagnostics.</summary>
    public string? Error { get; set; }
}
