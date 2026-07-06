namespace Elarion.Abstractions.ClientEvents;

/// <summary>
/// Publishes a <see cref="IClientEvent"/> to the connected subscribers of its topic within
/// <paramref name="scope"/>. Delivery is <b>immediate and at-most-once</b>: a client that is not connected
/// misses the event, and that is by design — client events are hints, not facts of record.
/// </summary>
/// <remarks>
/// <para>
/// The guarantee is stated by <em>where you call this</em>, mirroring how the two event planes state theirs:
/// </para>
/// <para>
/// <b>From an integration-event consumer</b> (the recommended projection point): the consumer already runs
/// after commit on a separate scope, so every pushed event describes a fact that durably happened.
/// <b>From inside a handler</b> (the ephemeral tier — e.g. progress of a long-running command): the publish is
/// immediate and unconditional; if the command later rolls back, the events were still sent — acceptable for
/// status hints about an ongoing attempt, wrong for facts. Never publish a fact from inside its own
/// transaction; publish the integration event and project it.
/// </para>
/// <example>
/// <code>
/// [Service]
/// internal sealed class InvoicingClientProjections(IClientEventPublisher clientEvents) {
///     [ConsumeEvent]
///     public ValueTask On(InvoicePaid evt, CancellationToken ct) =>
///         clientEvents.PublishAsync(
///             new InvoiceChanged { InvoiceId = evt.InvoiceId },
///             ClientEventScope.Resource($"customer:{evt.CustomerId}"), ct);
/// }
/// </code>
/// </example>
/// </remarks>
public interface IClientEventPublisher {
    /// <summary>Publishes <paramref name="event"/> to the subscribers of its topic within <paramref name="scope"/>.</summary>
    /// <typeparam name="TEvent">The client-event contract type; must be registered as a topic.</typeparam>
    /// <param name="event">The event instance (kept light: ids and refs, not state).</param>
    /// <param name="scope">The audience of this publish.</param>
    /// <param name="ct">A cancellation token observed while handing the event to the broadcaster.</param>
    ValueTask PublishAsync<TEvent>(TEvent @event, ClientEventScope scope, CancellationToken ct = default)
        where TEvent : class, IClientEvent;
}
