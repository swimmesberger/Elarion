using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Elarion.Abstractions.Resilience;
using Elarion.Abstractions.Scheduling;
using Elarion.Abstractions.Substitution;

namespace Elarion.Scheduling;

/// <summary>
/// Runs source-generated scheduled job descriptors in memory.
/// </summary>
/// <remarks>
/// The dispatch loop sleeps until the earliest due time and is woken early by runtime
/// scheduling or cancellation. All waits go through <see cref="TimeProvider"/>, so the
/// scheduler can be driven deterministically in tests and supports millisecond-level
/// intervals (subject to OS timer resolution, typically 1-15 ms of jitter).
/// </remarks>
public sealed class InMemoryScheduler(
    IEnumerable<ScheduledJobDescriptor> descriptors,
    IServiceScopeFactory scopeFactory,
    IVariableSource variableSource,
    TimeProvider timeProvider,
    SchedulerOptions options,
    IResiliencePolicyCatalog resiliencePolicies,
    ILogger<InMemoryScheduler> logger
) : BackgroundService, IJobScheduler, IJobSchedulerInspector {
    /// <summary>Upper bound for a single wait so very long schedules stay within timer limits.</summary>
    private static readonly TimeSpan MaxWaitSlice = TimeSpan.FromDays(1);

    // Schedules and the per-occurrence enabled check resolve their ${...} variables through the general
    // substitution seam, so the (default config-backed) source makes them runtime-changeable.
    private readonly IVariableSource _variableSource = variableSource;

    // Current queued recurring occurrence per job name, so a live variable change can supersede it.
    private readonly Dictionary<string, Guid> _recurringRunIds = new(StringComparer.Ordinal);

    // Queued occurrences replaced by a live reschedule; dropped silently (no outcome) at dequeue.
    private readonly HashSet<Guid> _supersededRuns = [];

    // Last resolved variable signature per recurring job, to detect which jobs a change actually affects.
    private readonly Dictionary<string, string> _recurringSignatures = new(StringComparer.Ordinal);

    private IDisposable? _variableSubscription;

    // ReSharper disable once PossibleMultipleEnumeration
    private readonly IReadOnlyList<ScheduledJobDescriptor> _descriptors = descriptors.ToArray();
    private readonly PriorityQueue<ScheduledJobWorkItem, DateTimeOffset> _queue = new();
    private readonly Dictionary<Guid, ScheduledJobWorkItem> _queuedRuns = new();
    private readonly Dictionary<Guid, ScheduledJobWorkItem> _dispatchingRuns = new();
    private readonly HashSet<Guid> _cancelledRuns = [];
    private readonly Dictionary<string, int> _activeRunsByJob = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, ActiveScheduledJobRun> _activeRuns = new();
    private readonly Dictionary<Guid, ScheduledJobState> _jobStates = new();
    private readonly Queue<Guid> _completedJobStateOrder = new();
    private readonly HashSet<Guid> _completedJobStates = [];
    private readonly Dictionary<string, ScheduledJobOutcomeInfo> _lastOutcomesByJob = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SemaphoreSlim> _serializationSemaphores = new(StringComparer.Ordinal);
    private readonly List<Task> _inFlightRuns = [];
    private readonly Lock _stateLock = new();
    private readonly SemaphoreSlim _concurrencyLimiter = new(Math.Max(1, options.MaxConcurrentExecutions));
    private readonly Dictionary<(Type JobType, Type PayloadType), ScheduledJobDescriptor> _runtimeDescriptors =
        // ReSharper disable once PossibleMultipleEnumeration
        descriptors
            .Where(descriptor => descriptor.SupportsRuntimeScheduling)
            .ToDictionary(descriptor => (descriptor.JobType!, descriptor.PayloadType!));

    /// <summary>Last successfully resolved schedule per job, used when a config reload turns invalid.</summary>
    private readonly Dictionary<string, ResolvedSchedule> _resolvedSchedules = new(StringComparer.Ordinal);

    private TaskCompletionSource _wakeSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <inheritdoc />
    public ValueTask<ScheduledJobRunHandle> EnqueueAsync<TJob, TPayload>(
        TPayload payload,
        CancellationToken ct = default)
        where TJob : IScheduledJob<TPayload> =>
        EnqueueAsync<TJob, TPayload>(payload, jobOptions: null, ct);

    /// <inheritdoc />
    public ValueTask<ScheduledJobRunHandle> EnqueueAsync<TJob, TPayload>(
        TPayload payload,
        ScheduledJobOptions? jobOptions,
        CancellationToken ct = default)
        where TJob : IScheduledJob<TPayload> =>
        ScheduleAsync<TJob, TPayload>(payload, timeProvider.GetUtcNow(), jobOptions, ct);

    /// <inheritdoc />
    public ValueTask<ScheduledJobRunHandle> ScheduleAsync<TJob, TPayload>(
        TPayload payload,
        DateTimeOffset dueTimeUtc,
        CancellationToken ct = default)
        where TJob : IScheduledJob<TPayload> =>
        ScheduleAsync<TJob, TPayload>(payload, dueTimeUtc, jobOptions: null, ct);

    /// <inheritdoc />
    public ValueTask<ScheduledJobRunHandle> ScheduleAsync<TJob, TPayload>(
        TPayload payload,
        DateTimeOffset dueTimeUtc,
        ScheduledJobOptions? jobOptions,
        CancellationToken ct = default)
        where TJob : IScheduledJob<TPayload> {
        ct.ThrowIfCancellationRequested();
        jobOptions ??= new ScheduledJobOptions();

        var key = (typeof(TJob), typeof(TPayload));
        if (!_runtimeDescriptors.TryGetValue(key, out var descriptor)) {
            throw new InvalidOperationException(
                $"Runtime job '{typeof(TJob).FullName}' with payload '{typeof(TPayload).FullName}' is not registered with the generated scheduler registry.");
        }

        if (!options.Enabled) {
            logger.LogWarning(
                "Job {JobName} was scheduled while the in-memory scheduler is disabled; it will not run.",
                descriptor.Name);
        }

        var normalizedDueTimeUtc = dueTimeUtc.ToUniversalTime();
        var jobId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var maxAttempts = GetMaxAttempts(jobOptions);
        var item = new ScheduledJobWorkItem(
            runId,
            jobId,
            descriptor,
            normalizedDueTimeUtc,
            payload,
            true,
            1,
            maxAttempts,
            jobOptions.ResiliencePolicy,
            jobOptions.ResilienceMode,
            jobOptions.CorrelationId,
            0,
            null);

        using var activity = SchedulerTelemetry.Source.StartActivity(
            $"scheduler schedule {descriptor.Name}",
            ActivityKind.Internal);
        var started = Stopwatch.GetTimestamp();
        var outcome = "success";
        if (activity?.IsAllDataRequested == true) {
            activity.SetTag("scheduler.operation", "schedule");
            SetJobTags(activity, item);
        }

        try {
            item = item with {
                TraceParent = activity?.Context ?? Activity.Current?.Context
            };
            Enqueue(item);
        } catch (Exception ex) {
            outcome = "error";
            RecordException(activity, ex);
            throw;
        } finally {
            RecordOperation(activity, "schedule", outcome, started);
        }

        return ValueTask.FromResult(new ScheduledJobRunHandle {
            JobId = jobId,
            RunId = runId,
            JobName = descriptor.Name,
            DueTimeUtc = normalizedDueTimeUtc,
            Attempt = 1,
            MaxAttempts = maxAttempts
        });
    }

    /// <inheritdoc />
    public ValueTask<bool> CancelRunAsync(Guid runId, CancellationToken ct = default) {
        ct.ThrowIfCancellationRequested();

        using var activity = SchedulerTelemetry.Source.StartActivity("scheduler cancel run", ActivityKind.Internal);
        var started = Stopwatch.GetTimestamp();
        var outcome = "not-found";
        if (activity?.IsAllDataRequested == true) {
            activity.SetTag("scheduler.operation", "cancel-run");
            activity.SetTag("scheduler.job.run_id", runId);
        }

        CancellationTokenSource? activeCancellation = null;
        lock (_stateLock) {
            if (_queuedRuns.TryGetValue(runId, out var queued)) {
                _cancelledRuns.Add(runId);
                outcome = "queued";
                if (activity?.IsAllDataRequested == true) {
                    SetJobTags(activity, queued);
                }

                if (queued.IsRuntimeScheduled) {
                    RecordJobStateLocked(queued, ScheduledJobLifecycleStatus.Cancelled, null, "cancelled", timeProvider.GetUtcNow());
                }
            } else if (_dispatchingRuns.TryGetValue(runId, out var dispatching)) {
                _cancelledRuns.Add(runId);
                outcome = "dispatching";
                if (activity?.IsAllDataRequested == true) {
                    SetJobTags(activity, dispatching);
                }

                if (dispatching.IsRuntimeScheduled) {
                    RecordJobStateLocked(dispatching, ScheduledJobLifecycleStatus.Cancelled, null, "cancelled", timeProvider.GetUtcNow());
                }
            } else if (_activeRuns.TryGetValue(runId, out var activeRun)) {
                activeCancellation = activeRun.Cancellation;
                outcome = "active";
                if (activity?.IsAllDataRequested == true) {
                    SetJobTags(activity, activeRun.Item);
                }
            } else {
                RecordOperation(activity, "cancel-run", outcome, started);
                return ValueTask.FromResult(false);
            }
        }

        try {
            activeCancellation?.Cancel();
        } catch (ObjectDisposedException) {
            RecordOperation(activity, "cancel-run", "disposed", started);
            return ValueTask.FromResult(false);
        }

        Wake();
        RecordOperation(activity, "cancel-run", outcome, started);
        return ValueTask.FromResult(true);
    }

    /// <inheritdoc />
    public ValueTask<bool> CancelJobAsync(Guid jobId, CancellationToken ct = default) {
        ct.ThrowIfCancellationRequested();

        using var activity = SchedulerTelemetry.Source.StartActivity("scheduler cancel job", ActivityKind.Internal);
        var started = Stopwatch.GetTimestamp();
        var outcome = "not-found";
        if (activity?.IsAllDataRequested == true) {
            activity.SetTag("scheduler.operation", "cancel-job");
            activity.SetTag("scheduler.job.id", jobId);
        }

        CancellationTokenSource? activeCancellation = null;
        lock (_stateLock) {
            if (!_jobStates.ContainsKey(jobId)) {
                RecordOperation(activity, "cancel-job", outcome, started);
                return ValueTask.FromResult(false);
            }

            var pending = _queuedRuns.Values
                .Concat(_dispatchingRuns.Values)
                .FirstOrDefault(item => item.JobId == jobId);
            if (pending is not null) {
                _cancelledRuns.Add(pending.RunId);
                outcome = "pending";
                if (activity?.IsAllDataRequested == true) {
                    SetJobTags(activity, pending);
                }

                RecordJobStateLocked(pending, ScheduledJobLifecycleStatus.Cancelled, null, "cancelled", timeProvider.GetUtcNow());
            } else {
                var active = _activeRuns.Values.FirstOrDefault(run => run.Item.JobId == jobId);
                if (active is null) {
                    RecordOperation(activity, "cancel-job", "not-active", started);
                    return ValueTask.FromResult(false);
                }

                activeCancellation = active.Cancellation;
                outcome = "active";
                if (activity?.IsAllDataRequested == true) {
                    SetJobTags(activity, active.Item);
                }
            }
        }

        try {
            activeCancellation?.Cancel();
        } catch (ObjectDisposedException) {
            RecordOperation(activity, "cancel-job", "disposed", started);
            return ValueTask.FromResult(false);
        }

        Wake();
        RecordOperation(activity, "cancel-job", outcome, started);
        return ValueTask.FromResult(true);
    }

    /// <inheritdoc />
    public SchedulerSnapshot GetSnapshot() {
        lock (_stateLock) {
            var queuedRuns = _queuedRuns.Values
                .Where(item => !_cancelledRuns.Contains(item.RunId))
                .OrderBy(item => item.DueTimeUtc)
                .Select(static item => CreateRunInfo(item, null, ScheduledJobRunStatus.Queued))
                .ToArray();
            var activeRuns = _activeRuns.Values
                .OrderBy(run => run.StartedAtUtc)
                .Select(static run => CreateRunInfo(run.Item, run.StartedAtUtc, ScheduledJobRunStatus.Running))
                .ToArray();
            var nextDueByJob = _queuedRuns.Values
                .Where(item => !_cancelledRuns.Contains(item.RunId))
                .GroupBy(item => item.Descriptor.Name, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.Min(item => item.DueTimeUtc),
                    StringComparer.Ordinal);
            var jobs = _descriptors
                .OrderBy(descriptor => descriptor.Name, StringComparer.Ordinal)
                .Select(descriptor => new ScheduledJobDescriptorInfo {
                    Name = descriptor.Name,
                    ScheduleKind = descriptor.Schedule?.Kind,
                    SupportsRuntimeScheduling = descriptor.SupportsRuntimeScheduling,
                    Group = descriptor.Group,
                    Overlap = descriptor.Overlap,
                    MisfirePolicy = descriptor.MisfirePolicy,
                    MaxConcurrentRuns = descriptor.MaxConcurrentRuns,
                    NextDueTimeUtc = nextDueByJob.TryGetValue(descriptor.Name, out var nextDueTimeUtc)
                        ? nextDueTimeUtc
                        : null
                })
                .ToArray();

            return new SchedulerSnapshot {
                CapturedAtUtc = timeProvider.GetUtcNow(),
                Jobs = jobs,
                QueuedRuns = queuedRuns,
                ActiveRuns = activeRuns,
                RecentOutcomes = _lastOutcomesByJob.Values
                    .OrderByDescending(outcome => outcome.CompletedAtUtc)
                    .ToArray()
            };
        }
    }

    /// <inheritdoc />
    public ScheduledJobState? GetJob(Guid jobId) {
        lock (_stateLock) {
            return _jobStates.TryGetValue(jobId, out var state) ? state : null;
        }
    }

    /// <summary>
    /// Enqueues each recurring job's first occurrence and subscribes to variable changes <b>synchronously</b>, so the
    /// scheduler is fully initialized — jobs queued and the live-reschedule subscription active — the moment
    /// <see cref="StartAsync"/> returns, before the dispatch loop spawns. Invalid startup configuration therefore
    /// fails host startup deterministically rather than faulting the background loop.
    /// </summary>
    public override Task StartAsync(CancellationToken cancellationToken) {
        if (options.Enabled) {
            // A misconfigured resilience runner is a startup error, not a per-run failure loop: validate before
            // any occurrence is enqueued so the host fails fast with a message naming the affected job(s).
            ValidateResilienceRunnerRegistration();
            // Note 32: Startup enqueues only the next occurrence for each recurring job; future occurrences are chained as runs dispatch or complete.
            EnqueueRecurringJobs();
            SubscribeToVariableChanges();
        }

        return base.StartAsync(cancellationToken);
    }

    /// <summary>
    /// Fails fast when a registered job runs its resilience policy inline but the opt-in
    /// <see cref="IResiliencePipelineRunner"/> (from <c>Elarion.Resilience</c>) is not registered. Without this a
    /// <c>[Resilient]</c>-decorated inline job would throw on <em>every</em> run with no startup signal — a silent
    /// per-run failure loop rather than an actionable configuration error.
    /// </summary>
    private void ValidateResilienceRunnerRegistration() {
        // Inline resilience is driven by a descriptor-level policy (deferred retry takes a separate path that
        // never resolves the runner). Runtime one-offs may also carry an inline policy, but those descriptors are
        // exactly the ones that expose a descriptor policy or support runtime scheduling with resilience overrides;
        // the descriptor-level policy is the compile-time-known signal we can validate at startup.
        var jobsNeedingRunner = _descriptors
            .Where(descriptor => descriptor.ResiliencePolicy is not null)
            .Select(descriptor => descriptor.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        if (jobsNeedingRunner.Length == 0) {
            return;
        }

        using var scope = scopeFactory.CreateScope();
        if (scope.ServiceProvider.GetService<IResiliencePipelineRunner>() is not null) {
            return;
        }

        throw new InvalidOperationException(
            $"Scheduled job(s) [{string.Join(", ", jobsNeedingRunner)}] run their resilience policy inline, but no " +
            $"{nameof(IResiliencePipelineRunner)} is registered. Reference the Elarion.Resilience package and call " +
            "AddElarionResilience() during service registration, or remove the resilience policy from these jobs.");
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        if (!options.Enabled) {
            logger.LogInformation("In-memory scheduler is disabled.");
            return;
        }

        // Recurring jobs and the variable-change subscription were set up synchronously in StartAsync.
        logger.LogInformation("In-memory scheduler started with {JobCount} descriptor(s).", _descriptors.Count);

        try {
            while (!stoppingToken.IsCancellationRequested) {
                // Note 33: Completed tasks are observed on the scheduler loop so exceptions are logged and no task is left unobserved.
                ObserveCompletedRuns();

                // Reset the wake signal before inspecting the queue so an enqueue that
                // races with the inspection completes the signal we are about to await.
                var wake = ResetWakeSignal();
                var item = TryDequeueDueItem(out var delay);
                if (item is null) {
                    await WaitForWakeSignalAsync(wake, delay, stoppingToken);
                    continue;
                }

                StartRun(item, stoppingToken);
            }
        } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
            logger.LogInformation("In-memory scheduler is shutting down.");
        } finally {
            _variableSubscription?.Dispose();
            await AwaitInFlightRunsAsync();
            logger.LogInformation("In-memory scheduler stopped.");
        }
    }

    private void EnqueueRecurringJobs() {
        // Disabled descriptors are enqueued too: the enabled flag is evaluated per
        // occurrence so jobs can be turned on and off through configuration reloads
        // without restarting the host. Invalid startup configuration fails fast here.
        var now = timeProvider.GetUtcNow();
        foreach (var descriptor in _descriptors) {
            if (descriptor.Schedule is null) {
                continue;
            }

            var resolved = descriptor.Schedule.Resolve(_variableSource);
            var signature = ComputeSignature(descriptor);
            lock (_stateLock) {
                _resolvedSchedules[descriptor.Name] = resolved;
                _recurringSignatures[descriptor.Name] = signature;
            }

            if (resolved.IsDisabled) {
                continue;
            }

            Enqueue(BuildRecurringOccurrence(descriptor, resolved.GetFirstDueTime(now)));
        }
    }

    private static ScheduledJobWorkItem BuildRecurringOccurrence(ScheduledJobDescriptor descriptor, DateTimeOffset dueTimeUtc) =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            descriptor,
            dueTimeUtc,
            null,
            false,
            1,
            1,
            null,
            ScheduledJobResilienceMode.Inline,
            null,
            0,
            null);

    // Resolves the schedule's variable-bearing inputs to a signature, so a live change can be matched to the
    // jobs it actually affects (a job whose schedule is all literals never reschedules on an unrelated change).
    private string ComputeSignature(ScheduledJobDescriptor descriptor) {
        var schedule = descriptor.Schedule!;
        return string.Join(
            "\u001f",
            VariableSubstitution.Resolve(schedule.Value, _variableSource),
            schedule.InitialDelay is null ? null : VariableSubstitution.Resolve(schedule.InitialDelay, _variableSource),
            schedule.TimeZone is null ? null : VariableSubstitution.Resolve(schedule.TimeZone, _variableSource),
            descriptor.Enabled is null ? null : VariableSubstitution.Resolve(descriptor.Enabled, _variableSource));
    }

    private void SubscribeToVariableChanges() {
        // Live reschedule: when a watched variable changes, re-resolve affected recurring jobs immediately
        // instead of waiting for each one's next natural occurrence. Sources that cannot observe change skip this.
        if (_variableSource is IObservableVariableSource observable) {
            _variableSubscription = ChangeToken.OnChange(observable.Watch, ResyncRecurringSchedules);
        }
    }

    private void ResyncRecurringSchedules() {
        try {
            var now = timeProvider.GetUtcNow();
            foreach (var descriptor in _descriptors) {
                if (descriptor.Schedule is not null) {
                    ResyncDescriptor(descriptor, now);
                }
            }
        } catch (Exception ex) {
            logger.LogError(ex, "Failed to resync scheduled jobs after a variable change.");
        }
    }

    private void ResyncDescriptor(ScheduledJobDescriptor descriptor, DateTimeOffset now) {
        string signature;
        try {
            signature = ComputeSignature(descriptor);
        } catch (FormatException) {
            return; // malformed placeholder; leave the existing chain untouched
        }

        bool supersededQueued;
        bool jobActive;
        lock (_stateLock) {
            if (string.Equals(_recurringSignatures.GetValueOrDefault(descriptor.Name), signature, StringComparison.Ordinal)) {
                return; // variables affecting this job did not change
            }

            _recurringSignatures[descriptor.Name] = signature;

            supersededQueued = false;
            if (_recurringRunIds.TryGetValue(descriptor.Name, out var runId) && _queuedRuns.Remove(runId)) {
                // Tombstone the queued occurrence; its physical queue entry is dropped lazily at dequeue.
                _supersededRuns.Add(runId);
                _recurringRunIds.Remove(descriptor.Name);
                supersededQueued = true;
            }

            jobActive = _activeRunsByJob.GetValueOrDefault(descriptor.Name) > 0
                || DispatchingContainsLocked(descriptor.Name);
        }

        ResolvedSchedule resolved;
        try {
            resolved = ResolveScheduleSafely(descriptor);
        } catch (Exception ex) {
            logger.LogError(ex, "Failed to resolve the schedule for {JobName} during live reschedule.", descriptor.Name);
            return;
        }

        if (resolved.IsDisabled) {
            return; // now disabled: superseding the queued occurrence stopped the chain
        }

        // Skip re-enqueuing only when the job is mid-run with no queued occurrence: a fixed-delay chain
        // reschedules itself on completion and will pick up the new variables there.
        if (!supersededQueued && jobActive) {
            return;
        }

        // For fixed-rate/cron grid schedules the chain advances at dispatch, so a run in flight always has a
        // queued successor. Re-resolving that successor to GetFirstDueTime(now) would enqueue an *immediate*
        // extra occurrence that races the active run (concurrently under AllowConcurrent, or an unexpected
        // immediate execution otherwise). Instead recompute the successor on the new grid strictly after now
        // (anchored at now) — preserving the "do not trigger a run while one is active" intent while still
        // picking up the new schedule.
        if (jobActive && IsRecurringGridSchedule(descriptor.Schedule)) {
            Enqueue(BuildRecurringOccurrence(descriptor, resolved.GetNextDueTime(now, now)));
            return;
        }

        Enqueue(BuildRecurringOccurrence(descriptor, resolved.GetFirstDueTime(now)));
    }

    private bool DispatchingContainsLocked(string jobName) {
        foreach (var dispatching in _dispatchingRuns.Values) {
            if (string.Equals(dispatching.Descriptor.Name, jobName, StringComparison.Ordinal)) {
                return true;
            }
        }

        return false;
    }

    private ResolvedSchedule ResolveScheduleSafely(ScheduledJobDescriptor descriptor) {
        try {
            var resolved = descriptor.Schedule!.Resolve(_variableSource);
            lock (_stateLock) {
                _resolvedSchedules[descriptor.Name] = resolved;
            }

            return resolved;
        } catch (Exception ex) {
            // A bad configured value must not kill the recurring chain; reuse the last
            // schedule that resolved successfully until the configuration is fixed.
            ResolvedSchedule fallback;
            lock (_stateLock) {
                fallback = _resolvedSchedules[descriptor.Name];
            }

            logger.LogError(
                ex,
                "Failed to resolve the schedule for job {JobName}; reusing the last valid schedule.",
                descriptor.Name);
            return fallback;
        }
    }

    private bool IsDescriptorEnabled(ScheduledJobDescriptor descriptor) {
        if (descriptor.Enabled is null) {
            return true;
        }

        string? resolved;
        try {
            resolved = VariableSubstitution.Resolve(descriptor.Enabled, _variableSource);
        } catch (FormatException ex) {
            logger.LogError(ex, "Scheduled job {JobName} has a malformed Enabled placeholder; treating it as disabled.", descriptor.Name);
            return false;
        }

        // An unconfigured placeholder without an inline default means enabled.
        if (string.IsNullOrWhiteSpace(resolved)) {
            return true;
        }

        if (bool.TryParse(resolved, out var enabled)) {
            return enabled;
        }

        logger.LogError(
            "Enabled value '{Value}' for scheduled job {JobName} must be a boolean; treating it as disabled.",
            resolved,
            descriptor.Name);
        return false;
    }

    private int GetMaxAttempts(ScheduledJobOptions options) {
        if (options.ResilienceMode != ScheduledJobResilienceMode.DeferredRetry ||
            options.ResiliencePolicy is not { } policy) {
            return 1;
        }

        var metadata = resiliencePolicies.GetPolicy(policy);
        if (metadata is null) {
            throw new InvalidOperationException(
                $"Deferred scheduler retry requires generated resilience metadata for policy '{policy.Name}'. Register the generated resilience policy before scheduling jobs with deferred retry.");
        }

        return metadata.Retry is { } retry
            ? Math.Max(1, retry.MaxRetryAttempts + 1)
            : 1;
    }

    private void Enqueue(ScheduledJobWorkItem item) {
        lock (_stateLock) {
            // Note 34: The priority queue gives the scheduler an efficient "next due run" without a fixed polling interval.
            _queue.Enqueue(item, item.DueTimeUtc);
            _queuedRuns[item.RunId] = item;
            if (!item.IsRuntimeScheduled) {
                // Track the current queued recurring occurrence so a live variable change can supersede it.
                _recurringRunIds[item.Descriptor.Name] = item.RunId;
            }
            if (item.IsRuntimeScheduled && !_jobStates.ContainsKey(item.JobId)) {
                RecordJobStateLocked(item, ScheduledJobLifecycleStatus.Queued, item.DueTimeUtc, null, null);
            }
        }

        RecordEnqueue(item);
        Wake();
    }

    private ScheduledJobWorkItem? TryDequeueDueItem(out TimeSpan delay) {
        lock (_stateLock) {
            while (_queue.Count > 0) {
                var next = _queue.Peek();
                if (_supersededRuns.Remove(next.RunId)) {
                    // A live reschedule replaced this occurrence; drop it silently (no recorded outcome).
                    _queue.Dequeue();
                    _queuedRuns.Remove(next.RunId);
                    continue;
                }

                if (_cancelledRuns.Remove(next.RunId)) {
                    _queuedRuns.Remove(next.RunId);
                    _queue.Dequeue();
                    RecordOutcomeLocked(
                        next,
                        null,
                        timeProvider.GetUtcNow(),
                        ScheduledJobRunStatus.Cancelled,
                        "cancelled before execution");
                    RecordPreExecutionOutcome(next, "cancelled", "cancelled before execution");
                    RecordTerminalStatus(next.Descriptor, "cancelled", TimeSpan.Zero);
                    continue;
                }

                var now = timeProvider.GetUtcNow();
                if (next.DueTimeUtc > now) {
                    delay = next.DueTimeUtc - now;
                    return null;
                }

                _queue.Dequeue();
                _queuedRuns.Remove(next.RunId);
                // Note 35: Dispatching is tracked separately to close the race between leaving the queue and becoming active.
                _dispatchingRuns[next.RunId] = next;
                delay = TimeSpan.Zero;
                return next;
            }

            delay = Timeout.InfiniteTimeSpan;
            return null;
        }
    }

    private void StartRun(ScheduledJobWorkItem item, CancellationToken stoppingToken) {
        // Fixed-rate and cron chains advance at dispatch; fixed-delay chains advance when
        // the run completes (see RunItemAsync). Runtime-scheduled one-offs never advance a
        // chain, even when their job type also has a recurring schedule.
        if (!item.IsRuntimeScheduled &&
            item.Descriptor.Schedule is { } schedule &&
            schedule.Kind != ScheduledJobScheduleKind.FixedDelay) {
            if (TrySkipMisfiredOccurrence(item, stoppingToken)) {
                return;
            }

            RescheduleRecurring(item);
        }

        var task = RunItemAsync(item, stoppingToken);
        if (task.IsCompleted) {
            // Skipped or synchronously completed occurrences never enter the in-flight list.
            if (task.Exception is not null) {
                logger.LogError(task.Exception, "Scheduled job runner observed an unhandled execution task exception.");
            }

            return;
        }

        lock (_stateLock) {
            _inFlightRuns.Add(task);
        }
    }

    private async Task RunItemAsync(ScheduledJobWorkItem item, CancellationToken stoppingToken) {
        var descriptor = item.Descriptor;
        var beganRun = false;
        // Note 36: Linking the host shutdown token with per-run cancellation gives both global and user-triggered cancellation paths.
        using var runCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        if (TryConsumeCancelledDispatch(item)) {
            RecordOutcome(
                item,
                null,
                timeProvider.GetUtcNow(),
                ScheduledJobRunStatus.Cancelled,
                "cancelled before execution");
            return;
        }

        if (!IsDescriptorEnabled(descriptor)) {
            if (item.IsRuntimeScheduled) {
                RecordSkipped(item, "disabled");
                RecordOutcome(
                    item,
                    null,
                    timeProvider.GetUtcNow(),
                    ScheduledJobRunStatus.Skipped,
                    "disabled");
            }

            RemoveDispatching(item.RunId);
            RescheduleFixedDelayOccurrence(item, stoppingToken);
            return;
        }

        var executionStarted = false;
        try {
            if (IsScheduleDisabled(descriptor)) {
                RecordSkipped(item, "schedule-disabled");
                RecordOutcome(
                    item,
                    null,
                    timeProvider.GetUtcNow(),
                    ScheduledJobRunStatus.Skipped,
                    "schedule disabled");
                return;
            }

            if (TryConsumeCancelledDispatch(item)) {
                RecordOutcome(
                    item,
                    null,
                    timeProvider.GetUtcNow(),
                    ScheduledJobRunStatus.Cancelled,
                    "cancelled before execution");
                return;
            }

            if (!TryBeginRun(item, timeProvider.GetUtcNow(), runCancellation, out var skipReason)) {
                // Note 37: Overlap and job-local concurrency decisions are recorded as skipped outcomes, not silent drops.
                RecordSkipped(item, skipReason);
                RecordOutcome(
                    item,
                    null,
                    timeProvider.GetUtcNow(),
                    ScheduledJobRunStatus.Skipped,
                    skipReason);
                return;
            }

            beganRun = true;
            var serializationSemaphore = GetSerializationSemaphore(descriptor);
            if (serializationSemaphore is null) {
                executionStarted = true;
                await ExecuteWithConcurrencyLimitAsync(item, runCancellation, stoppingToken);
                return;
            }

            // The serialization semaphore is taken before a global concurrency slot so a
            // queued occurrence waiting for its predecessor cannot starve unrelated jobs.
            await serializationSemaphore.WaitAsync(runCancellation.Token);
            try {
                executionStarted = true;
                await ExecuteWithConcurrencyLimitAsync(item, runCancellation, stoppingToken);
            } finally {
                serializationSemaphore.Release();
            }
        } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
            logger.LogWarning("Scheduled job {JobName} was cancelled during scheduler shutdown.", descriptor.Name);
            if (!executionStarted) {
                if (item.IsRuntimeScheduled) {
                    RecordOutcome(
                        item,
                        null,
                        timeProvider.GetUtcNow(),
                        ScheduledJobRunStatus.Cancelled,
                        "scheduler shutdown");
                }

                RecordTerminalStatus(descriptor, "cancelled", TimeSpan.Zero);
            }
        } catch (OperationCanceledException) when (runCancellation.IsCancellationRequested) {
            logger.LogWarning("Scheduled job {JobName} ({RunId}) was cancelled before execution.", descriptor.Name, item.RunId);
            RecordOutcome(
                item,
                null,
                timeProvider.GetUtcNow(),
                ScheduledJobRunStatus.Cancelled,
                "cancelled");
            RecordTerminalStatus(descriptor, "cancelled", TimeSpan.Zero);
        } finally {
            if (beganRun) {
                EndRun(descriptor.Name, item.RunId);
            } else {
                RemoveDispatching(item.RunId);
            }

            // Fixed-delay chains advance only after the run finishes (success, failure,
            // disabled, or skipped alike) so the delay is measured from completion.
            RescheduleFixedDelayOccurrence(item, stoppingToken);
        }
    }

    private void RescheduleFixedDelayOccurrence(ScheduledJobWorkItem item, CancellationToken stoppingToken) {
        if (!item.IsRuntimeScheduled &&
            item.Descriptor.Schedule is { Kind: ScheduledJobScheduleKind.FixedDelay } &&
            !stoppingToken.IsCancellationRequested) {
            RescheduleRecurring(item);
        }
    }

    private async Task ExecuteWithConcurrencyLimitAsync(
        ScheduledJobWorkItem item,
        CancellationTokenSource runCancellation,
        CancellationToken stoppingToken) {
        await _concurrencyLimiter.WaitAsync(runCancellation.Token);
        try {
            await ExecuteDescriptorAsync(item, runCancellation, stoppingToken);
        } finally {
            _concurrencyLimiter.Release();
        }
    }

    private bool TryBeginRun(
        ScheduledJobWorkItem item,
        DateTimeOffset startedAtUtc,
        CancellationTokenSource runCancellation,
        out string skipReason) {
        var descriptor = item.Descriptor;
        lock (_stateLock) {
            var active = _activeRunsByJob.GetValueOrDefault(descriptor.Name);
            switch (descriptor.Overlap) {
                case ScheduledJobOverlap.Skip when active >= 1:
                    _dispatchingRuns.Remove(item.RunId);
                    skipReason = "overlap";
                    return false;
                // Recurring queued occurrences coalesce into at most one pending run so a
                // job that is consistently slower than its interval cannot pile up waiters.
                case ScheduledJobOverlap.Queue when !item.IsRuntimeScheduled && active >= 2:
                    _dispatchingRuns.Remove(item.RunId);
                    skipReason = "coalesced";
                    return false;
                case ScheduledJobOverlap.AllowConcurrent when descriptor.MaxConcurrentRuns > 0 && active >= descriptor.MaxConcurrentRuns:
                    _dispatchingRuns.Remove(item.RunId);
                    skipReason = "max-concurrency";
                    return false;
            }

            _dispatchingRuns.Remove(item.RunId);
            _activeRunsByJob[descriptor.Name] = active + 1;
            _activeRuns[item.RunId] = new ActiveScheduledJobRun(item, startedAtUtc, runCancellation);
            if (item.IsRuntimeScheduled) {
                RecordJobStateLocked(item, ScheduledJobLifecycleStatus.Running, null, null, null);
            }

            skipReason = string.Empty;
            return true;
        }
    }

    private void EndRun(string jobName, Guid runId) {
        lock (_stateLock) {
            _activeRuns.Remove(runId);
            var active = _activeRunsByJob.GetValueOrDefault(jobName);
            if (active <= 1) {
                _activeRunsByJob.Remove(jobName);
            } else {
                _activeRunsByJob[jobName] = active - 1;
            }
        }
    }

    private bool TryConsumeCancelledDispatch(ScheduledJobWorkItem item) {
        lock (_stateLock) {
            if (!_cancelledRuns.Remove(item.RunId)) {
                return false;
            }

            _dispatchingRuns.Remove(item.RunId);
            return true;
        }
    }

    private void RemoveDispatching(Guid runId) {
        lock (_stateLock) {
            _dispatchingRuns.Remove(runId);
        }
    }

    private void RecordJobStateLocked(
        ScheduledJobWorkItem item,
        ScheduledJobLifecycleStatus status,
        DateTimeOffset? nextAttemptDueTimeUtc,
        string? lastError,
        DateTimeOffset? completedAtUtc) {
        var existing = _jobStates.TryGetValue(item.JobId, out var state) ? state : null;
        _jobStates[item.JobId] = new ScheduledJobState {
            JobId = item.JobId,
            CurrentRunId = item.RunId,
            JobName = item.Descriptor.Name,
            Status = status,
            Attempt = item.Attempt,
            MaxAttempts = item.MaxAttempts,
            NextAttemptDueTimeUtc = nextAttemptDueTimeUtc,
            CorrelationId = item.CorrelationId,
            LastError = lastError,
            CreatedAtUtc = existing?.CreatedAtUtc ?? timeProvider.GetUtcNow(),
            CompletedAtUtc = completedAtUtc
        };

        if (IsTerminal(status) && _completedJobStates.Add(item.JobId)) {
            _completedJobStateOrder.Enqueue(item.JobId);
            PruneCompletedJobStatesLocked();
        }
    }

    private void PruneCompletedJobStatesLocked() {
        var maxRetained = Math.Max(0, options.MaxRetainedCompletedJobs);
        while (_completedJobStateOrder.Count > maxRetained) {
            var jobId = _completedJobStateOrder.Dequeue();
            _completedJobStates.Remove(jobId);
            if (_jobStates.TryGetValue(jobId, out var state) && IsTerminal(state.Status)) {
                _jobStates.Remove(jobId);
            }
        }
    }

    private static bool IsTerminal(ScheduledJobLifecycleStatus status) =>
        status is ScheduledJobLifecycleStatus.Succeeded
            or ScheduledJobLifecycleStatus.Failed
            or ScheduledJobLifecycleStatus.Cancelled
            or ScheduledJobLifecycleStatus.Skipped;

    private static ScheduledJobLifecycleStatus ToLifecycleStatus(ScheduledJobRunStatus status) =>
        status switch {
            ScheduledJobRunStatus.Succeeded => ScheduledJobLifecycleStatus.Succeeded,
            ScheduledJobRunStatus.Cancelled => ScheduledJobLifecycleStatus.Cancelled,
            ScheduledJobRunStatus.Skipped => ScheduledJobLifecycleStatus.Skipped,
            ScheduledJobRunStatus.Failed => ScheduledJobLifecycleStatus.Failed,
            ScheduledJobRunStatus.Running => ScheduledJobLifecycleStatus.Running,
            _ => ScheduledJobLifecycleStatus.Queued
        };

    private SemaphoreSlim? GetSerializationSemaphore(ScheduledJobDescriptor descriptor) {
        var key = !string.IsNullOrWhiteSpace(descriptor.Group)
            ? descriptor.Group
            : descriptor.Overlap == ScheduledJobOverlap.Queue
                ? descriptor.Name
                : null;
        if (string.IsNullOrWhiteSpace(key)) {
            return null;
        }

        lock (_stateLock) {
            if (!_serializationSemaphores.TryGetValue(key, out var semaphore)) {
                semaphore = new SemaphoreSlim(1, 1);
                _serializationSemaphores[key] = semaphore;
            }

            return semaphore;
        }
    }

    private async Task ExecuteDescriptorAsync(
        ScheduledJobWorkItem item,
        CancellationTokenSource runCancellation,
        CancellationToken stoppingToken) {
        var descriptor = item.Descriptor;
        var startedAt = timeProvider.GetUtcNow();
        var lag = startedAt - item.DueTimeUtc;
        var startedTimestamp = timeProvider.GetTimestamp();
        var status = "success";
        var runStatus = ScheduledJobRunStatus.Succeeded;
        string? outcomeMessage = null;
        var retryScheduled = false;

        using var activity = item.TraceParent is { } traceParent
            ? SchedulerTelemetry.Source.StartActivity($"scheduled {descriptor.Name}", ActivityKind.Internal, traceParent)
            : SchedulerTelemetry.Source.StartActivity($"scheduled {descriptor.Name}", ActivityKind.Internal);
        if (activity?.IsAllDataRequested == true) {
            SetJobTags(activity, item);
            activity.SetTag("scheduler.job.scheduling_lag_ms", Math.Max(0, lag.TotalMilliseconds));
        }

        SchedulerTelemetry.ActiveJobRuns.Add(1, CreateTags(descriptor, "active"));
        try {
            await using var scope = scopeFactory.CreateAsyncScope();
            var context = new InMemoryScheduledJobContext(
                item.RunId,
                descriptor.Name,
                item.DueTimeUtc,
                startedAt,
                item.IsRuntimeScheduled,
                runCancellation);

            logger.LogInformation("Starting scheduled job {JobName} ({RunId}).", descriptor.Name, item.RunId);
            var invocationPolicy = item.ResilienceMode == ScheduledJobResilienceMode.DeferredRetry
                ? null
                : item.ResiliencePolicy ?? descriptor.ResiliencePolicy;
            if (item.ResilienceMode == ScheduledJobResilienceMode.DeferredRetry &&
                item.ResiliencePolicy is { } deferredPolicy) {
                // Note 38: Deferred retry cannot use a sleeping pipeline because scheduler capacity should be released between attempts.
                var metadata = resiliencePolicies.GetPolicy(deferredPolicy);
                if (metadata?.Timeout is { } timeout) {
                    await InvokeDescriptorWithTimeoutAsync(
                        descriptor,
                        scope.ServiceProvider,
                        item.Payload,
                        context,
                        timeout,
                        runCancellation.Token);
                } else {
                    await descriptor.InvokeAsync(scope.ServiceProvider, item.Payload, context, runCancellation.Token);
                }
            } else if (invocationPolicy is { } policy) {
                // Note 39: Inline resilience keeps all attempts inside the current RunId, similar to retrying a normal method call.
                var runner = scope.ServiceProvider.GetRequiredService<IResiliencePipelineRunner>();
                await runner.ExecuteAsync(
                    policy,
                    token => descriptor.InvokeAsync(scope.ServiceProvider, item.Payload, context, token),
                    runCancellation.Token);
            } else {
                await descriptor.InvokeAsync(scope.ServiceProvider, item.Payload, context, runCancellation.Token);
            }

            if (runCancellation.IsCancellationRequested && !stoppingToken.IsCancellationRequested) {
                status = "cancelled";
                runStatus = ScheduledJobRunStatus.Cancelled;
                outcomeMessage = "cancelled";
                logger.LogWarning("Scheduled job {JobName} ({RunId}) acknowledged cancellation.", descriptor.Name, item.RunId);
            } else {
                logger.LogInformation("Scheduled job {JobName} ({RunId}) completed.", descriptor.Name, item.RunId);
            }
        } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
            status = "cancelled";
            runStatus = ScheduledJobRunStatus.Cancelled;
            outcomeMessage = "scheduler shutdown";
            activity?.SetStatus(ActivityStatusCode.Error, "cancelled");
            throw;
        } catch (OperationCanceledException) when (runCancellation.IsCancellationRequested) {
            status = "cancelled";
            runStatus = ScheduledJobRunStatus.Cancelled;
            outcomeMessage = "cancelled";
            logger.LogWarning("Scheduled job {JobName} ({RunId}) was cancelled.", descriptor.Name, item.RunId);
            activity?.SetStatus(ActivityStatusCode.Error, "cancelled");
        } catch (Exception ex) {
            status = "failed";
            runStatus = ScheduledJobRunStatus.Failed;
            outcomeMessage = ex.Message;
            if (TryScheduleDeferredRetry(item, ex, activity)) {
                retryScheduled = true;
                status = "waiting-retry";
                logger.LogWarning(
                    ex,
                    "Scheduled job {JobName} ({RunId}) failed on attempt {Attempt}/{MaxAttempts}; retry was scheduled.",
                    descriptor.Name,
                    item.RunId,
                    item.Attempt,
                    item.MaxAttempts);
            } else {
                logger.LogError(ex, "Scheduled job {JobName} ({RunId}) failed.", descriptor.Name, item.RunId);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            }

            activity?.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection {
                { "exception.type", ex.GetType().FullName },
                { "exception.message", ex.Message }
            }));
        } finally {
            var elapsed = timeProvider.GetElapsedTime(startedTimestamp);
            var completedAt = timeProvider.GetUtcNow();
            lock (_stateLock) {
                RecordOutcomeLocked(item, startedAt, completedAt, runStatus, outcomeMessage, recordJobState: !retryScheduled);
            }

            SchedulerTelemetry.ActiveJobRuns.Add(-1, CreateTags(descriptor, "active"));
            SchedulerTelemetry.JobRunLag.Record(Math.Max(0, lag.TotalMilliseconds), CreateTags(descriptor, status));
            if (activity?.IsAllDataRequested == true) {
                activity.SetTag("scheduler.job.status", status);
                activity.SetTag("scheduler.job.duration_ms", elapsed.TotalMilliseconds);
            }

            RecordTerminalStatus(descriptor, status, elapsed);
        }
    }

    private async ValueTask InvokeDescriptorWithTimeoutAsync(
        ScheduledJobDescriptor descriptor,
        IServiceProvider serviceProvider,
        object? payload,
        IScheduledJobContext context,
        TimeSpan timeout,
        CancellationToken runToken) {
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(runToken);
        try {
            await descriptor
                .InvokeAsync(serviceProvider, payload, context, timeoutCancellation.Token)
                .AsTask()
                .WaitAsync(timeout, timeProvider, runToken);
        } catch (TimeoutException ex) {
            timeoutCancellation.Cancel();
            throw new TimeoutException(
                $"Scheduled job {descriptor.Name} ({context.RunId}) timed out after {timeout}.",
                ex);
        }
    }

    private bool TryScheduleDeferredRetry(
        ScheduledJobWorkItem item,
        Exception exception,
        Activity? activity) {
        if (!item.IsRuntimeScheduled ||
            item.ResilienceMode != ScheduledJobResilienceMode.DeferredRetry ||
            item.ResiliencePolicy is not { } policy ||
            exception is NonRetryableException ||
            item.Attempt >= item.MaxAttempts) {
            return false;
        }

        var metadata = resiliencePolicies.GetPolicy(policy);
        if (metadata?.Retry is not { } retry) {
            return false;
        }

        var now = timeProvider.GetUtcNow();
        var delay = CalculateRetryDelay(retry, item.Attempt);
        var nextDue = now + delay;
        var retryItem = item with {
            RunId = Guid.NewGuid(),
            DueTimeUtc = nextDue,
            Attempt = item.Attempt + 1
        };

        lock (_stateLock) {
            // Note 40: The logical JobId stays the same, but each retry attempt gets a new RunId for telemetry and cancellation.
            _queue.Enqueue(retryItem, retryItem.DueTimeUtc);
            _queuedRuns[retryItem.RunId] = retryItem;
            RecordJobStateLocked(retryItem, ScheduledJobLifecycleStatus.WaitingRetry, nextDue, exception.Message, null);
        }

        activity?.AddEvent(new ActivityEvent("retry scheduled", tags: new ActivityTagsCollection {
            { "scheduler.job.next_attempt", retryItem.Attempt },
            { "scheduler.job.next_due_time", nextDue },
            { "scheduler.job.retry_delay_ms", delay.TotalMilliseconds }
        }));
        RecordEnqueue(retryItem);
        Wake();
        return true;
    }

    private static TimeSpan CalculateRetryDelay(ResilienceRetryOptions retry, int completedAttempt) {
        var multiplier = retry.Backoff switch {
            ResilienceBackoffType.Constant => 1d,
            ResilienceBackoffType.Linear => completedAttempt,
            ResilienceBackoffType.Exponential => Math.Pow(2d, Math.Max(0, completedAttempt - 1)),
            _ => 1d
        };
        var delay = TimeSpan.FromMilliseconds(retry.Delay.TotalMilliseconds * multiplier);
        if (retry.MaxDelay is { } maxDelay && delay > maxDelay) {
            delay = maxDelay;
        }

        if (retry.UseJitter && delay > TimeSpan.Zero) {
            delay += TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * delay.TotalMilliseconds);
        }

        return delay;
    }

    private bool TrySkipMisfiredOccurrence(ScheduledJobWorkItem item, CancellationToken stoppingToken) {
        if (stoppingToken.IsCancellationRequested ||
            item.Descriptor.MisfirePolicy != ScheduledJobMisfirePolicy.Skip ||
            !IsRecurringGridSchedule(item.Descriptor.Schedule)) {
            return false;
        }

        // Note 41: Skip misfires are evaluated before execution so a stale run does not consume a concurrency slot.
        var resolved = ResolveScheduleSafely(item.Descriptor);
        if (resolved.IsDisabled || !IsDescriptorEnabled(item.Descriptor)) {
            return false;
        }

        bool hasLaterOccurrenceDue;
        try {
            hasLaterOccurrenceDue = HasLaterOccurrenceDue(resolved, item.DueTimeUtc, timeProvider.GetUtcNow());
        } catch (Exception ex) {
            logger.LogError(
                ex,
                "Failed to evaluate misfire policy for scheduled job {JobName}; continuing with normal scheduling.",
                item.Descriptor.Name);
            return false;
        }

        if (!hasLaterOccurrenceDue) {
            return false;
        }

        if (TryConsumeCancelledDispatch(item)) {
            RecordOutcome(
                item,
                null,
                timeProvider.GetUtcNow(),
                ScheduledJobRunStatus.Cancelled,
                "cancelled before execution");
            RecordPreExecutionOutcome(item, "cancelled", "cancelled before execution");
            RecordTerminalStatus(item.Descriptor, "cancelled", TimeSpan.Zero);
            return true;
        }

        RecordSkipped(item, "misfire");
        RecordOutcome(
            item,
            null,
            timeProvider.GetUtcNow(),
            ScheduledJobRunStatus.Skipped,
            "misfire");
        RemoveDispatching(item.RunId);
        RescheduleRecurring(item, resolved);
        return true;
    }

    private void RescheduleRecurring(ScheduledJobWorkItem item, ResolvedSchedule? resolvedSchedule = null) {
        var now = timeProvider.GetUtcNow();
        DateTimeOffset nextDue;
        var nextCatchUpRuns = 0;
        try {
            var resolved = resolvedSchedule ?? ResolveScheduleSafely(item.Descriptor);
            if (resolved.IsDisabled) {
                return;
            }

            nextDue = GetNextDueTimeForMisfirePolicy(item, resolved, now, out nextCatchUpRuns);
        } catch (Exception ex) {
            // Even a schedule that resolves but cannot produce a due time (e.g. a cron
            // expression that never matches) must not end the chain; retry in an hour.
            logger.LogError(
                ex,
                "Failed to compute the next due time for scheduled job {JobName}; retrying in one hour.",
                item.Descriptor.Name);
            nextDue = now + TimeSpan.FromHours(1);
        }

        Enqueue(item with {
            RunId = Guid.NewGuid(),
            JobId = Guid.NewGuid(),
            DueTimeUtc = nextDue,
            Payload = null,
            IsRuntimeScheduled = false,
            Attempt = 1,
            MaxAttempts = 1,
            ResiliencePolicy = null,
            ResilienceMode = ScheduledJobResilienceMode.Inline,
            CorrelationId = null,
            MisfireCatchUpRuns = nextCatchUpRuns,
            TraceParent = null
        });
    }

    private DateTimeOffset GetNextDueTimeForMisfirePolicy(
        ScheduledJobWorkItem item,
        ResolvedSchedule resolved,
        DateTimeOffset now,
        out int nextCatchUpRuns) {
        nextCatchUpRuns = 0;
        if (item.Descriptor.MisfirePolicy == ScheduledJobMisfirePolicy.CatchUp &&
            IsRecurringGridSchedule(item.Descriptor.Schedule)) {
            var nextAfterPrevious = GetNextOccurrenceAfterPrevious(resolved, item.DueTimeUtc);
            if (nextAfterPrevious > now) {
                return resolved.GetNextDueTime(item.DueTimeUtc, now);
            }

            // Note 42: Catch-up is bounded because in-memory schedulers must never create unbounded bursts after long pauses.
            var maxCatchUpRuns = Math.Max(0, options.MaxMisfireCatchUpRuns);
            if (item.MisfireCatchUpRuns < maxCatchUpRuns) {
                nextCatchUpRuns = item.MisfireCatchUpRuns + 1;
                return nextAfterPrevious;
            }

            RecordSkipped(item, "misfire-coalesced");
        }

        return resolved.GetNextDueTime(item.DueTimeUtc, now);
    }

    private static bool HasLaterOccurrenceDue(
        ResolvedSchedule resolved,
        DateTimeOffset previousDueTimeUtc,
        DateTimeOffset nowUtc) {
        var nextDueTimeUtc = GetNextOccurrenceAfterPrevious(resolved, previousDueTimeUtc);
        return nextDueTimeUtc <= nowUtc;
    }

    private static DateTimeOffset GetNextOccurrenceAfterPrevious(
        ResolvedSchedule resolved,
        DateTimeOffset previousDueTimeUtc) =>
        resolved.Kind switch {
            ScheduledJobScheduleKind.FixedRate => previousDueTimeUtc + resolved.Interval!.Value,
            ScheduledJobScheduleKind.Cron => resolved.Cron!.GetNextOccurrence(previousDueTimeUtc, resolved.TimeZone),
            _ => throw new InvalidOperationException("Only fixed-rate and cron schedules have grid occurrences.")
        };

    private static bool IsRecurringGridSchedule(ScheduledJobSchedule? schedule) =>
        schedule?.Kind is ScheduledJobScheduleKind.FixedRate or ScheduledJobScheduleKind.Cron;

    private bool IsScheduleDisabled(ScheduledJobDescriptor descriptor) =>
        descriptor.Schedule is { Kind: ScheduledJobScheduleKind.Cron } &&
        ResolveScheduleSafely(descriptor).IsDisabled;

    private void ObserveCompletedRuns() {
        List<Task>? completed = null;
        lock (_stateLock) {
            for (var i = _inFlightRuns.Count - 1; i >= 0; i--) {
                var task = _inFlightRuns[i];
                if (!task.IsCompleted) {
                    continue;
                }

                (completed ??= []).Add(task);
                _inFlightRuns.RemoveAt(i);
            }
        }

        if (completed is null) {
            return;
        }

        foreach (var task in completed) {
            if (task.Exception is not null) {
                logger.LogError(task.Exception, "Scheduled job runner observed an unhandled execution task exception.");
            }
        }
    }

    private async Task AwaitInFlightRunsAsync() {
        Task[] remaining;
        lock (_stateLock) {
            remaining = _inFlightRuns.ToArray();
            _inFlightRuns.Clear();
        }

        if (remaining.Length == 0) {
            return;
        }

        try {
            await Task.WhenAll(remaining);
        } catch (Exception ex) {
            logger.LogError(ex, "One or more scheduled jobs failed during scheduler shutdown.");
        }
    }

    private Task ResetWakeSignal() {
        lock (_stateLock) {
            if (_wakeSignal.Task.IsCompleted) {
                _wakeSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            return _wakeSignal.Task;
        }
    }

    private void Wake() {
        lock (_stateLock) {
            _wakeSignal.TrySetResult();
        }
    }

    private async Task WaitForWakeSignalAsync(Task wake, TimeSpan delay, CancellationToken stoppingToken) {
        if (delay != Timeout.InfiniteTimeSpan) {
            if (delay <= TimeSpan.Zero) {
                return;
            }

            if (delay > MaxWaitSlice) {
                delay = MaxWaitSlice;
            }
        }

        try {
            await wake.WaitAsync(delay, timeProvider, stoppingToken);
        } catch (TimeoutException) {
            // The next queued item is due.
        }
    }

    private static void RecordSkipped(ScheduledJobWorkItem item, string reason) {
        using var activity = item.TraceParent is { } traceParent
            ? SchedulerTelemetry.Source.StartActivity($"scheduled {item.Descriptor.Name} skipped", ActivityKind.Internal, traceParent)
            : SchedulerTelemetry.Source.StartActivity($"scheduled {item.Descriptor.Name} skipped", ActivityKind.Internal);
        if (activity?.IsAllDataRequested == true) {
            SetJobTags(activity, item);
            activity.SetTag("scheduler.job.status", "skipped");
            activity.SetTag("scheduler.skip.reason", reason);
        }

        var tags = CreateTags(item.Descriptor, "skipped");
        tags.Add("scheduler.skip.reason", reason);
        SchedulerTelemetry.JobRunCount.Add(1, tags);
    }

    private static void RecordTerminalStatus(
        ScheduledJobDescriptor descriptor,
        string status,
        TimeSpan elapsed) {
        var tags = CreateTags(descriptor, status);
        SchedulerTelemetry.JobRunCount.Add(1, tags);
        SchedulerTelemetry.JobRunDuration.Record(elapsed.TotalMilliseconds, tags);
    }

    private void RecordOutcome(
        ScheduledJobWorkItem item,
        DateTimeOffset? startedAtUtc,
        DateTimeOffset completedAtUtc,
        ScheduledJobRunStatus status,
        string? message,
        bool recordJobState = true) {
        lock (_stateLock) {
            RecordOutcomeLocked(item, startedAtUtc, completedAtUtc, status, message, recordJobState);
        }
    }

    private void RecordOutcomeLocked(
        ScheduledJobWorkItem item,
        DateTimeOffset? startedAtUtc,
        DateTimeOffset completedAtUtc,
        ScheduledJobRunStatus status,
        string? message,
        bool recordJobState = true) {
        _lastOutcomesByJob[item.Descriptor.Name] = new ScheduledJobOutcomeInfo {
            RunId = item.RunId,
            JobName = item.Descriptor.Name,
            DueTimeUtc = item.DueTimeUtc,
            StartedAtUtc = startedAtUtc,
            CompletedAtUtc = completedAtUtc,
            IsRuntimeScheduled = item.IsRuntimeScheduled,
            Status = status,
            Message = message
        };

        if (item.IsRuntimeScheduled && recordJobState) {
            RecordJobStateLocked(item, ToLifecycleStatus(status), null, message, completedAtUtc);
        }
    }

    private static ScheduledJobRunInfo CreateRunInfo(
        ScheduledJobWorkItem item,
        DateTimeOffset? startedAtUtc,
        ScheduledJobRunStatus status) =>
        new() {
            RunId = item.RunId,
            JobName = item.Descriptor.Name,
            DueTimeUtc = item.DueTimeUtc,
            StartedAtUtc = startedAtUtc,
            IsRuntimeScheduled = item.IsRuntimeScheduled,
            Status = status
        };

    private static TagList CreateTags(ScheduledJobDescriptor descriptor, string status) =>
        new() {
            { "scheduler.job.name", descriptor.Name },
            { "scheduler.job.status", status }
        };

    private static void RecordEnqueue(ScheduledJobWorkItem item) {
        using var activity = SchedulerTelemetry.Source.StartActivity(
            $"scheduler enqueue {item.Descriptor.Name}",
            ActivityKind.Internal);
        if (activity?.IsAllDataRequested == true) {
            activity.SetTag("scheduler.operation", "enqueue");
            SetJobTags(activity, item);
            activity.SetTag("scheduler.operation.outcome", "queued");
        }

        SchedulerTelemetry.RecordOperation("enqueue", "queued", 0);
    }

    private static void RecordPreExecutionOutcome(ScheduledJobWorkItem item, string status, string reason) {
        using var activity = item.TraceParent is { } traceParent
            ? SchedulerTelemetry.Source.StartActivity($"scheduled {item.Descriptor.Name} {status}", ActivityKind.Internal, traceParent)
            : SchedulerTelemetry.Source.StartActivity($"scheduled {item.Descriptor.Name} {status}", ActivityKind.Internal);
        if (activity?.IsAllDataRequested == true) {
            SetJobTags(activity, item);
            activity.SetTag("scheduler.job.status", status);
            activity.SetTag("scheduler.job.outcome.reason", reason);
        }
    }

    private static void SetJobTags(Activity activity, ScheduledJobWorkItem item) {
        activity.SetTag("scheduler.job.name", item.Descriptor.Name);
        activity.SetTag("scheduler.job.id", item.JobId);
        activity.SetTag("scheduler.job.run_id", item.RunId);
        activity.SetTag("scheduler.job.attempt", item.Attempt);
        activity.SetTag("scheduler.job.max_attempts", item.MaxAttempts);
        activity.SetTag("scheduler.job.runtime_scheduled", item.IsRuntimeScheduled);
        activity.SetTag("scheduler.job.due_time", item.DueTimeUtc);
        activity.SetTag("scheduler.job.schedule_kind", item.Descriptor.Schedule?.Kind.ToString() ?? "runtime");
        if (!string.IsNullOrWhiteSpace(item.CorrelationId)) {
            activity.SetTag("scheduler.job.correlation_id", item.CorrelationId);
        }
    }

    private static void RecordOperation(Activity? activity, string operation, string outcome, long started) {
        if (activity?.IsAllDataRequested == true) {
            activity.SetTag("scheduler.operation.outcome", outcome);
        }

        SchedulerTelemetry.RecordOperation(
            operation,
            outcome,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    private static void RecordException(Activity? activity, Exception exception) {
        activity?.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection {
            { "exception.type", exception.GetType().FullName },
            { "exception.message", exception.Message }
        }));
        activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
    }

    private sealed record ActiveScheduledJobRun(
        ScheduledJobWorkItem Item,
        DateTimeOffset StartedAtUtc,
        CancellationTokenSource Cancellation);

    private sealed class InMemoryScheduledJobContext(
        Guid runId,
        string jobName,
        DateTimeOffset dueTimeUtc,
        DateTimeOffset startedAtUtc,
        bool isRuntimeScheduled,
        CancellationTokenSource cancellation
    ) : IScheduledJobContext {
        public Guid RunId { get; } = runId;

        public string JobName { get; } = jobName;

        public DateTimeOffset DueTimeUtc { get; } = dueTimeUtc;

        public DateTimeOffset StartedAtUtc { get; } = startedAtUtc;

        public TimeSpan SchedulingLag => StartedAtUtc - DueTimeUtc;

        public bool IsRuntimeScheduled { get; } = isRuntimeScheduled;

        public void RequestCancellation() => cancellation.Cancel();
    }

    private sealed record ScheduledJobWorkItem(
        Guid RunId,
        Guid JobId,
        ScheduledJobDescriptor Descriptor,
        DateTimeOffset DueTimeUtc,
        object? Payload,
        bool IsRuntimeScheduled,
        int Attempt,
        int MaxAttempts,
        ResiliencePolicyReference? ResiliencePolicy,
        ScheduledJobResilienceMode ResilienceMode,
        string? CorrelationId,
        int MisfireCatchUpRuns,
        ActivityContext? TraceParent);
}
