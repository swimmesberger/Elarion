using Elarion.Abstractions.Results;

namespace Elarion.Abstractions.Messaging;

/// <summary>
/// Marks an event consumer, discovered by the event consumer source generator and registered as
/// an <see cref="EventSubscriptionDescriptor"/>. It applies in two forms.
/// </summary>
/// <remarks>
/// <para>
/// Every consumer is a <b>fan-out subscriber</b> — the event bus is pub/sub-only (ADR-0010). For
/// request/reply (one typed response), call a handler by type with <c>IHandlerSender</c>/<c>IHandler</c>
/// instead of the event bus.
/// </para>
/// <para>
/// <b>Handler form (preferred)</b> — on a class implementing <c>IHandler&lt;TEvent&gt;</c> (or
/// <c>IHandler&lt;TEvent, Result&lt;<see cref="Unit"/>&gt;&gt;</c>) whose request type <em>is</em> the
/// event. The consumer is a first-class unit of business logic dispatched through its full decorator
/// pipeline (tracing, resilience, validation, cache-invalidation); a failed <c>Result</c> surfaces as an
/// <see cref="EventConsumerFailedException"/> so the publishing plane's failure semantics apply.
/// </para>
/// <para>
/// <b>Method form (alternative)</b> — on a method of a <c>[Service]</c> class, for a small side
/// effect on a service you already have (no decorator pipeline). The consumed message type is the
/// method's message parameter, and the plane is taken from that type's marker
/// (<see cref="IDomainEvent"/> or <see cref="IIntegrationEvent"/>). The method returns
/// <c>void</c>/<c>Task</c>/<c>ValueTask</c>.
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
