namespace Elarion.Connections;

/// <summary>
/// A per-connection conversation inbox for codecs whose protocol flows span multiple inbound messages: the
/// receive path <see cref="Post"/>s every parsed message, and flow code awaits the next message matching a
/// predicate (<see cref="WaitAsync"/>) — "wait for the ready frame", "wait for step 2 or the abort frame"
/// (compose the predicate), with a timeout so a silent peer surfaces as a fault. Messages nobody is waiting
/// for are buffered (bounded, drop-oldest) so a waiter registered just after its message arrived still
/// finds it.
/// </summary>
/// <remarks>
/// Optional helper — nothing in the kernel requires it; it exists because every gateway codec with
/// multi-message conversations hand-rolls exactly this. Call <see cref="Complete"/> when the connection
/// ends so pending and future waiters fault instead of hanging. For keyed request/reply correlation
/// (sequence numbers), prefer <see cref="ConnectionPendingRequests{TKey, TResponse}"/>.
/// </remarks>
/// <typeparam name="TMessage">The codec's parsed message type.</typeparam>
public sealed class ConnectionInbox<TMessage>(int bufferCapacity = 64) {
    private readonly Lock _lock = new();
    private readonly List<TMessage> _buffer = [];
    private readonly List<Waiter> _waiters = [];
    private Exception? _completion;

    /// <summary>
    /// Delivers one inbound message: the first waiter whose predicate matches consumes it; otherwise it is
    /// buffered (oldest dropped beyond capacity). A no-op after <see cref="Complete"/>.
    /// </summary>
    public void Post(TMessage message) {
        Waiter? matched = null;
        lock (_lock) {
            if (_completion is not null) return;

            for (var i = 0; i < _waiters.Count; i++)
                if (_waiters[i].Match(message)) {
                    matched = _waiters[i];
                    _waiters.RemoveAt(i);
                    break;
                }

            if (matched is null) {
                _buffer.Add(message);
                if (_buffer.Count > bufferCapacity) _buffer.RemoveAt(0);
            }
        }

        matched?.Source.TrySetResult(message);
    }

    /// <summary>
    /// Awaits the next message matching <paramref name="match"/> (<see langword="null"/> = any message),
    /// consuming it. Buffered messages are checked first, in arrival order.
    /// </summary>
    /// <param name="match">The predicate a message must satisfy; compose alternatives into one predicate
    /// ("the reply or the abort frame") and branch on the returned message.</param>
    /// <param name="timeout">Faults with <see cref="TimeoutException"/> when no matching message arrives in
    /// time; <see langword="null"/> waits until cancellation or <see cref="Complete"/>.</param>
    /// <param name="ct">Cancels the wait. A message that matches concurrently with the cancellation is
    /// handed to this caller rather than lost — check the returned message even on a cancelled path if
    /// your flow must not drop it.</param>
    /// <exception cref="ConnectionInboxCompletedException">The inbox was completed (default reason).</exception>
    public async Task<TMessage> WaitAsync(
        Func<TMessage, bool>? match = null, TimeSpan? timeout = null, CancellationToken ct = default) {
        Waiter waiter;
        lock (_lock) {
            if (_completion is not null) throw _completion;

            var predicate = match ?? (static _ => true);
            for (var i = 0; i < _buffer.Count; i++)
                if (predicate(_buffer[i])) {
                    var buffered = _buffer[i];
                    _buffer.RemoveAt(i);
                    return buffered;
                }

            waiter = new Waiter(predicate);
            _waiters.Add(waiter);
        }

        try {
            return timeout is { } window
                ? await waiter.Source.Task.WaitAsync(window, ct)
                : await waiter.Source.Task.WaitAsync(ct);
        }
        catch (Exception) when (RemoveOrRace(waiter)) {
            // A message matched concurrently with the timeout/cancellation — it was consumed by this
            // waiter, so hand it over instead of losing it.
            return await waiter.Source.Task;
        }
    }

    /// <summary>
    /// Ends the inbox: pending and future waiters fault with <paramref name="error"/> (default
    /// <see cref="ConnectionInboxCompletedException"/>) and the buffer is dropped. Idempotent — call it
    /// from the codec when the connection ends.
    /// </summary>
    public void Complete(Exception? error = null) {
        Waiter[] pending;
        Exception completion;
        lock (_lock) {
            if (_completion is not null) return;

            completion = _completion = error ?? new ConnectionInboxCompletedException();
            pending = [.. _waiters];
            _waiters.Clear();
            _buffer.Clear();
        }

        foreach (var waiter in pending) waiter.Source.TrySetException(completion);
    }

    /// <summary>Returns <see langword="true"/> when a post already consumed the waiter (its message is
    /// ready — hand it over instead of losing it); <see langword="false"/> when the waiter was still
    /// registered (genuinely unmatched — it is removed here and the timeout/cancellation propagates).</summary>
    private bool RemoveOrRace(Waiter waiter) {
        lock (_lock) {
            return !_waiters.Remove(waiter);
        }
    }

    private sealed class Waiter(Func<TMessage, bool> match) {
        public Func<TMessage, bool> Match { get; } = match;

        public TaskCompletionSource<TMessage> Source { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

/// <summary>The default fault of a completed <see cref="ConnectionInbox{TMessage}"/> — the connection ended
/// while (or before) a flow was waiting.</summary>
public sealed class ConnectionInboxCompletedException()
    : Exception("The connection's inbox was completed; no further messages will arrive.");
