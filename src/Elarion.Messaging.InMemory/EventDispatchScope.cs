namespace Elarion.Messaging.InMemory;

/// <summary>
/// Scoped buffer that holds integration events until the unit of work commits, then hands them to
/// the <see cref="EventDispatchPump"/> for after-commit delivery.
/// </summary>
/// <remarks>
/// The EF Core interceptors drive this buffer from the DbContext lifecycle: <see cref="FlushAsync"/>
/// runs after a successful commit and <see cref="Discard"/> runs on rollback. It is an internal
/// implementation detail of the in-memory integration tier, not a public seam.
/// </remarks>
internal sealed class EventDispatchScope(EventDispatchPump pump) {
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
