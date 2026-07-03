namespace Elarion.Abstractions.Messaging;

/// <summary>
/// Configures the inbox (idempotent-consumer) dedup for a handler-form integration-event consumer (ADR-0022).
/// </summary>
/// <remarks>
/// <para>
/// The inbox is <b>on by default</b> for every handler-form consumer whose request is an
/// <see cref="IIntegrationEvent"/> — integration delivery is at-least-once, so dedup is the pit of success. The
/// generator attaches an <see cref="Elarion.Abstractions.Idempotency.IdempotencyDecorator{TRequest, TResponse}"/>
/// keyed per <c>(consumer, message id)</c>: the consumer's business writes commit atomically with the inbox claim,
/// and a redelivery replays the recorded success instead of re-running the effect. This attribute exists to
/// <b>opt out</b> or tune retention; its absence means the default inbox applies.
/// </para>
/// <para>
/// Opt out (<c>[Inbox(Enabled = false)]</c>) when the consumer needs no dedup rows: its effect is naturally
/// idempotent (a pure upsert on a business key), or its only effect is a call to a downstream that dedups on a
/// caller-supplied key (pass <see cref="IEventContext.MessageId"/>). An opted-out consumer gets the plain
/// transaction decorator back, exactly as if the inbox did not exist.
/// </para>
/// <para>
/// Domain-event consumers never get an inbox — they run inline in the publisher's transaction and are
/// exactly-once by atomicity. Method-form consumers have no decorator pipeline to attach to; convert to the
/// handler form when dedup matters.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class InboxAttribute : Attribute {
    /// <summary>
    /// Whether the inbox attaches to this consumer. Default <see langword="true"/>; set <see langword="false"/>
    /// to opt out (the consumer handles duplicates itself).
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// How long a completed inbox row is retained, in hours. Default 24. Retention must exceed the delivery
    /// tier's maximum retry window (for the EF Core outbox: the backoff sum across
    /// <c>OutboxOptions.MaxDeliveryAttempts</c>), or a still-retrying message could re-run after its row was
    /// purged.
    /// </summary>
    public int RetentionHours { get; init; } = 24;
}
