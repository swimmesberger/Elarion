using System.Text.Json;
using System.Threading.Channels;
using Elarion.Abstractions.ClientEvents;
using Elarion.Abstractions.Serialization;

namespace Elarion.ClientEvents;

/// <summary>
/// The single-subscriber sink handed to <see cref="IClientEventSubscriptionObserver.OnSubscribedAsync"/>:
/// writes directly into that subscriber's channel, bypassing fan-out. A publish after the subscriber
/// disconnected is a no-op (the channel is completed) — at-most-once, like every client event.
/// </summary>
internal sealed class ClientEventSubscriberSink(
    ClientEventSubscription subscription,
    ChannelWriter<ClientEventEnvelope> writer,
    ClientEventTopicCatalog catalog,
    IElarionJsonSerialization serialization) : IClientEventSubscriberSink {
    public ClientEventSubscription Subscription { get; } = subscription;

    public ValueTask PublishAsync<TEvent>(TEvent @event, CancellationToken ct = default)
        where TEvent : class, IClientEvent {
        ArgumentNullException.ThrowIfNull(@event);
        var topic = catalog.GetByType(typeof(TEvent));
        if (!string.Equals(topic.Name, Subscription.Topic, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"'{typeof(TEvent)}' is the contract of topic '{topic.Name}', but this sink delivers to a " +
                $"subscriber of '{Subscription.Topic}'. A subscriber sink only accepts its own topic's contract.");

        writer.TryWrite(new ClientEventEnvelope {
            Id = Guid.CreateVersion7(),
            Topic = topic.Name,
            Scope = Subscription.Scope,
            Payload = JsonSerializer.Serialize(@event, serialization.GetTypeInfo<TEvent>())
        });
        return default;
    }
}
