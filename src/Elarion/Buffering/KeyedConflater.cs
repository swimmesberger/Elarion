using System.Collections.Concurrent;

namespace Elarion.Buffering;

/// <summary>
/// A keyed latest-wins conflater with a maximum publish rate: producers <see cref="Post"/> hot values from
/// any thread, and each key emits through an async delegate at most once per
/// <see cref="KeyedConflaterOptions.MinInterval"/> — immediately when the key is idle, conflated to the
/// newest value while the window is open. A quiet key always emits its final value once the window
/// elapses, so conflation never ends on a stale value. The delegate's natural body is a client-event
/// publish (ADR-0043) — the "live visualization" primitive that keeps a hot stream from drowning the UI
/// (ADR-0055).
/// </summary>
/// <remarks>
/// <para>
/// <b>Per-key serialization.</b> Emissions for one key never overlap: while a publish is in flight, newer
/// posts conflate, and the newest value emits when both the publish and the rate window allow — so a slow
/// publish target lowers the effective rate instead of stacking calls. Keys are independent; idle keys are
/// retired automatically, so unbounded key spaces do not leak.
/// </para>
/// <para>
/// <b>At-most-once delivery.</b> A publish-delegate exception drops that emission and goes to the optional
/// <c>onPublishError</c> callback (supply it to log/count — without it failures are swallowed; these are
/// hints, and the next post heals). <see cref="DisposeAsync"/> flushes every pending latest value
/// (ignoring windows) before returning and never throws; posts after dispose are dropped.
/// </para>
/// </remarks>
/// <typeparam name="TKey">The conflation key (symbol, device id, …).</typeparam>
/// <typeparam name="TValue">The hot value; only the newest per key survives a window.</typeparam>
public sealed class KeyedConflater<TKey, TValue> : IAsyncDisposable where TKey : notnull {
    private readonly ConcurrentDictionary<TKey, KeyState> _states = new();
    private readonly Lock _disposeLock = new();
    private readonly Func<TKey, TValue, CancellationToken, ValueTask> _publish;
    private readonly Action<Exception, TKey, TValue>? _onPublishError;
    private readonly TimeSpan _minInterval;
    private readonly TimeProvider _timeProvider;
    private volatile bool _disposed;

    /// <summary>Creates a conflater that emits through <paramref name="publish"/>.</summary>
    /// <param name="publish">Receives each emitted (key, latest value). An exception drops that emission
    /// and is routed to <paramref name="onPublishError"/>. The token it receives never signals (dispose
    /// flushes pending values rather than cancelling them) — the delegate bounds its own work.</param>
    /// <param name="options">Rate window and clock; see <see cref="KeyedConflaterOptions"/>.</param>
    /// <param name="onPublishError">Observes publish failures with the affected key/value. Without it they
    /// are swallowed — supply it to log or meter them.</param>
    public KeyedConflater(
        Func<TKey, TValue, CancellationToken, ValueTask> publish,
        KeyedConflaterOptions? options = null,
        Action<Exception, TKey, TValue>? onPublishError = null) {
        ArgumentNullException.ThrowIfNull(publish);
        options ??= new KeyedConflaterOptions();
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MinInterval, TimeSpan.Zero, nameof(options));

        _publish = publish;
        _onPublishError = onPublishError;
        _minInterval = options.MinInterval;
        _timeProvider = options.TimeProvider;
    }

    /// <summary>
    /// Posts the newest value for a key: emits immediately when the key is idle, otherwise replaces the
    /// key's pending value (latest wins) to emit when the rate window and any in-flight publish allow.
    /// Never blocks on the publish target. Dropped silently after <see cref="DisposeAsync"/>.
    /// </summary>
    public void Post(TKey key, TValue value) {
        if (_disposed) return;

        while (true) {
            var state = _states.GetOrAdd(key, static _ => new KeyState());
            lock (state.Lock) {
                // Re-checked under the state lock: dispose sets the flag before draining, so a post that
                // passed the lock-free check above cannot start an emission after the drain completed.
                if (_disposed) {
                    if (!state.Publishing && !state.HasPending)
                        RemoveLocked(key, state); // don't strand an idle state this racing post inserted

                    return;
                }

                if (state.Removed) continue; // retired concurrently as idle — retry against a fresh state

                if (state.Publishing || state.WindowOpen) {
                    state.Pending = value;
                    state.HasPending = true;
                }
                else {
                    StartEmitLocked(key, state, value);
                }

                return;
            }
        }
    }

    /// <summary>
    /// Flushes every key's pending latest value (ignoring rate windows), waits for all in-flight publishes,
    /// and retires the keys; failures go to <c>onPublishError</c> (dispose never throws). Subsequent posts
    /// are dropped — conflation ends with the newest value delivered, never a stale one. A concurrent
    /// second dispose returns immediately without waiting for the first caller's drain.
    /// </summary>
    public async ValueTask DisposeAsync() {
        lock (_disposeLock) {
            if (_disposed) return;

            _disposed = true;
        }

        // Drain until quiet: emit-on-completion chains (and posts that raced the flag) can surface new
        // pending values, so re-snapshot until nothing is in flight anymore.
        while (true) {
            List<Task> inFlight = [];
            foreach (var (key, state) in _states)
                lock (state.Lock) {
                    if (state.Removed) continue;

                    if (!state.Publishing && state.HasPending) {
                        var value = state.Pending!;
                        state.HasPending = false;
                        state.Pending = default;
                        StartEmitLocked(key, state, value);
                    }

                    if (state.InFlight is { } task) inFlight.Add(task);
                }

            if (inFlight.Count == 0) break;

            await Task.WhenAll(inFlight).ConfigureAwait(false);
        }

        foreach (var (key, state) in _states)
            lock (state.Lock) {
                RemoveLocked(key, state);
            }
    }

    private void StartEmitLocked(TKey key, KeyState state, TValue value) {
        state.Publishing = true;
        state.WindowOpen = true;
        state.Timer ??= _timeProvider.CreateTimer(
            s => OnWindowElapsed(key, (KeyState)s!), state, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        state.Timer.Change(_minInterval, Timeout.InfiniteTimeSpan);
        state.InFlight = EmitAsync(key, state, value);
    }

    private async Task EmitAsync(TKey key, KeyState state, TValue value) {
        // Yield first so the publish delegate never runs inside the state lock StartEmitLocked's caller holds.
        await Task.Yield();
        try {
            // Deliberately uncancellable: dispose flushes pending values rather than cancelling them; the
            // delegate bounds its own work.
            await _publish(key, value, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) {
            try {
                _onPublishError?.Invoke(exception, key, value);
            }
            catch {
                // A throwing error callback must never wedge the key in the publishing state.
            }
        }

        OnEmitCompleted(key, state);
    }

    private void OnEmitCompleted(TKey key, KeyState state) {
        lock (state.Lock) {
            state.Publishing = false;
            state.InFlight = null;
            if (state.HasPending && (!state.WindowOpen || _disposed)) {
                // The window elapsed while the publish was in flight (slow target) — the pending latest
                // emits now, so the effective rate degrades to the publish duration instead of stalling.
                var value = state.Pending!;
                state.HasPending = false;
                state.Pending = default;
                StartEmitLocked(key, state, value);
            }
            else if (!state.HasPending && !state.WindowOpen) {
                RemoveLocked(key, state);
            }
        }
    }

    private void OnWindowElapsed(TKey key, KeyState state) {
        lock (state.Lock) {
            state.WindowOpen = false;
            if (state.Publishing) return; // completion emits the pending latest or retires the key

            if (state.HasPending) {
                var value = state.Pending!;
                state.HasPending = false;
                state.Pending = default;
                StartEmitLocked(key, state, value); // the trailing emit — a quiet key never ends stale
            }
            else {
                RemoveLocked(key, state);
            }
        }
    }

    private void RemoveLocked(TKey key, KeyState state) {
        state.Removed = true;
        state.Timer?.Dispose();
        _states.TryRemove(new KeyValuePair<TKey, KeyState>(key, state));
    }

    private sealed class KeyState {
        public Lock Lock { get; } = new();
        public ITimer? Timer { get; set; }
        public Task? InFlight { get; set; }
        public TValue? Pending { get; set; }
        public bool HasPending { get; set; }
        public bool Publishing { get; set; }
        public bool WindowOpen { get; set; }
        public bool Removed { get; set; }
    }
}
