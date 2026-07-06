using System.Collections.Concurrent;

namespace Elarion.Actors.Runtime;

/// <summary>
/// Pools the per-call <see cref="CancellationTokenSource"/> that backs call timeouts and caller
/// cancellation. The first benchmark baseline showed this CTS (+ its timer) as the largest single
/// per-call allocation (ADR-0042 roadmap), and the happy path never cancels it — so
/// <see cref="CancellationTokenSource.TryReset"/> recycles it. Sources are created against the
/// runtime's <see cref="TimeProvider"/> (the provider is captured at construction and honored by
/// <c>CancelAfter</c>), which keeps fake-clock tests deterministic.
/// </summary>
internal sealed class ActorCancellationPool(TimeProvider timeProvider) {
    private const int MaxPooled = 256;

    private readonly ConcurrentQueue<CancellationTokenSource> _pool = new();
    private int _count;

    internal CancellationTokenSource Rent() {
        if (_pool.TryDequeue(out var source)) {
            Interlocked.Decrement(ref _count);
            return source;
        }

        return new CancellationTokenSource(Timeout.InfiniteTimeSpan, timeProvider);
    }

    /// <summary>
    /// Returns a source to the pool when it can be reset (never canceled; a pending
    /// <c>CancelAfter</c> timer is disarmed by <see cref="CancellationTokenSource.TryReset"/>).
    /// The caller must have disposed every registration on the source's token first.
    /// </summary>
    internal void Return(CancellationTokenSource source) {
        if (source.TryReset()) {
            if (Interlocked.Increment(ref _count) <= MaxPooled) {
                _pool.Enqueue(source);
                return;
            }

            Interlocked.Decrement(ref _count);
        }

        source.Dispose();
    }
}
