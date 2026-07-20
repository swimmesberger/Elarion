namespace Elarion.Buffering;

/// <summary>
/// A single-flight ownership handoff between a producer that stages state into <em>one</em>
/// preallocated, producer-owned batch object and a background writer that drains it through an async
/// delegate — the "producer-owned state snapshot" primitive of the ADR-0055 family (ADR-0069). The
/// batch's shape, staging logic, dirty flags, and reset are the application's; the flusher owns only
/// ownership transfer, signaling, the writer loop, error policy, and shutdown draining — it never
/// inspects the batch. Loss-tolerant by contract: a failed write drops that batch's staged content
/// (the producer's dirty flags re-stage current values on the next sweep — latest wins); state that
/// must not be lost belongs in a handler transaction, not here.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ownership protocol.</b> The producer may read or write the batch only while <see cref="IsIdle"/>
/// is <see langword="true"/> and no submit is pending. <see cref="TrySubmit"/> transfers ownership to
/// the writer and returns immediately — it never blocks and never allocates; <see langword="false"/>
/// means a batch is already in flight and nothing was transferred. The intended producer pattern is
/// <em>skip and retry</em>: on <see langword="false"/>, stage nothing, keep the dirty flags set, and
/// try again on the next sweep — the flags carry the state across a busy interval, which is why there
/// is deliberately no second buffer and no internal queue (queueing is
/// <see cref="WriteBehindBuffer{T}"/>'s job). After the write delegate returns <em>or throws</em>,
/// ownership returns to the producer and <see cref="IsIdle"/> becomes <see langword="true"/> with full
/// visibility of the writer's effects (volatile semantics): a producer observing idle may freely reuse
/// the batch.
/// </para>
/// <para>
/// <b>Writer loop.</b> Started at construction, owned by the flusher (no DI, no hosting dependency);
/// <see cref="DisposeAsync"/> is the lifecycle. The write delegate's token never signals — dispose
/// drains rather than cancels (the final write is the last state of departed entities); the delegate
/// bounds its own work. Write failures go to the optional <c>onFlushError</c> callback (supply it to
/// log/count — without it they are swallowed, by the loss-tolerance contract) and never tear down the
/// loop or leak ownership.
/// </para>
/// <para>
/// <b>Shutdown drains.</b> <see cref="DisposeAsync"/> lets any in-flight write run to completion,
/// writes a batch submitted before disposal, then stops the loop
/// (<see cref="StagedBatchFlusherOptions.DisposeTimeout"/> bounds the wait). Submits after dispose
/// return <see langword="false"/>. <see cref="FlushAsync"/> awaits idleness at explicit sync points
/// without disposing.
/// </para>
/// </remarks>
/// <typeparam name="TBatch">The producer-owned batch. Opaque to the flusher.</typeparam>
public sealed class StagedBatchFlusher<TBatch> : IAsyncDisposable where TBatch : class {
    private const int StateIdle = 0;
    private const int StateInFlight = 1;

    private readonly SemaphoreSlim _signal = new(0);
    private readonly Func<TBatch, CancellationToken, ValueTask> _write;
    private readonly Action<Exception, TBatch>? _onFlushError;
    private readonly Action<TBatch>? _reset;
    private readonly TimeSpan _disposeTimeout;
    private readonly TimeProvider _timeProvider;
    private readonly Task _loop;
    private TBatch? _pending;
    private TaskCompletionSource? _idleWaiter;
    private int _state;
    private volatile bool _disposed;

    /// <summary>Creates a flusher that drains submitted batches through <paramref name="write"/>.</summary>
    /// <param name="write">Receives each submitted batch. An exception drops that batch's staged content
    /// and is routed to <paramref name="onFlushError"/>; ownership still returns to the producer. The
    /// token it receives never signals (dispose drains rather than cancels) — the delegate bounds its
    /// own work.</param>
    /// <param name="options">Dispose timeout and clock; see <see cref="StagedBatchFlusherOptions"/>.</param>
    /// <param name="onFlushError">Observes write failures together with the affected batch. Without it
    /// they are swallowed — supply it to log or meter them.</param>
    /// <param name="reset">Optional convenience invoked at each ownership return (after the write
    /// completed or failed, before <see cref="IsIdle"/> becomes <see langword="true"/>) so the batch
    /// author's clear-the-sections logic lives in one place. Must not throw; an exception from it is
    /// swallowed so it can never wedge the handoff.</param>
    public StagedBatchFlusher(
        Func<TBatch, CancellationToken, ValueTask> write,
        StagedBatchFlusherOptions? options = null,
        Action<Exception, TBatch>? onFlushError = null,
        Action<TBatch>? reset = null) {
        ArgumentNullException.ThrowIfNull(write);
        options ??= new StagedBatchFlusherOptions();
        if (options.DisposeTimeout != Timeout.InfiniteTimeSpan)
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.DisposeTimeout, TimeSpan.Zero, nameof(options));

        _write = write;
        _onFlushError = onFlushError;
        _reset = reset;
        _disposeTimeout = options.DisposeTimeout;
        _timeProvider = options.TimeProvider;
        _loop = Task.Run(RunAsync);
    }

    /// <summary>
    /// Whether the producer currently owns the batch: <see langword="true"/> means no batch is in
    /// flight and staging/reading it is safe, with volatile visibility of all writer-side effects.
    /// The producer-side gate of the ownership protocol (see the type remarks).
    /// </summary>
    public bool IsIdle => Volatile.Read(ref _state) == StateIdle;

    /// <summary>
    /// Transfers ownership of the staged batch to the writer; returns immediately, never blocks, never
    /// allocates. <see langword="false"/> means a batch is already in flight (or the flusher is
    /// disposed) and nothing was transferred — keep the dirty flags set and retry on the next sweep.
    /// </summary>
    public bool TrySubmit(TBatch batch) {
        ArgumentNullException.ThrowIfNull(batch);
        if (_disposed) return false;

        if (Interlocked.CompareExchange(ref _state, StateInFlight, StateIdle) != StateIdle)
            return false;

        if (_disposed) {
            // The claim raced DisposeAsync, which may already have seen an idle loop exit — reject the
            // submit rather than strand a batch no writer will drain. The release lets a loop that is
            // instead still waiting re-evaluate its exit condition.
            Volatile.Write(ref _state, StateIdle);
            Interlocked.Exchange(ref _idleWaiter, null)?.TrySetResult();
            _signal.Release();
            return false;
        }

        Volatile.Write(ref _pending, batch);
        _signal.Release();
        return true;
    }

    /// <summary>
    /// Completes when the flusher is idle — any in-flight or just-submitted batch has been written (or
    /// dropped on failure) and ownership has returned to the producer. An explicit sync point; it does
    /// not trigger a write and does not dispose.
    /// </summary>
    public async Task FlushAsync(CancellationToken cancellationToken = default) {
        ObjectDisposedException.ThrowIf(_disposed, this);
        while (Volatile.Read(ref _state) != StateIdle) {
            var waiter = Volatile.Read(ref _idleWaiter);
            if (waiter is null) {
                var fresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                waiter = Interlocked.CompareExchange(ref _idleWaiter, fresh, null) ?? fresh;
            }

            // Re-check after installing the waiter: the writer completes waiters after flipping to idle,
            // so a transition between the loop condition and the install is never missed.
            if (Volatile.Read(ref _state) == StateIdle) return;

            await waiter.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Drains and stops the loop: an in-flight write runs to completion, a batch submitted before
    /// disposal is written (both uncancelled — they carry the last state), then the loop exits.
    /// Failures go to <c>onFlushError</c>; dispose never throws.
    /// <see cref="StagedBatchFlusherOptions.DisposeTimeout"/> bounds the wait. Subsequent submits
    /// return <see langword="false"/>.
    /// </summary>
    public async ValueTask DisposeAsync() {
        if (_disposed) return;

        _disposed = true;
        _signal.Release(); // wake the loop so it can observe the flag and drain out
        if (_disposeTimeout == Timeout.InfiniteTimeSpan) {
            await _loop.ConfigureAwait(false);
        }
        else {
            try {
                await _loop.WaitAsync(_disposeTimeout, _timeProvider).ConfigureAwait(false);
            }
            catch (TimeoutException) {
                // Abandon the wait: the wedged write may still complete in the background, but shutdown
                // is not held hostage — the documented DisposeTimeout contract.
            }
        }
    }

    // Never faults: the write delegate and both callbacks are guarded, so the loop task is
    // deliberately observed-by-construction.
    private async Task RunAsync() {
        while (true) {
            await _signal.WaitAsync().ConfigureAwait(false);
            var batch = Interlocked.Exchange(ref _pending, null);
            if (batch is not null) {
                try {
                    // Deliberately uncancellable: dispose drains rather than cancels, and a mid-write
                    // cancellation would leave the batch half-applied; the delegate bounds its own work.
                    await _write(batch, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception) {
                    try {
                        _onFlushError?.Invoke(exception, batch);
                    }
                    catch {
                        // A throwing error callback must never tear down the writer loop.
                    }
                }

                try {
                    _reset?.Invoke(batch);
                }
                catch {
                    // A throwing reset callback must never wedge the handoff in the in-flight state.
                }

                Volatile.Write(ref _state, StateIdle); // ownership returns; producers see all effects above
                Interlocked.Exchange(ref _idleWaiter, null)?.TrySetResult();
            }

            // A dispose wake with a submit still mid-publish (state claimed, batch not yet visible)
            // keeps looping: the submitter's Release is still coming and the batch must be written.
            if (_disposed && Volatile.Read(ref _state) == StateIdle) return;
        }
    }
}
