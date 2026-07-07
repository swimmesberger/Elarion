using System.Collections.Concurrent;

namespace Elarion.Actors.Runtime;

/// <summary>
/// A bounded free-list of reusable work items, one instantiation per generated work-item type. The
/// generated facade rents an item, sets the call arguments, and enqueues it; the runtime returns it
/// here once the actor finishes (via <see cref="ActorWorkItem{TActor,TResult}"/>'s recycle hook).
/// Because a call's result lives in the item's <see cref="System.Threading.Tasks.TaskCompletionSource{TResult}"/>
/// and the caller captures that task before enqueue, reuse never disturbs an in-flight await
/// (ADR-0042 roadmap). The cap mirrors <see cref="ActorCancellationPool"/> so a burst cannot retain
/// items unbounded.
/// </summary>
/// <remarks>Public because generated code in consuming assemblies calls it; not part of the app-facing API.</remarks>
/// <typeparam name="TItem">The concrete work-item type being pooled.</typeparam>
public static class ActorWorkItemPool<TItem> where TItem : class {
    private const int MaxPooled = 256;

    private static readonly ConcurrentQueue<TItem> Pool = new();
    private static int _count;

    /// <summary>Rents a pooled item, or creates one via <paramref name="factory"/> on a miss.</summary>
    public static TItem Rent(Func<TItem> factory) {
        if (Pool.TryDequeue(out var item)) {
            Interlocked.Decrement(ref _count);
            return item;
        }

        return factory();
    }

    /// <summary>Returns an item for reuse, dropping it when the pool is at capacity.</summary>
    public static void Return(TItem item) {
        if (Interlocked.Increment(ref _count) <= MaxPooled) {
            Pool.Enqueue(item);
            return;
        }

        Interlocked.Decrement(ref _count);
    }
}
