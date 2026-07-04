namespace Elarion.Abstractions.Messaging;

/// <summary>
/// Declares that duplicate deliveries of the event are acceptable to this consumer, opting it out of the
/// default-on inbox (ADR-0022). The consumer-side mirror of
/// <see cref="Elarion.Abstractions.Authorization.AllowAnonymousAttribute"/>: a positive declaration that switches
/// off a default guard.
/// </summary>
/// <remarks>
/// <para>
/// Integration delivery is at-least-once, so every handler-form consumer whose request is an
/// <see cref="IIntegrationEvent"/> is deduplicated by default: the generator attaches an
/// <see cref="Elarion.Abstractions.Idempotency.IdempotencyDecorator{TRequest, TResponse}"/> keyed per
/// <c>(consumer, message id)</c>, claimed inside the consumer's own transaction, so a redelivery replays the
/// recorded success instead of re-running the effect.
/// </para>
/// <para>
/// Declare <c>[AllowDuplicates]</c> when the consumer needs no dedup rows because re-running it is harmless: its
/// effect is naturally idempotent (a pure upsert or conditional state transition on a business key), or its only
/// effect is a call to a downstream that dedups on a caller-supplied key (pass
/// <see cref="IEventContext.MessageId"/>). The opted-out consumer gets the plain transaction decorator back,
/// exactly as if the inbox did not exist — delivery stays at-least-once either way; this attribute never causes
/// loss, only un-absorbed redeliveries the consumer has declared harmless.
/// </para>
/// <para>
/// Domain-event consumers never have an inbox to opt out of — they run inline in the publisher's transaction and
/// are exactly-once by atomicity. Method-form consumers have no decorator pipeline; convert to the handler form
/// when dedup matters. On either, this attribute has no effect (ELINBX001).
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class AllowDuplicatesAttribute : Attribute;
