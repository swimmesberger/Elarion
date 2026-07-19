namespace Elarion.Buffering;

/// <summary>
/// A write-behind buffer for loss-tolerant, high-frequency samples: producers <see cref="Add"/> from any
/// thread, and the buffer flushes accumulated batches through an async delegate when
/// <see cref="WriteBehindBufferOptions.MaxItems"/> or <see cref="WriteBehindBufferOptions.FlushInterval"/>
/// is reached — whichever comes first. The delegate's natural body is a set-based database write
/// (ADR-0051 <c>ExecuteInsertAsync</c>); the natural owner is the actor or gateway component that
/// produces the samples (ADR-0055).
/// </summary>
/// <remarks>
/// <para>
/// <b>Loss-tolerant by contract.</b> The buffer is bounded: past
/// <see cref="WriteBehindBufferOptions.Capacity"/> the oldest unflushed item is dropped
/// (<see cref="DroppedCount"/> counts them), and a failed flush drops its batch rather than retrying it
/// (a poisoned batch must never wedge the pipeline). Samples that must not be lost belong in a
/// transaction, not here.
/// </para>
/// <para>
/// <b>Flushes are single-flight.</b> One flush runs at a time; items added while a flush is in progress
/// are picked up by the same drain pass (or the next trigger), so a slow flush target coalesces work
/// instead of stacking calls. Background flush failures go to the optional <c>onFlushError</c> callback
/// (supply it to log/count — without it they are swallowed, by the loss-tolerance contract); an explicit
/// <see cref="FlushAsync"/> rethrows to its caller instead.
/// </para>
/// <para>
/// <b>Shutdown flushes.</b> <see cref="DisposeAsync"/> stops the timer, flushes the remaining tail
/// (uncancellable — shutdown writes the tail rather than dropping it; the delegate bounds its own work),
/// and routes any failure to <c>onFlushError</c>. Adds after dispose are dropped silently — producers
/// racing a shutdown must not crash the receive path.
/// </para>
/// </remarks>
/// <typeparam name="T">The sample type.</typeparam>
public sealed class WriteBehindBuffer<T> : IAsyncDisposable {
    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private readonly Queue<T> _items = new();
    private readonly Func<IReadOnlyList<T>, CancellationToken, ValueTask> _flush;
    private readonly Action<Exception, IReadOnlyList<T>>? _onFlushError;
    private readonly int _maxItems;
    private readonly int _capacity;
    private readonly TimeSpan _flushInterval;
    private readonly ITimer? _timer;
    private bool _timerArmed;
    private bool _flushQueued;
    private volatile bool _disposed;
    private long _droppedCount;

    /// <summary>Creates a buffer that flushes through <paramref name="flush"/>.</summary>
    /// <param name="flush">Receives each accumulated batch (never empty). An exception drops the batch —
    /// rethrown from <see cref="FlushAsync"/>, routed to <paramref name="onFlushError"/> otherwise. The
    /// token it receives signals only when an explicit <see cref="FlushAsync"/> caller cancels; background
    /// and dispose flushes pass a token that never signals — the delegate bounds its own work.</param>
    /// <param name="options">Batch size, interval, and bound; see <see cref="WriteBehindBufferOptions"/>.</param>
    /// <param name="onFlushError">Observes background/dispose flush failures together with the dropped
    /// batch. Without it those failures are swallowed — supply it to log or meter them.</param>
    public WriteBehindBuffer(
        Func<IReadOnlyList<T>, CancellationToken, ValueTask> flush,
        WriteBehindBufferOptions? options = null,
        Action<Exception, IReadOnlyList<T>>? onFlushError = null) {
        ArgumentNullException.ThrowIfNull(flush);
        options ??= new WriteBehindBufferOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxItems, 1, nameof(options));
        if (options.FlushInterval != Timeout.InfiniteTimeSpan)
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.FlushInterval, TimeSpan.Zero, nameof(options));

        var capacity = options.Capacity ?? (int)Math.Min(options.MaxItems * 4L, int.MaxValue);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, options.MaxItems, nameof(options));

        _flush = flush;
        _onFlushError = onFlushError;
        _maxItems = options.MaxItems;
        _capacity = capacity;
        _flushInterval = options.FlushInterval;
        if (_flushInterval != Timeout.InfiniteTimeSpan)
            _timer = options.TimeProvider.CreateTimer(
                static state => ((WriteBehindBuffer<T>)state!).OnIntervalElapsed(),
                this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Currently buffered (unflushed) items.</summary>
    public int Count {
        get {
            lock (_lock) {
                return _items.Count;
            }
        }
    }

    /// <summary>
    /// Items dropped so far — oldest-first evictions past the capacity bound. A steadily climbing value
    /// means the flush target cannot keep up with the sample rate.
    /// </summary>
    public long DroppedCount {
        get {
            lock (_lock) {
                return _droppedCount;
            }
        }
    }

    /// <summary>
    /// Buffers one item: flushes when the batch size is reached, otherwise arms the interval timer so the
    /// item flushes within <see cref="WriteBehindBufferOptions.FlushInterval"/>. Never blocks on the flush
    /// target. Dropped silently after <see cref="DisposeAsync"/>.
    /// </summary>
    public void Add(T item) {
        var queueFlush = false;
        var armTimer = false;
        lock (_lock) {
            if (_disposed) return;

            _items.Enqueue(item);
            if (_items.Count > _capacity) {
                _items.Dequeue();
                _droppedCount++;
            }

            if (_items.Count >= _maxItems) {
                if (!_flushQueued) {
                    _flushQueued = true;
                    queueFlush = true;
                }
            }
            else if (!_timerArmed && _timer is not null) {
                _timerArmed = true;
                armTimer = true;
            }
        }

        if (armTimer)
            // May race DisposeAsync's timer disposal: ITimer.Change after Dispose returns false rather
            // than throwing (system provider and FakeTimeProvider alike), keeping a racing producer
            // crash-free per the dropped-after-dispose contract.
            _timer!.Change(_flushInterval, Timeout.InfiniteTimeSpan);

        if (queueFlush) _ = FlushInBackgroundAsync();
    }

    /// <summary>
    /// Flushes everything buffered right now and returns when it reached the target (waiting first for any
    /// in-flight flush — note a batch already taken by a concurrent background flush reports its failure
    /// through <c>onFlushError</c>, not here). A flush-delegate exception propagates to this caller; its
    /// batch is dropped — cancelling this call mid-flush drops the in-flight batch too.
    /// </summary>
    public Task FlushAsync(CancellationToken cancellationToken = default) {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return DrainAsync(true, cancellationToken);
    }

    /// <summary>
    /// Stops the interval timer and flushes the remaining tail; failures go to <c>onFlushError</c>
    /// (dispose never throws). Subsequent adds are dropped. A concurrent second dispose returns
    /// immediately without waiting for the first caller's final flush.
    /// </summary>
    public async ValueTask DisposeAsync() {
        lock (_lock) {
            if (_disposed) return;

            _disposed = true;
        }

        _timer?.Dispose();
        await DrainAsync(false, CancellationToken.None).ConfigureAwait(false);
    }

    private void OnIntervalElapsed() {
        lock (_lock) {
            _timerArmed = false;
            if (_disposed || _items.Count == 0 || _flushQueued) return;

            _flushQueued = true;
        }

        _ = FlushInBackgroundAsync();
    }

    // Never faults: DrainAsync routes delegate exceptions to the callback when rethrow is off, so the
    // discarded task is deliberately observed-by-construction.
    private async Task FlushInBackgroundAsync() {
        // Off the producer's thread before touching the gate/delegate — Add never blocks on the flush target.
        await Task.Yield();
        await DrainAsync(false, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task DrainAsync(bool rethrow, CancellationToken cancellationToken) {
        await _flushGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try {
            while (true) {
                T[] batch;
                lock (_lock) {
                    _flushQueued = false;
                    if (_items.Count == 0) return;

                    batch = [.. _items];
                    _items.Clear();
                }

                try {
                    await _flush(batch, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (!rethrow) {
                    try {
                        _onFlushError?.Invoke(exception, batch);
                    }
                    catch {
                        // A throwing error callback must never fault the background flush task.
                    }
                }
            }
        }
        finally {
            _flushGate.Release();
        }
    }
}
