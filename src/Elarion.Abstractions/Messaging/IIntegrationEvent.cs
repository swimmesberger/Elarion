namespace Elarion.Abstractions.Messaging;

/// <summary>
/// Marks a message as an <em>integration event</em>: a notification recorded within the
/// publisher's unit of work and delivered <em>after</em> the transaction commits.
/// </summary>
/// <remarks>
/// Integration events are published through <see cref="IIntegrationEventBus"/>. They are the only
/// broker-portable plane: a delivery failure is retried independently of the originating command
/// and never fails it, and an event is never delivered if the command rolls back. Use an integration
/// event for reactions that must be reliable but need not be atomic with the command (e.g. sending
/// email, updating a read model, notifying another service). For in-transaction reactions, model the
/// message as an <see cref="IDomainEvent"/> instead.
/// </remarks>
public interface IIntegrationEvent;
