namespace Elarion.Abstractions.Messaging;

/// <summary>
/// Marks an event consumer, discovered by the event consumer source generator and registered as
/// an <see cref="EventSubscriptionDescriptor"/>. It applies in two forms.
/// </summary>
/// <remarks>
/// <para>
/// <b>Handler form (preferred)</b> — on a class implementing
/// <c>IHandler&lt;TEvent, Result&lt;T&gt;&gt;</c> (or the <c>IHandler&lt;TEvent&gt;</c> sugar) whose
/// request type <em>is</em> the event. The consumer is a first-class unit of business logic
/// dispatched through its full decorator pipeline (tracing, resilience, validation,
/// cache-invalidation), and its role is inferred from the response type:
/// <c>Result&lt;Unit&gt;</c> (the <c>IHandler&lt;TEvent&gt;</c> sugar) is a fan-out subscriber whose
/// failed <c>Result</c> surfaces as an <see cref="EventConsumerFailedException"/> so the publishing
/// plane's failure semantics apply, while a domain handler returning <c>Result&lt;T&gt;</c>
/// (<c>T</c> ≠ <see cref="Unit"/>) is the single responder for an
/// <see cref="IDomainEventBus.RequestAsync{TRequest,TResponse}"/> call. Integration events are
/// fan-out only.
/// </para>
/// <para>
/// <b>Method form (alternative)</b> — on a method of a <c>[Service]</c> class, for a small side
/// effect on a service you already have (no decorator pipeline). The consumed message type is the
/// method's message parameter, and the plane is taken from that type's marker
/// (<see cref="IDomainEvent"/> or <see cref="IIntegrationEvent"/>). The role is inferred from the
/// return type: a <c>ValueTask</c>/<c>Task</c> (or <c>void</c>) method is a fan-out subscriber,
/// while a method returning <c>Result&lt;TResponse&gt;</c> is the single responder.
/// </para>
/// <example>
/// <code>
/// // Handler form (preferred)
/// [ConsumeEvent]
/// public sealed class ProjectInvoice : IHandler&lt;InvoiceCreated&gt; {
///     public ValueTask&lt;Result&gt; HandleAsync(InvoiceCreated e, CancellationToken ct) =&gt; ...;
/// }
///
/// // Method form (alternative)
/// [Service]
/// public sealed class InvoiceProjections(AppDbContext db) {
///     [ConsumeEvent(Order = 0)]
///     public ValueTask OnCreating(InvoiceCreating e, CancellationToken ct) =&gt; ...;
/// }
/// </code>
/// </example>
/// </remarks>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class ConsumeEventAttribute : Attribute {
    /// <summary>
    /// Relative invocation order among fan-out subscribers of the same event, ascending.
    /// Consumers with equal order run in a stable, generator-determined sequence.
    /// </summary>
    public int Order { get; init; }
}
