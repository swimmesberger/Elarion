using System.Diagnostics;
using Elarion.Actors.Diagnostics;
using Elarion.Actors.Runtime;

namespace Elarion.Actors;

/// <summary>How a turn ended, as observed by the cell (the caller's completion is handled inside the item).</summary>
internal enum ActorTurnOutcome {
    /// <summary>The turn completed (result, error, or cancellation delivered to the caller).</summary>
    Completed,

    /// <summary>
    /// The turn failed with <see cref="ActorSnapshotConcurrencyException"/> and the caller has NOT
    /// been completed: the cell passivates this stale activation and re-enqueues the item, so the
    /// turn re-runs once against a fresh activation that loaded the winning snapshot (ADR-0047).
    /// </summary>
    SnapshotConflictRetry,

    /// <summary>
    /// The retried turn conflicted again (live contention / sustained double-hosting): the caller
    /// got the exception; the cell still passivates so the next activation reloads.
    /// </summary>
    SnapshotConflictFailed
}

/// <summary>
/// A single queued actor call. Generated facade implementations subclass the result-typed
/// <see cref="ActorWorkItem{TActor, TResult}"/> per method; application code never touches work
/// items directly.
/// </summary>
/// <typeparam name="TActor">The actor implementation type.</typeparam>
public abstract class ActorWorkItem<TActor> where TActor : class {
    private protected ActorWorkItem() { }

    /// <summary>The invoked actor method name (telemetry identity).</summary>
    public abstract string MethodName { get; }

    internal abstract void Initialize(
        string actorName,
        object key,
        TimeSpan? callTimeout,
        ActorCancellationPool cancellationPool,
        TimeProvider timeProvider,
        CancellationToken callerToken);

    internal abstract ValueTask<ActorTurnOutcome> RunAsync(TActor actor, CancellationToken stopping);

    internal abstract void TryFail(Exception exception);

    internal abstract void Abandon();
}

/// <summary>
/// The result-typed actor work item: carries the call's completion, cancellation/timeout wiring,
/// and telemetry capture. A generated subclass stores the method arguments and overrides
/// <see cref="InvokeAsync"/> with a direct, statically-typed call — no reflection, no message
/// envelope types, AOT-safe.
/// </summary>
/// <remarks>
/// Cancellation design (ADR-0042 roadmap: the timeout CTS was the largest per-call allocation): a
/// single <em>pooled</em> <see cref="CancellationTokenSource"/> is the invocation token. The call
/// timeout arms it via <c>CancelAfter</c>, caller cancellation and the activation's stopping token
/// cancel it through registrations, and an attribution callback decides whether the caller sees
/// canceled or <see cref="TimeoutException"/>. On the happy path it is never canceled and returns
/// to the pool via <c>TryReset</c> — no CTS, timer, or linked-source allocation per call.
/// </remarks>
/// <typeparam name="TActor">The actor implementation type.</typeparam>
/// <typeparam name="TResult">The method result type (<c>Unit</c> for void-shaped methods).</typeparam>
public abstract class ActorWorkItem<TActor, TResult> : ActorWorkItem<TActor> where TActor : class {
    // The actor-side exception is set with TrySetException, so the caller's await rethrows it with
    // the original actor-side stack trace ("end of stack trace from previous location" + caller
    // frames) — the cross-mailbox stack-trace story needs no wrapper exception.
    //
    // Reassigned per call (in Initialize) so a *pooled* work item hands each caller a fresh
    // completion. The caller captures this Task before the item is enqueued, so the result lives
    // independently of the work item and recycling the item after the actor finishes can never
    // disturb an in-flight await — which is why pooling here needs no IValueTaskSource and keeps
    // AsTask() allocation-free (unlike a source-backed ValueTask).
    private TaskCompletionSource<TResult> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private string _actorName = string.Empty;
    private object? _key;
    private TimeProvider _timeProvider = TimeProvider.System;
    private ActorCancellationPool? _cancellationPool;
    private ActivityContext _callerContext;
    private long _enqueuedTimestamp;
    private CancellationTokenSource? _invocationCts;
    private bool _timeoutArmed;
    private CancellationToken _callerToken;
    private CancellationToken _stoppingToken;
    private CancellationTokenRegistration _attributionRegistration;
    private CancellationTokenRegistration _callerRegistration;
    private CancellationTokenRegistration _stoppingRegistration;
    // Set when a snapshot conflict already consumed this item's one transparent retry. Reset in
    // Initialize (pooled reuse), NOT between the two attempts of one call.
    private bool _snapshotRetryAttempted;

    internal Task<TResult> Completion => _completion.Task;

    /// <summary>Invokes the actor method with the already-stored arguments.</summary>
    protected abstract ValueTask<TResult> InvokeAsync(TActor actor, CancellationToken cancellationToken);

    internal override void Initialize(
        string actorName,
        object key,
        TimeSpan? callTimeout,
        ActorCancellationPool cancellationPool,
        TimeProvider timeProvider,
        CancellationToken callerToken) {
        // Reset the per-call state a pooled reuse would otherwise inherit (a fresh completion, and
        // no leftover timeout/cancellation wiring). Registrations are already default after Cleanup.
        _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _invocationCts = null;
        _timeoutArmed = false;
        _snapshotRetryAttempted = false;
        _actorName = actorName;
        _key = key;
        _timeProvider = timeProvider;
        _callerToken = callerToken;
        _callerContext = Activity.Current?.Context ?? default;
        _enqueuedTimestamp = timeProvider.GetTimestamp();

        if (callTimeout is null && !callerToken.CanBeCanceled) {
            return; // fast path: the invocation token is just the activation's stopping token
        }

        _cancellationPool = cancellationPool;
        _invocationCts = cancellationPool.Rent();
        if (callTimeout is { } timeout) {
            _timeoutArmed = true;
            _invocationCts.CancelAfter(timeout);
        }

        // Fires on ANY cancellation of the invocation token (timeout, caller, stopping) and
        // completes the caller eagerly with the right outcome; the sources are checked because a
        // canceled token itself carries no "why".
        _attributionRegistration = _invocationCts.Token.UnsafeRegister(static state => {
            var self = (ActorWorkItem<TActor, TResult>)state!;
            self.OnInvocationCanceled();
        }, this);

        if (callerToken.CanBeCanceled) {
            _callerRegistration = callerToken.UnsafeRegister(static state => {
                var self = (ActorWorkItem<TActor, TResult>)state!;
                self._completion.TrySetCanceled(self._callerToken);
                self._invocationCts?.Cancel();
            }, this);
        }
    }

    internal override async ValueTask<ActorTurnOutcome> RunAsync(TActor actor, CancellationToken stopping) {
        // Canceled or timed out while queued: skip execution entirely.
        if (_completion.Task.IsCompleted) {
            Cleanup();
            return ActorTurnOutcome.Completed;
        }

        if (stopping.IsCancellationRequested) {
            _completion.TrySetCanceled(stopping);
            Cleanup();
            return ActorTurnOutcome.Completed;
        }

        ActorTelemetry.RecordQueueWait(
            _actorName, MethodName, _timeProvider.GetElapsedTime(_enqueuedTimestamp));
        using var activity = ActorTelemetry.StartProcess(_actorName, MethodName, _key, _callerContext);
        var startTimestamp = _timeProvider.GetTimestamp();
        var outcome = "ok";
        var turnOutcome = ActorTurnOutcome.Completed;
        try {
            CancellationToken token;
            if (_invocationCts is null) {
                token = stopping;
            }
            else {
                _stoppingToken = stopping;
                // A retried turn runs on a NEW activation with a new stopping token: drop the
                // previous attempt's registration before wiring the current one (no-op first time).
                _stoppingRegistration.Dispose();
                if (stopping.CanBeCanceled) {
                    _stoppingRegistration = stopping.UnsafeRegister(static state =>
                        ((ActorWorkItem<TActor, TResult>)state!)._invocationCts?.Cancel(), this);
                }

                token = _invocationCts.Token;
            }

            var result = await InvokeAsync(actor, token).ConfigureAwait(false);
            if (!_completion.TrySetResult(result)) {
                outcome = "abandoned";
            }
        }
        catch (OperationCanceledException oce) {
            // The method observed the invocation token — attribute the cancellation here instead of
            // racing the attribution registration: a timeout must surface as TimeoutException even
            // when this catch runs before that callback.
            if (_invocationCts is { IsCancellationRequested: true }) {
                OnInvocationCanceled();
            }

            _completion.TrySetCanceled(oce.CancellationToken.CanBeCanceled ? oce.CancellationToken : stopping);
            outcome = _completion.Task.IsFaulted ? "timeout" : "canceled";
            activity?.SetStatus(ActivityStatusCode.Error, outcome);
        }
        catch (Exception ex) {
            if (activity is not null) {
                activity.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity.AddException(ex);
            }

            if (ex is ActorSnapshotConcurrencyException) {
                ActorTelemetry.RecordSnapshotConflict(_actorName);
                if (!_snapshotRetryAttempted && !_completion.Task.IsCompleted) {
                    // Greenfield conflict semantics (ADR-0047): the caller is NOT completed. The
                    // cell passivates this stale activation and re-enqueues the item, so the turn
                    // re-runs once against a fresh activation that loaded the winning snapshot.
                    // The call timeout stays armed across both attempts, bounding the whole call.
                    _snapshotRetryAttempted = true;
                    outcome = "snapshot_conflict";
                    turnOutcome = ActorTurnOutcome.SnapshotConflictRetry;
                }
                else {
                    // Second consecutive conflict: live contention or sustained double-hosting —
                    // now the caller sees it.
                    outcome = "error";
                    turnOutcome = ActorTurnOutcome.SnapshotConflictFailed;
                    _completion.TrySetException(ex);
                }
            }
            else {
                outcome = "error";
                _completion.TrySetException(ex);
            }
        }
        finally {
            // Record telemetry off this item's fields BEFORE Cleanup, because Cleanup recycles the
            // item and another caller may immediately re-Initialize it (overwriting _actorName /
            // _timeProvider). Cleanup must be the last touch of this instance on every path.
            ActorTelemetry.RecordMessage(
                _actorName, MethodName, outcome, _timeProvider.GetElapsedTime(startTimestamp));
            if (turnOutcome == ActorTurnOutcome.SnapshotConflictRetry) {
                // The item lives on into its retry: no Cleanup/Recycle, and the queue-wait clock
                // restarts so the second attempt measures its own re-queue time.
                _enqueuedTimestamp = _timeProvider.GetTimestamp();
            }
            else {
                Cleanup();
            }
        }

        return turnOutcome;
    }

    internal override void TryFail(Exception exception) {
        _completion.TrySetException(exception);
        Cleanup();
    }

    internal override void Abandon() {
        // Canceled (not faulted) so an item dropped before enqueue never surfaces as an unobserved
        // task exception — the caller already got the enqueue failure directly.
        _completion.TrySetCanceled(CancellationToken.None);
        Cleanup();
    }

    private void OnInvocationCanceled() {
        if (_callerToken.IsCancellationRequested) {
            _completion.TrySetCanceled(_callerToken);
        }
        else if (_stoppingToken.IsCancellationRequested) {
            _completion.TrySetCanceled(_stoppingToken);
        }
        else if (_timeoutArmed) {
            _completion.TrySetException(new TimeoutException(
                $"Actor call '{_actorName}.{MethodName}' timed out. If two actors await each " +
                "other this is the deadlock backstop; break the cycle or mark an actor [Reentrant]."));
        }
    }

    private void Cleanup() {
        // Registration disposal blocks until an in-flight callback finishes, so after these three
        // disposals nothing can touch the source anymore and it is safe to recycle.
        _callerRegistration.Dispose();
        _stoppingRegistration.Dispose();
        _attributionRegistration.Dispose();
        if (_invocationCts is { } source) {
            _invocationCts = null;
            _cancellationPool!.Return(source);
        }

        // The actor is done with this item and the caller already holds its Task, so a pooling
        // subclass may return itself for reuse now. Runs on every terminal path (result, cancel,
        // timeout, abandon, fail).
        Recycle();
    }

    /// <summary>
    /// Hook for a pooling work-item subclass to return itself to its pool. Called once the item is
    /// fully done. The default keeps the item as plain garbage (no pooling).
    /// </summary>
    protected virtual void Recycle() { }
}
