using Elarion.Abstractions.Messaging;

namespace Elarion.Messaging;

/// <summary>
/// Scoped buffer that holds integration events until the unit of work commits, then hands them to
/// the <see cref="EventDispatchPump"/> for after-commit delivery.
/// </summary>
internal sealed class EventDispatchScope(EventDispatchPump pump) : IEventDispatchScope {
    private readonly List<EventEnvelope> _buffer = [];

    public void Add(EventEnvelope envelope) => _buffer.Add(envelope);

    public async ValueTask FlushAsync(CancellationToken ct = default) {
        if (_buffer.Count == 0) {
            return;
        }

        foreach (var envelope in _buffer) {
            await pump.EnqueueAsync(envelope, ct).ConfigureAwait(false);
        }

        _buffer.Clear();
    }

    public void Discard() => _buffer.Clear();
}
