using System.Text.Json;
using Elarion.Abstractions.ClientEvents;
using Elarion.Abstractions.Serialization;

namespace Elarion.ClientEvents;

/// <summary>
/// The default <see cref="IClientEventPublisher"/>: resolves the contract's topic from the catalog (an
/// unregistered type fails loud — opt-in by enumeration), serializes the payload once via the canonical
/// JSON accessor (AOT-strict: the contract must be in a registered source-gen context), and hands the
/// envelope to the broadcaster seam.
/// </summary>
internal sealed class ClientEventPublisher(
    ClientEventTopicCatalog catalog,
    IElarionJsonSerialization serialization,
    IClientEventBroadcaster broadcaster) : IClientEventPublisher {
    public ValueTask PublishAsync<TEvent>(TEvent @event, ClientEventScope scope, CancellationToken ct = default)
        where TEvent : class, IClientEvent {
        ArgumentNullException.ThrowIfNull(@event);
        var topic = catalog.GetByType(typeof(TEvent));
        var payload = JsonSerializer.Serialize(@event, serialization.GetTypeInfo<TEvent>());
        var envelope = new ClientEventEnvelope {
            Id = Guid.CreateVersion7(),
            Topic = topic.Name,
            Scope = scope,
            Payload = payload
        };
        return broadcaster.BroadcastAsync(envelope, ct);
    }
}
