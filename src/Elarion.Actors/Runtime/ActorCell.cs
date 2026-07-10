using System.Threading.Channels;
using Elarion.Actors.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Elarion.Actors.Runtime;

/// <summary>
/// One activation: a mailbox (channel), the actor instance, its DI scope, and the processing loop.
/// Non-reentrant cells process one work item start-to-finish; reentrant cells run every message
/// segment on an exclusive scheduler so turns interleave but never run in parallel.
/// </summary>
/// <remarks>
/// Lifecycle: created inert (a losing <c>GetOrAdd</c> racer is discarded without side effects),
/// started once via <see cref="EnsureStarted"/> (activates instance + scope, then drains the
/// mailbox), closed by idle passivation (only when no work is pending), activation failure, or
/// shutdown. A closed cell rejects enqueues with <see langword="false"/> so the host retries
/// against a fresh activation.
/// </remarks>
internal sealed class ActorCell<TActor> where TActor : class {
    private readonly Channel<ActorWorkItem<TActor>> _mailbox;
    private readonly string _actorName;
    private readonly string _keyText;
    private readonly Func<IServiceProvider, TActor> _activator;
    private readonly ActorOptions _options;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    // Deliberately never disposed: it owns no timer, and disposing would race the shutdown
    // registration's Cancel call.
    private readonly CancellationTokenSource _stoppingCts;
    private readonly Action<ActorCell<TActor>> _onClosed;
    // Routes a snapshot-conflicted item back through the host once this cell has closed, so its
    // single transparent retry runs on a fresh activation that loaded the winning snapshot.
    private readonly Func<ActorWorkItem<TActor>, ValueTask> _reEnqueue;
    // Items whose turn conflicted and still owe their retry; drained after _onClosed. Guarded by
    // its own lock only because reentrant completions can race (rare path, no hot-path cost).
    private List<ActorWorkItem<TActor>>? _retryItems;

    // Mailbox coordination packed into one word so enqueue, completion, and idle passivation
    // synchronize with a single interlocked op instead of a shared lock (that lock was the hottest
    // contended frame on the actor call path). Bit 63 = closed; bits 0..62 = the pending count
    // (items reserved but not yet completed). Reserving a pending slot before the mailbox write and
    // re-checking closed under the same word is what stops the idle timer from closing the cell
    // underneath an in-flight enqueue — the invariant the old lock enforced.
    private const long ClosedFlag = long.MinValue;
    private const long PendingMask = long.MaxValue;
    private long _state;
    private int _started;
    // Set when a turn fails with ActorSnapshotConcurrencyException (ADR-0047): the activation's
    // in-memory state and ETag are stale, so it drain-closes once pending work reaches zero — the
    // next activation reloads the latest snapshot (the Orleans deactivate-on-InconsistentState
    // recovery). Int for Interlocked; only the 0 → 1 transition logs.
    private int _snapshotConflicted;
    private Task? _loop;
    // Touched off-lock now: created on the pump thread before the loop, re-armed from
    // OnItemCompleted (a different thread under [Reentrant]), disposed after the loop drains. Timer
    // ops are thread-safe and a post-dispose Change/fire is a benign no-op, so volatile suffices.
    private volatile ITimer? _idleTimer;

    private static bool IsClosed(long state) => (state & ClosedFlag) != 0;
    private static long Pending(long state) => state & PendingMask;

    internal ActorCell(
        string actorName,
        string keyText,
        Func<IServiceProvider, TActor> activator,
        ActorOptions options,
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        ILogger logger,
        CancellationTokenSource stoppingCts,
        Action<ActorCell<TActor>> onClosed,
        Func<ActorWorkItem<TActor>, ValueTask> reEnqueue) {
        _actorName = actorName;
        _keyText = keyText;
        _activator = activator;
        _options = options;
        _scopeFactory = scopeFactory;
        _timeProvider = timeProvider;
        _logger = logger;
        _stoppingCts = stoppingCts;
        _onClosed = onClosed;
        _reEnqueue = reEnqueue;
        _mailbox = options.MailboxCapacity is { } capacity
            ? Channel.CreateBounded<ActorWorkItem<TActor>>(new BoundedChannelOptions(capacity) {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
                FullMode = BoundedChannelFullMode.Wait
            })
            : Channel.CreateUnbounded<ActorWorkItem<TActor>>(new UnboundedChannelOptions {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });
    }

    internal void EnsureStarted() {
        if (Interlocked.Exchange(ref _started, 1) == 0) {
            _loop = Task.Run(RunAsync);
        }
    }

    // Atomically increments the pending count unless the cell is closed. The closed check and the
    // increment happen on one word, so OnIdleTimerFired (which only closes when it observes zero
    // pending on that same word) can never race in between and passivate a cell that is about to
    // receive an item.
    private bool TryReservePending() {
        var state = Volatile.Read(ref _state);
        while (true) {
            if (IsClosed(state)) {
                return false;
            }

            var prev = Interlocked.CompareExchange(ref _state, state + 1, state);
            if (prev == state) {
                return true;
            }

            state = prev;
        }
    }

    private void Close() => Interlocked.Or(ref _state, ClosedFlag);

    /// <summary>
    /// Enqueues a work item. Returns <see langword="false"/> when the cell has closed (the caller
    /// retries against a fresh cell); throws <see cref="ActorMailboxFullException"/> for a full
    /// fail-fast mailbox.
    /// </summary>
    internal async ValueTask<bool> TryEnqueueAsync(ActorWorkItem<TActor> item, CancellationToken cancellationToken) {
        // Reserve a pending slot (blocks passivation) unless the cell has already closed.
        if (!TryReservePending()) {
            return false;
        }

        ActorTelemetry.RecordEnqueued(_actorName);

        if (_mailbox.Writer.TryWrite(item)) {
            return true;
        }

        // TryWrite failed: bounded mailbox full, or the writer completed by shutdown.
        if (_options.MailboxFullMode == ActorMailboxFullMode.Fail) {
            OnItemCompleted();
            if (IsClosed(Volatile.Read(ref _state))) {
                return false;
            }

            throw new ActorMailboxFullException(
                $"Mailbox of actor '{_actorName}' ({_keyText}) is full (capacity {_options.MailboxCapacity}).");
        }

        try {
            await _mailbox.Writer.WriteAsync(item, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (ChannelClosedException) {
            OnItemCompleted();
            return false;
        }
        catch {
            OnItemCompleted();
            throw;
        }
    }

    internal async Task StopAsync(CancellationToken cancellationToken) {
        Close();
        _mailbox.Writer.TryComplete();
        var loop = _loop;
        if (loop is null) {
            // Never started: nothing was ever enqueued and no scope exists.
            return;
        }

        // Graceful drain: queued items still execute. When the host's shutdown token fires, the
        // stopping token cancels in-flight methods and remaining queued items complete as canceled.
        await using var registration = cancellationToken.UnsafeRegister(
            static state => ((ActorCell<TActor>)state!)._stoppingCts.Cancel(), this);
        await loop.ConfigureAwait(false);
    }

    private async Task RunAsync() {
        var scope = _scopeFactory.CreateAsyncScope();
        TActor instance;
        try {
            instance = _activator(scope.ServiceProvider);
            // Load persisted snapshots (ADR-0047) before OnActivateAsync so the hook and the first
            // message both observe durable state; a load failure fails the activation like any
            // other activation error.
            var states = scope.ServiceProvider.GetService<ActorStateTracker>();
            if (states is { HasStates: true }) {
                await states.LoadAllAsync(_stoppingCts.Token).ConfigureAwait(false);
            }

            if (instance is IActorLifecycle activating) {
                await activating.OnActivateAsync(_stoppingCts.Token).ConfigureAwait(false);
            }

            ActorTelemetry.RecordActivation(_actorName);
            _logger.LogDebug("Activated actor {Actor} ({ActorKey}).", _actorName, _keyText);
        }
        catch (Exception ex) {
            _logger.LogError(
                ex, "Activation of actor {Actor} ({ActorKey}) failed; failing queued calls.", _actorName, _keyText);
            ActorTelemetry.RecordActivationFailure(_actorName);
            await FailAllAsync(ex, scope).ConfigureAwait(false);
            return;
        }

        StartIdleTimer();
        try {
            if (_options.Reentrant) {
                await RunReentrantAsync(instance).ConfigureAwait(false);
            }
            else {
                await RunSequentialAsync(instance).ConfigureAwait(false);
            }
        }
        finally {
            // The pending count is drained (writer completed, mailbox empty) and every
            // OnItemCompleted has run (sequential: same thread; reentrant: awaited via WhenAll), so
            // no concurrent Change can race this dispose.
            var timer = _idleTimer;
            _idleTimer = null;
            timer?.Dispose();

            try {
                if (instance is IActorLifecycle deactivating) {
                    await deactivating.OnDeactivateAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception ex) {
                _logger.LogError(
                    ex, "OnDeactivateAsync of actor {Actor} ({ActorKey}) failed.", _actorName, _keyText);
            }

            await scope.DisposeAsync().ConfigureAwait(false);
            ActorTelemetry.RecordDeactivation(_actorName);
            _logger.LogDebug("Deactivated actor {Actor} ({ActorKey}).", _actorName, _keyText);
            _onClosed(this);

            // After the host forgot this cell: conflicted turns re-enter the mailbox path and land
            // on a fresh activation that loads the winning snapshot (their single retry).
            await ReEnqueueConflictedItemsAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask ReEnqueueConflictedItemsAsync() {
        List<ActorWorkItem<TActor>>? items;
        lock (_mailbox) {
            items = _retryItems;
            _retryItems = null;
        }

        if (items is null) {
            return;
        }

        foreach (var item in items) {
            try {
                await _reEnqueue(item).ConfigureAwait(false);
            }
            catch (Exception ex) {
                // System stopping or a fail-fast full mailbox: the caller gets the enqueue failure.
                item.TryFail(ex);
            }
        }
    }

    private async Task RunSequentialAsync(TActor instance) {
        await foreach (var item in _mailbox.Reader.ReadAllAsync().ConfigureAwait(false)) {
            OnTurnFinished(item, await item.RunAsync(instance, _stoppingCts.Token).ConfigureAwait(false));
            OnItemCompleted();
        }
    }

    private async Task RunReentrantAsync(TActor instance) {
        // Orleans-style turns: the initial segment of every message and each of its await
        // continuations run on the exclusive scheduler, so turns from different messages interleave
        // at await points but never execute in parallel.
        var schedulerPair = new ConcurrentExclusiveSchedulerPair();
        var inFlight = new List<Task>();
        await foreach (var item in _mailbox.Reader.ReadAllAsync().ConfigureAwait(false)) {
            var run = Task.Factory.StartNew(
                () => item.RunAsync(instance, _stoppingCts.Token).AsTask(),
                CancellationToken.None,
                TaskCreationOptions.DenyChildAttach,
                schedulerPair.ExclusiveScheduler).Unwrap();
            var currentItem = item;
            var tracked = run.ContinueWith(
                (task, state) => {
                    var cell = (ActorCell<TActor>)state!;
                    if (task.Status == TaskStatus.RanToCompletion) {
                        cell.OnTurnFinished(currentItem, task.Result);
                    }

                    cell.OnItemCompleted();
                },
                this,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            inFlight.Add(tracked);
            if (inFlight.Count >= 64) {
                inFlight.RemoveAll(static task => task.IsCompleted);
            }
        }

        await Task.WhenAll(inFlight).ConfigureAwait(false);
        schedulerPair.Complete();
    }

    private async Task FailAllAsync(Exception exception, AsyncServiceScope scope) {
        Close();
        _mailbox.Writer.TryComplete();
        while (_mailbox.Reader.TryRead(out var item)) {
            item.TryFail(exception);
            ActorTelemetry.RecordDequeued(_actorName);
        }

        await scope.DisposeAsync().ConfigureAwait(false);
        _onClosed(this);
    }

    private void StartIdleTimer() {
        if (_options.IdleTimeout is not { } idle) {
            return;
        }

        // Runs once on the pump thread before the loop reads, so this store precedes both the
        // re-arm reads in OnItemCompleted and the dispose in the loop's finally. A cell that closed
        // between the check and the create still gets its timer disposed by that finally.
        if (IsClosed(Volatile.Read(ref _state))) {
            return;
        }

        _idleTimer = _timeProvider.CreateTimer(
            static state => ((ActorCell<TActor>)state!).OnIdleTimerFired(),
            this,
            idle,
            Timeout.InfiniteTimeSpan);
    }

    private void OnIdleTimerFired() => TryCloseWhenIdle();

    // Closes the cell iff nothing is pending — the shared invariant of idle passivation and
    // snapshot-conflict passivation: closing only at zero pending means the mailbox is empty and no
    // enqueue is in flight, so the replacement activation the host creates can never process a
    // message concurrently with this one.
    private void TryCloseWhenIdle() {
        var state = Volatile.Read(ref _state);
        while (true) {
            if (IsClosed(state) || Pending(state) != 0) {
                // Still busy or already closing: re-attempted when pending drops back to zero.
                return;
            }

            var prev = Interlocked.CompareExchange(ref _state, state | ClosedFlag, state);
            if (prev == state) {
                break;
            }

            state = prev;
        }

        // The loop drains (the mailbox is empty), deactivates, and removes the cell from the host.
        _mailbox.Writer.TryComplete();
    }

    private void OnTurnFinished(ActorWorkItem<TActor> item, ActorTurnOutcome outcome) {
        if (outcome == ActorTurnOutcome.Completed) {
            return;
        }

        if (outcome == ActorTurnOutcome.SnapshotConflictRetry) {
            lock (_mailbox) {
                (_retryItems ??= []).Add(item);
            }
        }

        if (Interlocked.Exchange(ref _snapshotConflicted, 1) == 0) {
            _logger.LogWarning(
                "Actor {Actor} ({ActorKey}) hit a snapshot concurrency conflict; passivating this "
                + "activation and re-running the conflicted turn on a fresh one that reloads the "
                + "latest snapshot.",
                _actorName, _keyText);
        }
    }

    private void OnItemCompleted() {
        // Pending is >= 1 here (an item was reserved), so decrementing the word never borrows into
        // the closed bit. On the transition back to zero pending: a snapshot-conflicted activation
        // passivates immediately (its state/ETag are stale — ADR-0047), otherwise the idle timer
        // re-arms.
        var state = Interlocked.Decrement(ref _state);
        if (Pending(state) == 0 && !IsClosed(state)) {
            if (Volatile.Read(ref _snapshotConflicted) != 0) {
                TryCloseWhenIdle();
            }
            else if (_options.IdleTimeout is { } idle) {
                _idleTimer?.Change(idle, Timeout.InfiniteTimeSpan);
            }
        }

        ActorTelemetry.RecordDequeued(_actorName);
    }
}
