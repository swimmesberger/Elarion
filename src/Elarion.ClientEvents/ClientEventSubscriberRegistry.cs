using System.Collections.Concurrent;
using System.Threading.Channels;
using Elarion.Abstractions.ClientEvents;

namespace Elarion.ClientEvents;

/// <summary>
/// The in-process subscriber registry: the one place connections register (topic, scope) interest and
/// envelopes fan out. Matching is exact record equality on (topic, scope), O(subscribers) per envelope —
/// right-sized for the 1–10-node tier this default targets.
/// </summary>
internal sealed class ClientEventSubscriberRegistry : IClientEventLocalDelivery, IClientEventSubscriptionSource {
    // Per-subscriber buffer: enough to absorb bursts between reads; overflow drops oldest (hints, not a queue).
    private const int SubscriberBufferCapacity = 64;

    private readonly ConcurrentDictionary<Guid, Entry> _subscribers = new();

    public ClientEventSubscriptionHandle Subscribe(IReadOnlyList<ClientEventSubscription> subscriptions) {
        ArgumentNullException.ThrowIfNull(subscriptions);
        if (subscriptions.Count == 0) {
            throw new ArgumentException("At least one subscription is required.", nameof(subscriptions));
        }

        var id = Guid.CreateVersion7();
        var channel = Channel.CreateBounded<ClientEventEnvelope>(new BoundedChannelOptions(SubscriberBufferCapacity) {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        _subscribers[id] = new Entry([.. subscriptions], channel);

        return new ClientEventSubscriptionHandle(channel.Reader, () => {
            if (_subscribers.TryRemove(id, out var entry)) {
                entry.Channel.Writer.TryComplete();
            }
        });
    }

    public void Deliver(ClientEventEnvelope envelope) {
        ArgumentNullException.ThrowIfNull(envelope);
        var key = new ClientEventSubscription { Topic = envelope.Topic, Scope = envelope.Scope };
        foreach (var (_, entry) in _subscribers) {
            if (entry.Subscriptions.Contains(key)) {
                entry.Channel.Writer.TryWrite(envelope);
            }
        }
    }

    public void DeliverToAll(ClientEventEnvelope envelope) {
        ArgumentNullException.ThrowIfNull(envelope);
        foreach (var (_, entry) in _subscribers) {
            entry.Channel.Writer.TryWrite(envelope);
        }
    }

    private sealed record Entry(HashSet<ClientEventSubscription> Subscriptions, Channel<ClientEventEnvelope> Channel);
}
