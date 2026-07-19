using System.Threading.Channels;

namespace Elarion.ClientEvents;

/// <summary>
/// One live subscriber: the reader carrying its matched envelopes, valid until disposed. The buffer is
/// bounded and drops oldest on overflow — a slow client loses old hints, never stalls other subscribers.
/// </summary>
public sealed class ClientEventSubscriptionHandle : IDisposable {
    private Action? _onDispose;

    internal ClientEventSubscriptionHandle(ChannelReader<ClientEventEnvelope> events, Action onDispose) {
        Events = events;
        _onDispose = onDispose;
    }

    /// <summary>The subscriber's envelope stream; completes when the handle is disposed.</summary>
    public ChannelReader<ClientEventEnvelope> Events { get; }

    /// <inheritdoc />
    public void Dispose() {
        Interlocked.Exchange(ref _onDispose, null)?.Invoke();
    }
}
