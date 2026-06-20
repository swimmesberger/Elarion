namespace Elarion.Abstractions.Messaging;

/// <summary>
/// Configures the in-memory event bus runtime.
/// </summary>
/// <remarks>
/// These options affect only the current process. The in-memory integration-event tier does not
/// persist buffered or in-flight events across restarts; events flushed but not yet delivered when
/// the process exits are lost. Use a durable delivery tier for at-least-once guarantees.
/// </remarks>
public sealed record EventBusOptions {
    /// <summary>
    /// Whether the hosted integration-event delivery pump drains and dispatches flushed events.
    /// </summary>
    /// <remarks>
    /// When disabled, integration events can still be published and buffered, but they are not
    /// delivered until a pump is enabled. Domain-event publishing is unaffected.
    /// </remarks>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Maximum number of flushed integration events that may wait for delivery before
    /// <see cref="IEventDispatchScope.FlushAsync"/> applies back-pressure.
    /// </summary>
    /// <remarks>Values below one are normalized to one by configuration-based registration.</remarks>
    public int DeliveryChannelCapacity { get; init; } = 1024;
}
