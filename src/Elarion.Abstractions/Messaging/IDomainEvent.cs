namespace Elarion.Abstractions.Messaging;

/// <summary>
/// Marks a message as a <em>domain event</em>: an in-process notification dispatched
/// inline within the publisher's DI scope (and therefore the publisher's transaction).
/// </summary>
/// <remarks>
/// Domain events are delivered synchronously by <see cref="IDomainEventBus"/>. A subscriber
/// that writes to the same scoped <c>DbContext</c> commits atomically with the command, and a
/// subscriber failure fails the command. Domain events never cross a process boundary and are
/// never queued for after-commit delivery; for that, model the message as an
/// <see cref="IIntegrationEvent"/> instead.
/// </remarks>
public interface IDomainEvent;
