namespace Elarion.Abstractions.Messaging;

/// <summary>
/// Publishes <em>integration events</em>: notifications recorded within the caller's unit of
/// work and delivered <em>after</em> the transaction commits.
/// </summary>
/// <remarks>
/// <para>
/// The guarantee is stated by the interface, not the verb. A publish records the event for the
/// current scope; the configured delivery tier hands it to consumers only after the unit of work
/// commits, on a separate scope. Delivery is retried independently of the originating command and a
/// consumer failure never fails the command. If the command rolls back, the event is discarded.
/// </para>
/// <para>
/// This is the only broker-portable plane: an alternative backend (for example, a transactional
/// outbox or a message broker) implements <em>only</em> this interface.
/// </para>
/// <example>
/// <code>
/// // Inside a handler, within the unit of work:
/// await integrationEvents.PublishAsync(new InvoiceCreated(invoice.Id, command.CustomerEmail), ct);
/// </code>
/// </example>
/// </remarks>
public interface IIntegrationEventBus {
    /// <summary>
    /// Records <paramref name="event"/> for after-commit delivery to every registered
    /// integration-event consumer.
    /// </summary>
    /// <typeparam name="TEvent">The integration event type.</typeparam>
    /// <param name="event">The event instance.</param>
    /// <param name="ct">A cancellation token observed while recording the event.</param>
    /// <remarks>
    /// The returned task completes once the event is recorded, not once it is delivered. Delivery
    /// happens after the unit of work commits and is owned by the configured delivery tier.
    /// </remarks>
    ValueTask PublishAsync<TEvent>(TEvent @event, CancellationToken ct = default)
        where TEvent : IIntegrationEvent;
}
