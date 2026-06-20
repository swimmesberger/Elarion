namespace Elarion.Abstractions.Messaging;

/// <summary>
/// Identifies which dispatch plane a message and its consumers belong to.
/// </summary>
public enum EventPlane {
    /// <summary>In-process, inline, transaction-sharing delivery (<see cref="IDomainEventBus"/>).</summary>
    Domain,

    /// <summary>After-commit, retried-independently delivery (<see cref="IIntegrationEventBus"/>).</summary>
    Integration
}
