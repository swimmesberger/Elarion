namespace Elarion.Abstractions.Messaging;

/// <summary>
/// Marks a method on a <c>[Service]</c> class as an event consumer, discovered by the event
/// consumer source generator and registered as an <see cref="EventSubscriptionDescriptor"/>.
/// </summary>
/// <remarks>
/// <para>
/// The consumed message type is the method's message parameter, and the plane is taken from that
/// type's marker (<see cref="IDomainEvent"/> or <see cref="IIntegrationEvent"/>). The consumer
/// <em>role</em> is inferred from the return type: a <c>ValueTask</c>/<c>Task</c> (or <c>void</c>)
/// method is a fan-out subscriber, while a method returning <c>Result&lt;TResponse&gt;</c> is the
/// single responder for an <see cref="IDomainEventBus.RequestAsync{TRequest,TResponse}"/> call.
/// </para>
/// <example>
/// <code>
/// [Service]
/// public sealed class InvoiceProjections(AppDbContext db) {
///     [ConsumeEvent(Order = 0)]
///     public ValueTask OnCreating(InvoiceCreating e, CancellationToken ct) =&gt; ...;
/// }
/// </code>
/// </example>
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class ConsumeEventAttribute : Attribute {
    /// <summary>
    /// Relative invocation order among fan-out subscribers of the same event, ascending.
    /// Consumers with equal order run in a stable, generator-determined sequence.
    /// </summary>
    public int Order { get; init; }
}
