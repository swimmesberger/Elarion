using System.Diagnostics;
using Elarion.Actors.Diagnostics;
using Elarion.Actors.Runtime;
using Elarion.Abstractions.Results;

namespace Elarion.Actors;

/// <summary>
/// The invocation handle a generated facade holds: routes each call through the actor host to the
/// live activation for its key, so a facade stays valid across passivation/re-activation. Cheap to
/// create; facades may be cached or resolved per call.
/// </summary>
/// <typeparam name="TActor">The actor implementation type.</typeparam>
public sealed class ActorHandle<TActor> where TActor : class {
    private readonly IActorMailboxRouter<TActor> _router;
    private readonly object _key;
    private readonly ActorOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ActorCancellationPool _cancellationPool;
    private readonly string _actorName;

    internal ActorHandle(
        IActorMailboxRouter<TActor> router,
        object key,
        ActorOptions options,
        TimeProvider timeProvider,
        ActorCancellationPool cancellationPool,
        string actorName) {
        _router = router;
        _key = key;
        _options = options;
        _timeProvider = timeProvider;
        _cancellationPool = cancellationPool;
        _actorName = actorName;
    }

    /// <summary>Enqueues a result-bearing call and awaits its completion.</summary>
    public ValueTask<TResult> InvokeAsync<TResult>(
        ActorWorkItem<TActor, TResult> item,
        CancellationToken cancellationToken = default) {
        var callActivity = ActorTelemetry.StartCall(_actorName, item.MethodName, _key);
        item.Initialize(_actorName, _key, _options.CallTimeout, _cancellationPool, _timeProvider, cancellationToken);
        var enqueue = _router.EnqueueAsync(_key, item, cancellationToken);

        // Fast path (ADR-0042 roadmap): the unbounded-mailbox enqueue completes synchronously, so
        // with no trace listener attached there is nothing left to do here — hand the caller the
        // completion task directly, with no async state machine in this frame. A Task-shaped facade
        // calling .AsTask() on this ValueTask gets the underlying task back allocation-free.
        if (callActivity is null && enqueue.IsCompletedSuccessfully) {
            return new ValueTask<TResult>(item.Completion);
        }

        return AwaitAsync(item, enqueue, callActivity);
    }

    /// <summary>Enqueues a void-shaped call (a <c>Unit</c> work item) and awaits its completion.</summary>
    public ValueTask InvokeAsync(
        ActorWorkItem<TActor, Unit> item,
        CancellationToken cancellationToken = default) =>
        // On the fast path the generic call wraps the completion Task<Unit>, so AsTask() returns
        // that same instance and this conversion allocates nothing.
        new(InvokeAsync<Unit>(item, cancellationToken).AsTask());

    private static async ValueTask<TResult> AwaitAsync<TResult>(
        ActorWorkItem<TActor, TResult> item,
        ValueTask enqueue,
        Activity? callActivity) {
        try {
            try {
                await enqueue.ConfigureAwait(false);
            }
            catch (Exception ex) {
                callActivity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                item.Abandon();
                throw;
            }

            try {
                return await item.Completion.ConfigureAwait(false);
            }
            catch (Exception ex) when (MarkFailed(callActivity, ex)) {
                throw; // unreachable: MarkFailed always returns false
            }
        }
        finally {
            callActivity?.Dispose();
        }
    }

    private static bool MarkFailed(Activity? activity, Exception exception) {
        activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
        return false;
    }
}
