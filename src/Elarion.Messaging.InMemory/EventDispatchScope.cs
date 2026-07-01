using Microsoft.Extensions.Logging;

namespace Elarion.Messaging.InMemory;

/// <summary>
/// Scoped buffer that holds integration events until the unit of work commits, then hands them to
/// the <see cref="EventDispatchPump"/> for after-commit delivery.
/// </summary>
/// <remarks>
/// The EF Core interceptors drive this buffer from the DbContext lifecycle: <see cref="FlushAsync"/>
/// runs after a successful commit and <see cref="Discard"/> runs on rollback. It is an internal
/// implementation detail of the in-memory integration tier, not a public seam.
/// <para>
/// The buffer is <b>savepoint-aware</b>: <see cref="PushSavepoint"/> records the buffer high-water mark when a
/// savepoint is created, <see cref="RollbackToSavepoint"/> truncates the buffer back to the most recent mark
/// (dropping events buffered after the savepoint), and <see cref="ReleaseSavepoint"/> pops the mark. Savepoints
/// are modeled as a LIFO stack because the EF Core transaction-interceptor callbacks do not carry the savepoint
/// name (only <c>TransactionEventData</c>), and nested savepoints in a single transaction release/roll back in
/// stack order. This keeps a partial rollback — e.g. the idempotency decorator rolling a failed command back to a
/// savepoint yet still committing the outer transaction to persist the failure record — from delivering events for
/// writes that were undone.
/// </para>
/// </remarks>
internal sealed class EventDispatchScope(EventDispatchPump pump, ILogger<EventDispatchScope> logger) : IDisposable {
    private readonly List<EventEnvelope> _buffer = [];

    // Buffer high-water marks, one per active savepoint, in creation (stack) order. The interceptor callbacks
    // carry no savepoint name, so rollback/release target the top of the stack — correct for LIFO nesting.
    private readonly Stack<int> _savepointMarks = new();
    private bool _flushed;

    public void Add(EventEnvelope envelope) => _buffer.Add(envelope);

    /// <summary>Records the current buffer size as the high-water mark for a newly created savepoint.</summary>
    public void PushSavepoint() => _savepointMarks.Push(_buffer.Count);

    /// <summary>
    /// Truncates the buffer back to the most recent savepoint mark, dropping every event buffered after that
    /// savepoint was created. The mark is kept: SQL <c>ROLLBACK TO SAVEPOINT</c> leaves the savepoint intact, so
    /// it may be rolled back to again or released later.
    /// </summary>
    public void RollbackToSavepoint() {
        if (_savepointMarks.Count == 0) {
            return;
        }

        var mark = _savepointMarks.Peek();
        if (mark < _buffer.Count) {
            _buffer.RemoveRange(mark, _buffer.Count - mark);
        }
    }

    /// <summary>Pops the most recent savepoint mark without touching the buffer (SQL <c>RELEASE SAVEPOINT</c>).</summary>
    public void ReleaseSavepoint() {
        if (_savepointMarks.Count > 0) {
            _savepointMarks.Pop();
        }
    }

    public async ValueTask FlushAsync(CancellationToken ct = default) {
        _savepointMarks.Clear();
        if (_buffer.Count == 0) {
            _flushed = true;
            return;
        }

        foreach (var envelope in _buffer) {
            await pump.EnqueueAsync(envelope, ct).ConfigureAwait(false);
        }

        _buffer.Clear();
        _flushed = true;
    }

    /// <summary>
    /// Flushes the buffer from a <b>synchronous</b> commit/save interceptor path via the pump's non-blocking
    /// enqueue, so the commit thread is never blocked on a full delivery channel. See
    /// <see cref="EventDispatchPump.EnqueueSynchronously"/> for the drop-with-Error-log policy.
    /// </summary>
    public void FlushSynchronously() {
        _savepointMarks.Clear();
        if (_buffer.Count == 0) {
            _flushed = true;
            return;
        }

        foreach (var envelope in _buffer) {
            pump.EnqueueSynchronously(envelope);
        }

        _buffer.Clear();
        _flushed = true;
    }

    public void Discard() {
        _buffer.Clear();
        _savepointMarks.Clear();
        _flushed = true;
    }

    /// <summary>
    /// Warns if the scope ends with events that were buffered but never flushed by a commit — the sign that an
    /// integration event was published in a request that never committed a transaction (no <c>SaveChanges</c>, or
    /// an <c>ExecuteUpdate</c>-only handler with no ambient transaction), which would otherwise drop the event
    /// silently.
    /// </summary>
    public void Dispose() {
        if (_flushed || _buffer.Count == 0) {
            return;
        }

        var eventTypes = string.Join(", ", _buffer.Select(e => e.EventType.Name).Distinct(StringComparer.Ordinal));
        logger.LogWarning(
            "{Count} integration event(s) ({EventTypes}) were published but dropped without delivery because no " +
            "transaction committed in this scope. The in-memory integration tier only delivers events after a " +
            "committed unit of work; publish inside a SaveChanges/transaction, or use the transactional outbox.",
            _buffer.Count,
            eventTypes);
    }
}
