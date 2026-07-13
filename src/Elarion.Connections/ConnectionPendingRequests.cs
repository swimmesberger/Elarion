using System.Collections.Concurrent;

namespace Elarion.Connections;

/// <summary>
/// Keyed request/reply correlation for codecs — the sequence-number → completion map every gateway protocol
/// hand-rolls: the sender registers the key and awaits (<see cref="WaitAsync"/>), the receive path completes
/// it when the correlated reply arrives (<see cref="TryComplete"/>), and connection teardown faults
/// everything in flight (<see cref="FailAll"/>). This is the natural backing for an
/// <c>IClientConnectionProtocol.InvokeAsync</c> implementation.
/// </summary>
/// <remarks>
/// One in-flight request per key: registering a key that is already pending throws — a duplicate sequence
/// number is a codec bug, not a race to tolerate. Serial one-in-flight protocols enforce their send
/// ordering separately (a send queue/semaphore); this type only correlates.
/// </remarks>
/// <typeparam name="TKey">The correlation key (a sequence number, a request id).</typeparam>
/// <typeparam name="TResponse">The codec's reply type.</typeparam>
public sealed class ConnectionPendingRequests<TKey, TResponse> where TKey : notnull {
    private readonly ConcurrentDictionary<TKey, TaskCompletionSource<TResponse>> _pending = new();
    private Exception? _completion;

    /// <summary>
    /// Registers <paramref name="key"/> and awaits its reply. Send the request <b>after</b> calling this
    /// (register-then-send closes the fast-reply race).
    /// </summary>
    /// <param name="key">The correlation key the reply will carry.</param>
    /// <param name="timeout">Faults with <see cref="TimeoutException"/> when no reply arrives in time —
    /// a server→client invoke is never unbounded.</param>
    /// <param name="ct">Cancels the wait and withdraws the registration.</param>
    /// <exception cref="InvalidOperationException">The key is already in flight.</exception>
    public async Task<TResponse> WaitAsync(TKey key, TimeSpan? timeout = null, CancellationToken ct = default) {
        if (Volatile.Read(ref _completion) is { } alreadyFailed) {
            throw alreadyFailed;
        }

        var source = new TaskCompletionSource<TResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(key, source)) {
            throw new InvalidOperationException(
                $"A request with key '{key}' is already in flight — duplicate correlation keys are a codec bug.");
        }

        // FailAll may have swept between the completion check and the add; re-check so the registration
        // cannot outlive the teardown.
        if (Volatile.Read(ref _completion) is { } failed && _pending.TryRemove(new KeyValuePair<TKey, TaskCompletionSource<TResponse>>(key, source))) {
            throw failed;
        }

        try {
            return timeout is { } window
                ? await source.Task.WaitAsync(window, ct)
                : await source.Task.WaitAsync(ct);
        }
        catch (Exception) when (!_pending.TryRemove(new KeyValuePair<TKey, TaskCompletionSource<TResponse>>(key, source))) {
            // The reply raced the timeout/cancellation and already completed this registration — hand it
            // over instead of losing it.
            return await source.Task;
        }
    }

    /// <summary>Completes the in-flight request for <paramref name="key"/>; <see langword="false"/> when
    /// nothing was waiting (late or unsolicited reply — the codec decides whether that is an error).</summary>
    public bool TryComplete(TKey key, TResponse response) =>
        _pending.TryRemove(key, out var source) && source.TrySetResult(response);

    /// <summary>Faults the in-flight request for <paramref name="key"/> (e.g. the peer answered with a
    /// protocol-level rejection).</summary>
    public bool TryFail(TKey key, Exception error) =>
        _pending.TryRemove(key, out var source) && source.TrySetException(error);

    /// <summary>
    /// Faults everything in flight and every future <see cref="WaitAsync"/> with <paramref name="error"/> —
    /// call from the codec when the connection ends (typically with a
    /// <see cref="Elarion.Abstractions.Connections.ClientConnectionClosedException"/>). Idempotent; the
    /// first error wins.
    /// </summary>
    public void FailAll(Exception error) {
        ArgumentNullException.ThrowIfNull(error);
        if (Interlocked.CompareExchange(ref _completion, error, null) is not null) {
            return;
        }

        foreach (var key in _pending.Keys) {
            if (_pending.TryRemove(key, out var source)) {
                source.TrySetException(error);
            }
        }
    }
}
