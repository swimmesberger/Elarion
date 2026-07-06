using System.Diagnostics;
using Elarion.Actors.Diagnostics;
using Elarion.Actors.Runtime;

namespace Elarion.Actors;

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

    internal abstract ValueTask RunAsync(TActor actor, CancellationToken stopping);

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
    private readonly TaskCompletionSource<TResult> _completion =
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

    internal override async ValueTask RunAsync(TActor actor, CancellationToken stopping) {
        // Canceled or timed out while queued: skip execution entirely.
        if (_completion.Task.IsCompleted) {
            Cleanup();
            return;
        }

        if (stopping.IsCancellationRequested) {
            _completion.TrySetCanceled(stopping);
            Cleanup();
            return;
        }

        ActorTelemetry.RecordQueueWait(
            _actorName, MethodName, _timeProvider.GetElapsedTime(_enqueuedTimestamp));
        using var activity = ActorTelemetry.StartProcess(_actorName, MethodName, _key, _callerContext);
        var startTimestamp = _timeProvider.GetTimestamp();
        var outcome = "ok";
        try {
            CancellationToken token;
            if (_invocationCts is null) {
                token = stopping;
            }
            else {
                _stoppingToken = stopping;
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
            outcome = "error";
            if (activity is not null) {
                activity.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity.AddException(ex);
            }

            _completion.TrySetException(ex);
        }
        finally {
            Cleanup();
            ActorTelemetry.RecordMessage(
                _actorName, MethodName, outcome, _timeProvider.GetElapsedTime(startTimestamp));
        }
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
    }
}
