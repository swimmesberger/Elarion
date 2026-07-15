namespace Elarion.Messaging.Outbox;

/// <summary>One durable, independently retried consumer delivery for an <see cref="OutboxMessage"/>.</summary>
public sealed class OutboxDelivery {
    /// <summary>The delivery identifier and claim/finalize token.</summary>
    public required Guid Id { get; init; }

    /// <summary>The parent message identifier.</summary>
    public required Guid MessageId { get; init; }

    /// <summary>The source-generated stable consumer identity.</summary>
    public required string ConsumerId { get; init; }

    /// <summary>
    /// The role that must execute this delivery, or <see langword="null"/> when any worker may claim it.
    /// </summary>
    public string? TargetRole { get; init; }

    /// <summary>The number of failed delivery attempts made so far.</summary>
    public int Attempts { get; set; }

    /// <summary>When this consumer completed, or <see langword="null"/> while pending.</summary>
    public DateTimeOffset? ProcessedOnUtc { get; set; }

    /// <summary>The current worker lease token.</summary>
    public Guid? LockId { get; set; }

    /// <summary>Lease expiry or retry visibility deadline.</summary>
    public DateTimeOffset? LockedUntilUtc { get; set; }

    /// <summary>The latest delivery error, retained for diagnostics.</summary>
    public string? Error { get; set; }

    /// <summary>The immutable event envelope.</summary>
    public required OutboxMessage Message { get; init; }
}
