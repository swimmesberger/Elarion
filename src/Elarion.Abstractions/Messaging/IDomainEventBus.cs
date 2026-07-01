namespace Elarion.Abstractions.Messaging;

/// <summary>
/// Publishes <em>domain events</em>: in-process notifications dispatched inline within the
/// caller's DI scope, and therefore within the caller's transaction.
/// </summary>
/// <remarks>
/// <para>
/// The guarantee is stated by the interface, not the verb. Every method on this bus runs
/// synchronously in the caller's scope: consumers observe the same scoped services (including a
/// scoped <c>DbContext</c>), so their writes commit atomically with the command and a consumer
/// failure fails the command. Nothing is queued and nothing is broker-portable.
/// </para>
/// <example>
/// <code>
/// // Inside a handler, within the unit of work:
/// await domainEvents.PublishAsync(new InvoiceCreating(invoice.Id, command.CustomerId), ct);
/// </code>
/// </example>
/// </remarks>
public interface IDomainEventBus {
    /// <summary>
    /// Dispatches <paramref name="event"/> to every registered domain-event consumer, in
    /// ascending <see cref="ConsumeEventAttribute.Order"/>, awaiting each in turn.
    /// </summary>
    /// <typeparam name="TEvent">The domain event type.</typeparam>
    /// <param name="event">The event instance.</param>
    /// <param name="ct">A cancellation token observed by every consumer.</param>
    /// <remarks>
    /// If one or more consumers throw, the remaining consumers still run and the exceptions are
    /// aggregated and rethrown, so a single publish either fully succeeds or surfaces every failure.
    /// </remarks>
    ValueTask PublishAsync<TEvent>(TEvent @event, CancellationToken ct = default)
        where TEvent : IDomainEvent;
}
