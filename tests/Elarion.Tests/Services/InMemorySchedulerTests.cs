using System.Diagnostics;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Elarion.Abstractions.Resilience;
using Elarion.Abstractions.Scheduling;
using Elarion.Resilience;
using Elarion.Scheduling;
using Xunit;

namespace Elarion.Tests.Services;

public sealed class InMemorySchedulerTests
{
    // These tests drive real background job execution and poll for the outcome on the wall clock (WaitUntilAsync /
    // AdvanceUntilAsync). The scheduler advances via FakeTimeProvider, but the loop's continuations run on the
    // thread pool, so a heavily loaded CI runner (e.g. many Testcontainers Postgres instances in parallel) can
    // starve them past a tight budget. Keep the ceiling generous — a healthy run still finishes in milliseconds;
    // only a genuinely stuck scheduler waits this long before failing.
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(50);
    private const string RetryPolicyName = "test-deferred-retry";
    private const string InlineRetryPolicyName = "test-inline-retry";

    [Fact]
    public async Task EnqueueAsync_RuntimeJob_ExecutesTypedPayload()
    {
        using var cts = new CancellationTokenSource(WaitTimeout);
        var time = new FakeTimeProvider();
        await using var provider = CreateProvider(time);
        var hostedService = provider.GetRequiredService<IHostedService>();
        await hostedService.StartAsync(cts.Token);

        var scheduler = provider.GetRequiredService<IJobScheduler>();
        var recorder = provider.GetRequiredService<SchedulerRecorder>();

        await scheduler.EnqueueAsync<TestRuntimeJob, TestPayload>(
            new TestPayload { Value = "queued" },
            cts.Token);

        var observed = await recorder.WaitAsync(cts.Token);
        await hostedService.StopAsync(cts.Token);

        observed.Should().Be("queued");
    }

    [Fact]
    public async Task EnqueueAsync_RuntimeJob_EmitsScheduleEnqueueAndExecutionTrace() {
        using var activities = new ActivityCollector(SchedulerTelemetry.ActivitySourceName, "Elarion.Tests.Parent");
        using var meters = new MeterCollector(SchedulerTelemetry.MeterName);
        using var parentSource = new ActivitySource("Elarion.Tests.Parent");
        using var parent = parentSource.StartActivity("rpc parent");
        using var cts = new CancellationTokenSource(WaitTimeout);
        var time = new FakeTimeProvider();
        await using var provider = CreateProvider(time);
        var hostedService = provider.GetRequiredService<IHostedService>();
        await hostedService.StartAsync(cts.Token);

        var scheduler = provider.GetRequiredService<IJobScheduler>();
        var recorder = provider.GetRequiredService<SchedulerRecorder>();

        await scheduler.EnqueueAsync<TestRuntimeJob, TestPayload>(
            new TestPayload { Value = "traced" },
            cts.Token);

        var observed = await recorder.WaitAsync(cts.Token);
        await hostedService.StopAsync(cts.Token);

        observed.Should().Be("traced");
        var schedule = activities.Activities.Single(activity => activity.DisplayName == "scheduler schedule test.runtime");
        var execution = activities.Activities.Single(activity => activity.DisplayName == "scheduled test.runtime");
        activities.Activities.Should().Contain(activity =>
            activity.DisplayName == "scheduler enqueue test.runtime" &&
            Equals(activity.GetTag("scheduler.operation.outcome"), "queued"));
        execution.TraceId.Should().Be(schedule.TraceId);
        execution.ParentSpanId.Should().Be(schedule.SpanId);
        execution.GetTag("scheduler.job.status").Should().Be("success");
        meters.Measurements.Should().Contain(measurement =>
            measurement.InstrumentName == "scheduler.operation.count" &&
            measurement.HasTag("scheduler.operation", "schedule") &&
            measurement.HasTag("scheduler.operation.outcome", "success"));
    }

    [Fact]
    public async Task ScheduleAsync_FutureRun_ExecutesOnceDueTimeIsReached()
    {
        using var cts = new CancellationTokenSource(WaitTimeout);
        var time = new FakeTimeProvider();
        await using var provider = CreateProvider(time);
        var hostedService = provider.GetRequiredService<IHostedService>();
        await hostedService.StartAsync(cts.Token);

        var scheduler = provider.GetRequiredService<IJobScheduler>();
        var recorder = provider.GetRequiredService<SchedulerRecorder>();

        await scheduler.ScheduleAsync<TestRuntimeJob, TestPayload>(
            new TestPayload { Value = "future" },
            time.GetUtcNow().AddSeconds(5),
            cts.Token);

        await Task.Delay(100, cts.Token);
        recorder.HasObserved.Should().BeFalse();

        time.Advance(TimeSpan.FromSeconds(5));
        var observed = await recorder.WaitAsync(cts.Token);
        await hostedService.StopAsync(cts.Token);

        observed.Should().Be("future");
    }

    [Fact]
    public async Task ScheduleAsync_PastRun_ExecutesWithoutRecurringMisfireHandling()
    {
        using var cts = new CancellationTokenSource(WaitTimeout);
        var time = new FakeTimeProvider();
        await using var provider = CreateProvider(time);
        var hostedService = provider.GetRequiredService<IHostedService>();
        await hostedService.StartAsync(cts.Token);

        var scheduler = provider.GetRequiredService<IJobScheduler>();
        var recorder = provider.GetRequiredService<SchedulerRecorder>();

        await scheduler.ScheduleAsync<TestRuntimeJob, TestPayload>(
            new TestPayload { Value = "past" },
            time.GetUtcNow().AddSeconds(-30),
            cts.Token);

        var observed = await recorder.WaitAsync(cts.Token);
        await hostedService.StopAsync(cts.Token);

        observed.Should().Be("past");
    }

    [Fact]
    public async Task CancelRunAsync_QueuedFutureRun_PreventsExecution()
    {
        using var cts = new CancellationTokenSource(WaitTimeout);
        var time = new FakeTimeProvider();
        await using var provider = CreateProvider(time);
        var hostedService = provider.GetRequiredService<IHostedService>();
        await hostedService.StartAsync(cts.Token);

        var scheduler = provider.GetRequiredService<IJobScheduler>();
        var recorder = provider.GetRequiredService<SchedulerRecorder>();
        var handle = await scheduler.ScheduleAsync<TestRuntimeJob, TestPayload>(
            new TestPayload { Value = "cancelled" },
            time.GetUtcNow().AddSeconds(5),
            cts.Token);

        var cancelled = await scheduler.CancelRunAsync(handle.RunId, cts.Token);
        time.Advance(TimeSpan.FromSeconds(6));
        await Task.Delay(100, cts.Token);
        await hostedService.StopAsync(cts.Token);

        cancelled.Should().BeTrue();
        recorder.HasObserved.Should().BeFalse();
    }

    [Fact]
    public async Task EnqueueAsync_DisabledRuntimeJob_ParentsSkippedTraceToScheduleTrace() {
        using var activities = new ActivityCollector(SchedulerTelemetry.ActivitySourceName);
        using var cts = new CancellationTokenSource(WaitTimeout);
        var time = new FakeTimeProvider();
        await using var provider = CreateProvider(time);
        var hostedService = provider.GetRequiredService<IHostedService>();
        await hostedService.StartAsync(cts.Token);

        var scheduler = provider.GetRequiredService<IJobScheduler>();
        var inspector = provider.GetRequiredService<IJobSchedulerInspector>();

        await scheduler.EnqueueAsync<DisabledRuntimeJob, TestPayload>(
            new TestPayload { Value = "disabled" },
            cts.Token);

        await WaitUntilAsync(() => inspector.GetSnapshot().RecentOutcomes.Any(outcome =>
            outcome.JobName == "test.disabledRuntime" &&
            outcome.Status == ScheduledJobRunStatus.Skipped &&
            outcome.Message == "disabled"));
        await hostedService.StopAsync(cts.Token);

        var schedule = activities.Activities.Single(activity => activity.DisplayName == "scheduler schedule test.disabledRuntime");
        var skipped = activities.Activities.Single(activity => activity.DisplayName == "scheduled test.disabledRuntime skipped");
        skipped.TraceId.Should().Be(schedule.TraceId);
        skipped.ParentSpanId.Should().Be(schedule.SpanId);
    }

    [Fact]
    public async Task RecurringJob_MillisecondInterval_RunsOncePerInterval()
    {
        using var cts = new CancellationTokenSource(WaitTimeout);
        var time = new FakeTimeProvider();
        var descriptor = CreateCountingDescriptor("test.milliseconds", ScheduledJobSchedule.FixedRate("50ms"));
        await using var provider = CreateProvider(time, descriptor);
        var hostedService = provider.GetRequiredService<IHostedService>();
        var counter = provider.GetRequiredService<RunCounter>();

        await hostedService.StartAsync(cts.Token);
        await WaitUntilAsync(() => counter.Count >= 1);

        for (var expected = 2; expected <= 4; expected++) {
            time.Advance(Interval);
            await WaitUntilAsync(() => counter.Count >= expected);
        }

        await hostedService.StopAsync(cts.Token);
        counter.Count.Should().Be(4);
    }

    [Fact]
    public async Task RecurringJob_LongPause_FireOnceRunsSingleOverdueOccurrence()
    {
        using var cts = new CancellationTokenSource(WaitTimeout);
        var time = new FakeTimeProvider();
        var descriptor = CreateCountingDescriptor("test.catchUp", ScheduledJobSchedule.FixedRate("50ms"));
        await using var provider = CreateProvider(time, descriptor);
        var hostedService = provider.GetRequiredService<IHostedService>();
        var counter = provider.GetRequiredService<RunCounter>();

        await hostedService.StartAsync(cts.Token);
        await WaitUntilAsync(() => counter.Count >= 1);

        // Jump 10 seconds (200 missed 50ms slots): exactly one occurrence may run.
        time.Advance(TimeSpan.FromSeconds(10));
        await WaitUntilAsync(() => counter.Count >= 2);
        await Task.Delay(250, cts.Token);

        await hostedService.StopAsync(cts.Token);
        counter.Count.Should().Be(2);
    }

    [Fact]
    public async Task RecurringJob_LongPause_SkipMisfirePolicySkipsOverdueOccurrence()
    {
        using var cts = new CancellationTokenSource(WaitTimeout);
        var time = new FakeTimeProvider();
        var descriptor = CreateCountingDescriptor(
            "test.skipMisfire",
            ScheduledJobSchedule.FixedRate("50ms"),
            ScheduledJobMisfirePolicy.Skip);
        await using var provider = CreateProvider(time, descriptor);
        var hostedService = provider.GetRequiredService<IHostedService>();
        var inspector = provider.GetRequiredService<IJobSchedulerInspector>();
        var counter = provider.GetRequiredService<RunCounter>();

        await hostedService.StartAsync(cts.Token);
        await WaitUntilAsync(() => counter.Count >= 1);

        time.Advance(TimeSpan.FromSeconds(10));
        await WaitUntilAsync(() => inspector.GetSnapshot().RecentOutcomes.Any(outcome =>
            outcome.JobName == "test.skipMisfire" &&
            outcome.Status == ScheduledJobRunStatus.Skipped &&
            outcome.Message == "misfire"));
        await Task.Delay(100, cts.Token);

        await hostedService.StopAsync(cts.Token);
        counter.Count.Should().Be(1);
    }

    [Fact]
    public async Task RecurringJob_SkipMisfire_EmitsSkippedTrace() {
        using var activities = new ActivityCollector(SchedulerTelemetry.ActivitySourceName);
        using var cts = new CancellationTokenSource(WaitTimeout);
        var time = new FakeTimeProvider();
        var descriptor = CreateCountingDescriptor(
            "test.traceSkipMisfire",
            ScheduledJobSchedule.FixedRate("50ms"),
            ScheduledJobMisfirePolicy.Skip);
        await using var provider = CreateProvider(time, descriptor);
        var hostedService = provider.GetRequiredService<IHostedService>();
        var inspector = provider.GetRequiredService<IJobSchedulerInspector>();

        await hostedService.StartAsync(cts.Token);
        await WaitUntilAsync(() => provider.GetRequiredService<RunCounter>().Count >= 1);

        time.Advance(TimeSpan.FromSeconds(10));
        await WaitUntilAsync(() => inspector.GetSnapshot().RecentOutcomes.Any(outcome =>
            outcome.JobName == "test.traceSkipMisfire" &&
            outcome.Status == ScheduledJobRunStatus.Skipped &&
            outcome.Message == "misfire"));

        await hostedService.StopAsync(cts.Token);
        activities.Activities.Should().Contain(activity =>
            activity.DisplayName == "scheduled test.traceSkipMisfire skipped" &&
            Equals(activity.GetTag("scheduler.job.status"), "skipped") &&
            Equals(activity.GetTag("scheduler.skip.reason"), "misfire"));
    }

    [Fact]
    public async Task RecurringJob_LongPause_CatchUpMisfirePolicyRunsBoundedMissedOccurrences()
    {
        using var cts = new CancellationTokenSource(WaitTimeout);
        var time = new FakeTimeProvider();
        var descriptor = CreateCountingDescriptor(
            "test.catchUpMisfire",
            ScheduledJobSchedule.FixedRate("50ms"),
            ScheduledJobMisfirePolicy.CatchUp);
        var options = new SchedulerOptions {
            Enabled = true,
            MaxConcurrentExecutions = 8,
            MaxMisfireCatchUpRuns = 2
        };
        await using var provider = CreateProvider(time, options, descriptor);
        var hostedService = provider.GetRequiredService<IHostedService>();
        var counter = provider.GetRequiredService<RunCounter>();

        await hostedService.StartAsync(cts.Token);
        await WaitUntilAsync(() => counter.Count >= 1);

        time.Advance(TimeSpan.FromSeconds(10));
        await WaitUntilAsync(() => counter.Count >= 4);
        await Task.Delay(100, cts.Token);

        await hostedService.StopAsync(cts.Token);
        counter.Count.Should().Be(4);
    }

    [Fact]
    public async Task FixedDelayJob_NextRunIsScheduledAfterCompletion()
    {
        using var cts = new CancellationTokenSource(WaitTimeout);
        var time = new FakeTimeProvider();
        var descriptor = CreateBlockingDescriptor(
            "test.fixedDelay",
            ScheduledJobOverlap.Skip,
            ScheduledJobSchedule.FixedDelay("50ms"));
        await using var provider = CreateProvider(time, descriptor);
        var hostedService = provider.GetRequiredService<IHostedService>();
        var probe = provider.GetRequiredService<OverlapProbe>();

        await hostedService.StartAsync(cts.Token);
        await WaitUntilAsync(() => probe.StartedCount >= 1);

        // While the first run blocks, no further occurrence even exists: the chain only
        // advances after completion, so elapsing intervals dispatch nothing.
        for (var i = 0; i < 4; i++) {
            time.Advance(Interval);
            await Task.Delay(50, cts.Token);
        }

        probe.StartedCount.Should().Be(1);

        // After completion the next run is due one full delay later, not immediately.
        probe.ReleaseAll();
        await Task.Delay(150, cts.Token);
        probe.StartedCount.Should().Be(1);

        time.Advance(Interval);
        await WaitUntilAsync(() => probe.StartedCount >= 2);
        await hostedService.StopAsync(cts.Token);
    }

    [Fact]
    public async Task FixedDelayJob_DisabledOccurrence_ReschedulesForLaterConfigChanges()
    {
        using var cts = new CancellationTokenSource(WaitTimeout);
        var time = new FakeTimeProvider();
        var configuration = new ConfigurationManager {
            ["Jobs:Enabled"] = "false"
        };
        var descriptor = CreateCountingDescriptor(
            "test.fixedDelayToggle",
            ScheduledJobSchedule.FixedDelay("50ms")) with {
            Enabled = "${Jobs:Enabled}"
        };
        await using var provider = CreateProvider(time, configuration, descriptor);
        var hostedService = provider.GetRequiredService<IHostedService>();
        var counter = provider.GetRequiredService<RunCounter>();

        await hostedService.StartAsync(cts.Token);
        await Task.Delay(100, cts.Token);
        counter.Count.Should().Be(0);

        configuration["Jobs:Enabled"] = "true";
        time.Advance(Interval);
        await WaitUntilAsync(() => counter.Count >= 1);

        await hostedService.StopAsync(cts.Token);
    }

    [Fact]
    public async Task CronJob_RunsAtCronOccurrences()
    {
        using var cts = new CancellationTokenSource(WaitTimeout);
        var time = new FakeTimeProvider();
        var descriptor = CreateCountingDescriptor(
            "test.cron",
            ScheduledJobSchedule.Cron("*/30 * * * * *"));
        await using var provider = CreateProvider(time, descriptor);
        var hostedService = provider.GetRequiredService<IHostedService>();
        var counter = provider.GetRequiredService<RunCounter>();

        await hostedService.StartAsync(cts.Token);

        // The scheduler loop starts asynchronously and anchors the cron occurrences to
        // whatever the fake clock reads at that point, so advance in slot-sized steps.
        // Cron rescheduling skips missed slots, so each advance fires at most one run.
        await AdvanceUntilAsync(time, TimeSpan.FromSeconds(30), () => counter.Count >= 1);
        await AdvanceUntilAsync(time, TimeSpan.FromSeconds(30), () => counter.Count >= 2);

        await hostedService.StopAsync(cts.Token);
        counter.Count.Should().Be(2);
    }

    [Fact]
    public async Task CronJob_DisabledSentinel_DoesNotRun()
    {
        using var cts = new CancellationTokenSource(WaitTimeout);
        var time = new FakeTimeProvider();
        var descriptor = CreateCountingDescriptor(
            "test.disabledCron",
            ScheduledJobSchedule.Cron("-"));
        await using var provider = CreateProvider(time, descriptor);
        var hostedService = provider.GetRequiredService<IHostedService>();
        var counter = provider.GetRequiredService<RunCounter>();

        await hostedService.StartAsync(cts.Token);
        time.Advance(TimeSpan.FromHours(1));
        await Task.Delay(100, cts.Token);
        await hostedService.StopAsync(cts.Token);

        counter.Count.Should().Be(0);
    }

    [Fact]
    public async Task OneTimeJob_RunsOnceAfterInitialDelay()
    {
        using var cts = new CancellationTokenSource(WaitTimeout);
        var time = new FakeTimeProvider();
        var descriptor = CreateCountingDescriptor(
            "test.once",
            ScheduledJobSchedule.Once("50ms"));
        await using var provider = CreateProvider(time, descriptor);
        var hostedService = provider.GetRequiredService<IHostedService>();
        var counter = provider.GetRequiredService<RunCounter>();

        await hostedService.StartAsync(cts.Token);
        await AdvanceUntilAsync(time, Interval, () => counter.Count >= 1);

        time.Advance(TimeSpan.FromSeconds(10));
        await Task.Delay(100, cts.Token);
        await hostedService.StopAsync(cts.Token);

        counter.Count.Should().Be(1);
    }

    [Fact]
    public async Task RecurringJob_Skip_DropsOverlappingRuns()
    {
        using var cts = new CancellationTokenSource(WaitTimeout);
        var time = new FakeTimeProvider();
        var descriptor = CreateBlockingDescriptor("test.skip", ScheduledJobOverlap.Skip);
        await using var provider = CreateProvider(time, descriptor);
        var hostedService = provider.GetRequiredService<IHostedService>();
        var probe = provider.GetRequiredService<OverlapProbe>();

        await hostedService.StartAsync(cts.Token);
        await WaitUntilAsync(() => probe.StartedCount >= 1);

        for (var i = 0; i < 3; i++) {
            time.Advance(Interval);
            await Task.Delay(50, cts.Token);
        }

        probe.StartedCount.Should().Be(1);

        probe.ReleaseAll();
        await hostedService.StopAsync(cts.Token);
    }

    [Fact]
    public async Task RecurringJob_Queue_CoalescesIntoSinglePendingRun()
    {
        using var cts = new CancellationTokenSource(WaitTimeout);
        var time = new FakeTimeProvider();
        var descriptor = CreateBlockingDescriptor("test.queue", ScheduledJobOverlap.Queue);
        await using var provider = CreateProvider(time, descriptor);
        var hostedService = provider.GetRequiredService<IHostedService>();
        var probe = provider.GetRequiredService<OverlapProbe>();

        await hostedService.StartAsync(cts.Token);
        await WaitUntilAsync(() => probe.StartedCount >= 1);

        // While the first run blocks, several intervals elapse: one occurrence queues
        // behind it and the rest coalesce away.
        for (var i = 0; i < 4; i++) {
            time.Advance(Interval);
            await Task.Delay(50, cts.Token);
        }

        probe.StartedCount.Should().Be(1);

        probe.ReleaseAll();
        await WaitUntilAsync(() => probe.StartedCount >= 2);
        await Task.Delay(150, cts.Token);
        await hostedService.StopAsync(cts.Token);

        probe.StartedCount.Should().Be(2);
        probe.MaxActiveCount.Should().Be(1);
    }

    [Fact]
    public async Task RecurringJob_AllowConcurrent_StartsOverlappingRuns()
    {
        using var cts = new CancellationTokenSource(WaitTimeout);
        var time = new FakeTimeProvider();
        var descriptor = CreateBlockingDescriptor("test.allowConcurrent", ScheduledJobOverlap.AllowConcurrent);
        await using var provider = CreateProvider(time, descriptor);
        var hostedService = provider.GetRequiredService<IHostedService>();
        var probe = provider.GetRequiredService<OverlapProbe>();

        await hostedService.StartAsync(cts.Token);
        await WaitUntilAsync(() => probe.StartedCount >= 1);

        // Advance in steps until the overlapping run dispatches. A single Advance can land
        // before the scheduler registers its next-occurrence wait timer on the fake clock,
        // which loses the tick and stalls the run; AdvanceUntilAsync re-advances until it fires.
        await AdvanceUntilAsync(time, Interval, () => probe.StartedCount >= 2);

        probe.MaxActiveCount.Should().BeGreaterThan(1);

        probe.ReleaseAll();
        await hostedService.StopAsync(cts.Token);
    }

    [Fact]
    public async Task RecurringJob_AllowConcurrentWithMaxConcurrentRuns_SkipsAboveCap()
    {
        using var cts = new CancellationTokenSource(WaitTimeout);
        var time = new FakeTimeProvider();
        var descriptor = CreateBlockingDescriptor(
            "test.allowConcurrentCapped",
            ScheduledJobOverlap.AllowConcurrent,
            maxConcurrentRuns: 2);
        await using var provider = CreateProvider(time, descriptor);
        var hostedService = provider.GetRequiredService<IHostedService>();
        var probe = provider.GetRequiredService<OverlapProbe>();

        await hostedService.StartAsync(cts.Token);
        await WaitUntilAsync(() => probe.StartedCount >= 1);

        // Same fake-clock race as RecurringJob_AllowConcurrent_StartsOverlappingRuns: a single
        // Advance can be lost if it beats the scheduler's wait-timer registration. The cap then
        // holds StartedCount/MaxActiveCount at 2 regardless of how far the clock is advanced.
        await AdvanceUntilAsync(time, Interval, () => probe.StartedCount >= 2);
        time.Advance(Interval);
        await Task.Delay(100, cts.Token);

        probe.StartedCount.Should().Be(2);
        probe.MaxActiveCount.Should().Be(2);

        probe.ReleaseAll();
        await hostedService.StopAsync(cts.Token);
    }

    [Fact]
    public async Task CancelRunAsync_ActiveRun_RequestsCancellation()
    {
        using var cts = new CancellationTokenSource(WaitTimeout);
        var time = new FakeTimeProvider();
        await using var provider = CreateProvider(time);
        var hostedService = provider.GetRequiredService<IHostedService>();
        var scheduler = provider.GetRequiredService<IJobScheduler>();
        var probe = provider.GetRequiredService<CancellationProbe>();

        await hostedService.StartAsync(cts.Token);
        var handle = await scheduler.EnqueueAsync<CancellableRuntimeJob, TestPayload>(
            new TestPayload { Value = "active" },
            cts.Token);
        await probe.WaitStartedAsync(cts.Token);

        var cancelled = await scheduler.CancelRunAsync(handle.RunId, cts.Token);
        await probe.WaitCancelledAsync(cts.Token);
        await hostedService.StopAsync(cts.Token);

        cancelled.Should().BeTrue();
    }

    [Fact]
    public async Task CancelRunAsync_RunWaitingForGlobalConcurrency_CancelsBeforeJobStarts()
    {
        using var cts = new CancellationTokenSource(WaitTimeout);
        var time = new FakeTimeProvider();
        await using var provider = CreateProvider(
            time,
            new SchedulerOptions { Enabled = true, MaxConcurrentExecutions = 1 });
        var hostedService = provider.GetRequiredService<IHostedService>();
        var scheduler = provider.GetRequiredService<IJobScheduler>();
        var inspector = provider.GetRequiredService<IJobSchedulerInspector>();
        var probe = provider.GetRequiredService<CancellationProbe>();
        var recorder = provider.GetRequiredService<SchedulerRecorder>();

        await hostedService.StartAsync(cts.Token);
        var blockingHandle = await scheduler.EnqueueAsync<CancellableRuntimeJob, TestPayload>(
            new TestPayload { Value = "blocking" },
            cts.Token);
        await probe.WaitStartedAsync(cts.Token);

        var waitingHandle = await scheduler.EnqueueAsync<TestRuntimeJob, TestPayload>(
            new TestPayload { Value = "waiting" },
            cts.Token);
        await WaitUntilAsync(() => inspector.GetSnapshot().ActiveRuns.Any(run => run.RunId == waitingHandle.RunId));

        var cancelled = await scheduler.CancelRunAsync(waitingHandle.RunId, cts.Token);
        await WaitUntilAsync(() => inspector.GetSnapshot().RecentOutcomes.Any(outcome =>
            outcome.RunId == waitingHandle.RunId &&
            outcome.Status == ScheduledJobRunStatus.Cancelled));

        await scheduler.CancelRunAsync(blockingHandle.RunId, cts.Token);
        await probe.WaitCancelledAsync(cts.Token);
        await hostedService.StopAsync(cts.Token);

        cancelled.Should().BeTrue();
        recorder.HasObserved.Should().BeFalse();
    }

    [Fact]
    public async Task IScheduledJobContext_RequestCancellation_MarksRunCancelled()
    {
        using var cts = new CancellationTokenSource(WaitTimeout);
        var time = new FakeTimeProvider();
        var observedCancellation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var descriptor = new ScheduledJobDescriptor {
            Name = "test.contextCancellation",
            Schedule = ScheduledJobSchedule.Once("50ms"),
            InvokeAsync = (_, _, context, ct) => {
                context.RequestCancellation();
                observedCancellation.SetResult(ct.IsCancellationRequested);
                return ValueTask.CompletedTask;
            }
        };
        await using var provider = CreateProvider(time, descriptor);
        var hostedService = provider.GetRequiredService<IHostedService>();
        var inspector = provider.GetRequiredService<IJobSchedulerInspector>();

        await hostedService.StartAsync(cts.Token);
        await AdvanceUntilAsync(time, Interval, () => observedCancellation.Task.IsCompleted);
        var tokenWasCancelled = await observedCancellation.Task.WaitAsync(cts.Token);
        await WaitUntilAsync(() => inspector.GetSnapshot().RecentOutcomes.Any(outcome =>
            outcome.JobName == "test.contextCancellation" &&
            outcome.Status == ScheduledJobRunStatus.Cancelled));
        await hostedService.StopAsync(cts.Token);

        tokenWasCancelled.Should().BeTrue();
    }

    [Fact]
    public async Task CronJob_DisabledPlaceholderAfterStartup_SkipsFutureOccurrences()
    {
        using var cts = new CancellationTokenSource(WaitTimeout);
        var time = new FakeTimeProvider();
        var configuration = new ConfigurationManager {
            ["Jobs:Cron"] = "*/30 * * * * *"
        };
        var descriptor = CreateCountingDescriptor(
            "test.cronToggle",
            ScheduledJobSchedule.Cron("${Jobs:Cron}"));
        await using var provider = CreateProvider(time, configuration, descriptor);
        var hostedService = provider.GetRequiredService<IHostedService>();
        var inspector = provider.GetRequiredService<IJobSchedulerInspector>();
        var counter = provider.GetRequiredService<RunCounter>();

        await hostedService.StartAsync(cts.Token);
        await AdvanceUntilAsync(time, TimeSpan.FromSeconds(30), () => counter.Count >= 1);

        configuration["Jobs:Cron"] = "-";
        time.Advance(TimeSpan.FromSeconds(30));
        await WaitUntilAsync(() => inspector.GetSnapshot().RecentOutcomes.Any(outcome =>
            outcome.JobName == "test.cronToggle" &&
            outcome.Status == ScheduledJobRunStatus.Skipped));
        await hostedService.StopAsync(cts.Token);

        counter.Count.Should().Be(1);
    }

    [Fact]
    public async Task GetSnapshot_ReportsDescriptorsQueuedActiveAndOutcomes()
    {
        using var cts = new CancellationTokenSource(WaitTimeout);
        var time = new FakeTimeProvider();
        var descriptor = CreateBlockingDescriptor("test.snapshot", ScheduledJobOverlap.AllowConcurrent);
        await using var provider = CreateProvider(time, descriptor);
        var hostedService = provider.GetRequiredService<IHostedService>();
        var inspector = provider.GetRequiredService<IJobSchedulerInspector>();
        var scheduler = provider.GetRequiredService<IJobScheduler>();
        var probe = provider.GetRequiredService<OverlapProbe>();

        await hostedService.StartAsync(cts.Token);
        await WaitUntilAsync(() => probe.StartedCount >= 1);

        var handle = await scheduler.ScheduleAsync<TestRuntimeJob, TestPayload>(
            new TestPayload { Value = "future" },
            time.GetUtcNow().AddSeconds(5),
            cts.Token);
        var runningSnapshot = inspector.GetSnapshot();

        runningSnapshot.Jobs.Should().Contain(job =>
            job.Name == "test.snapshot" &&
            job.ScheduleKind == ScheduledJobScheduleKind.FixedRate &&
            job.MisfirePolicy == ScheduledJobMisfirePolicy.FireOnce &&
            job.NextDueTimeUtc != null);
        runningSnapshot.ActiveRuns.Should().Contain(run => run.JobName == "test.snapshot" && run.Status == ScheduledJobRunStatus.Running);
        runningSnapshot.QueuedRuns.Should().Contain(run => run.RunId == handle.RunId && run.Status == ScheduledJobRunStatus.Queued);

        probe.ReleaseAll();
        await WaitUntilAsync(() => inspector.GetSnapshot().RecentOutcomes.Any(outcome => outcome.JobName == "test.snapshot"));
        await hostedService.StopAsync(cts.Token);

        inspector.GetSnapshot().RecentOutcomes.Should().Contain(outcome =>
            outcome.JobName == "test.snapshot" &&
            outcome.Status == ScheduledJobRunStatus.Succeeded);
    }

    [Fact]
    public async Task QueueOverlap_PendingOccurrence_DoesNotStarveOtherJobs()
    {
        using var cts = new CancellationTokenSource(WaitTimeout);
        var time = new FakeTimeProvider();
        var descriptor = CreateBlockingDescriptor("test.starvation", ScheduledJobOverlap.Queue);
        await using var provider = CreateProvider(
            time,
            new SchedulerOptions { Enabled = true, MaxConcurrentExecutions = 2 },
            descriptor);
        var hostedService = provider.GetRequiredService<IHostedService>();
        var probe = provider.GetRequiredService<OverlapProbe>();
        var recorder = provider.GetRequiredService<SchedulerRecorder>();
        var scheduler = provider.GetRequiredService<IJobScheduler>();

        await hostedService.StartAsync(cts.Token);
        await WaitUntilAsync(() => probe.StartedCount >= 1);

        // Let one occurrence queue up behind the blocked run. It must wait on the
        // job's serialization semaphore without holding a global concurrency slot.
        time.Advance(Interval);
        await Task.Delay(100, cts.Token);

        await scheduler.EnqueueAsync<TestRuntimeJob, TestPayload>(
            new TestPayload { Value = "not starved" },
            cts.Token);
        var observed = await recorder.WaitAsync(cts.Token);

        probe.ReleaseAll();
        await hostedService.StopAsync(cts.Token);

        observed.Should().Be("not starved");
    }

    [Fact]
    public async Task DeferredRetry_RuntimeJob_SucceedsAfterRetryWithStableJobId()
    {
        using var activities = new ActivityCollector(SchedulerTelemetry.ActivitySourceName);
        using var meters = new MeterCollector(SchedulerTelemetry.MeterName);
        using var cts = new CancellationTokenSource(WaitTimeout);
        var time = new FakeTimeProvider();
        await using var provider = CreateProvider(time);
        var hostedService = provider.GetRequiredService<IHostedService>();
        var scheduler = provider.GetRequiredService<IJobScheduler>();
        var inspector = provider.GetRequiredService<IJobSchedulerInspector>();
        var recorder = provider.GetRequiredService<SchedulerRecorder>();

        await hostedService.StartAsync(cts.Token);
        var handle = await scheduler.EnqueueAsync<FlakyRuntimeJob, TestPayload>(
            new TestPayload { Value = "retry-success", FailAttempts = 1 },
            new ScheduledJobOptions {
                ResiliencePolicy = new ResiliencePolicyReference { Name = RetryPolicyName },
                ResilienceMode = ScheduledJobResilienceMode.DeferredRetry,
                CorrelationId = "invoice-42"
            },
            cts.Token);

        await WaitUntilAsync(() =>
            inspector.GetJob(handle.JobId)?.Status == ScheduledJobLifecycleStatus.WaitingRetry);
        var waiting = inspector.GetJob(handle.JobId)!;

        waiting.JobId.Should().Be(handle.JobId);
        waiting.CurrentRunId.Should().NotBe(handle.RunId);
        waiting.Attempt.Should().Be(2);
        waiting.MaxAttempts.Should().Be(3);
        waiting.CorrelationId.Should().Be("invoice-42");
        waiting.LastError.Should().Contain("transient");
        waiting.NextAttemptDueTimeUtc.Should().NotBeNull();

        time.Advance(Interval);
        var observed = await recorder.WaitAsync(cts.Token);
        await WaitUntilAsync(() =>
            inspector.GetJob(handle.JobId)?.Status == ScheduledJobLifecycleStatus.Succeeded);
        await hostedService.StopAsync(cts.Token);

        observed.Should().Be("retry-success");
        var succeeded = inspector.GetJob(handle.JobId)!;
        succeeded.Attempt.Should().Be(2);
        succeeded.CompletedAtUtc.Should().NotBeNull();
        succeeded.CurrentRunId.Should().Be(waiting.CurrentRunId);
        activities.Activities.Should().Contain(activity =>
            activity.DisplayName == "scheduler enqueue test.flakyRuntime" &&
            Equals(activity.GetTag("scheduler.job.attempt"), 2));
        meters.Measurements.Should().Contain(measurement =>
            measurement.InstrumentName == "scheduler.operation.count" &&
            measurement.HasTag("scheduler.operation", "enqueue") &&
            measurement.HasTag("scheduler.operation.outcome", "queued"));
    }

    [Fact]
    public async Task DeferredRetry_CancelJobAsync_CancelsWaitingRetry()
    {
        using var cts = new CancellationTokenSource(WaitTimeout);
        var time = new FakeTimeProvider();
        await using var provider = CreateProvider(time);
        var hostedService = provider.GetRequiredService<IHostedService>();
        var scheduler = provider.GetRequiredService<IJobScheduler>();
        var inspector = provider.GetRequiredService<IJobSchedulerInspector>();
        var recorder = provider.GetRequiredService<SchedulerRecorder>();

        await hostedService.StartAsync(cts.Token);
        var handle = await scheduler.EnqueueAsync<FlakyRuntimeJob, TestPayload>(
            new TestPayload { Value = "cancelled-retry", FailAttempts = 10 },
            new ScheduledJobOptions {
                ResiliencePolicy = new ResiliencePolicyReference { Name = RetryPolicyName },
                ResilienceMode = ScheduledJobResilienceMode.DeferredRetry
            },
            cts.Token);
        await WaitUntilAsync(() =>
            inspector.GetJob(handle.JobId)?.Status == ScheduledJobLifecycleStatus.WaitingRetry);

        var cancelled = await scheduler.CancelJobAsync(handle.JobId, cts.Token);
        time.Advance(TimeSpan.FromSeconds(1));
        await Task.Delay(100, cts.Token);
        await hostedService.StopAsync(cts.Token);

        cancelled.Should().BeTrue();
        inspector.GetJob(handle.JobId)?.Status.Should().Be(ScheduledJobLifecycleStatus.Cancelled);
        recorder.HasObserved.Should().BeFalse();
    }

    [Fact]
    public async Task DeferredRetry_ExhaustionRecordsFailedState()
    {
        using var cts = new CancellationTokenSource(WaitTimeout);
        var time = new FakeTimeProvider();
        await using var provider = CreateProvider(time);
        var hostedService = provider.GetRequiredService<IHostedService>();
        var scheduler = provider.GetRequiredService<IJobScheduler>();
        var inspector = provider.GetRequiredService<IJobSchedulerInspector>();

        await hostedService.StartAsync(cts.Token);
        var handle = await scheduler.EnqueueAsync<FlakyRuntimeJob, TestPayload>(
            new TestPayload { Value = "exhaust", FailAttempts = 10 },
            new ScheduledJobOptions {
                ResiliencePolicy = new ResiliencePolicyReference { Name = RetryPolicyName },
                ResilienceMode = ScheduledJobResilienceMode.DeferredRetry
            },
            cts.Token);

        while (inspector.GetJob(handle.JobId)?.Status != ScheduledJobLifecycleStatus.Failed) {
            await WaitUntilAsync(() => inspector.GetJob(handle.JobId)?.Status is
                ScheduledJobLifecycleStatus.WaitingRetry or ScheduledJobLifecycleStatus.Failed);
            var state = inspector.GetJob(handle.JobId)!;
            if (state.Status == ScheduledJobLifecycleStatus.Failed) {
                break;
            }

            time.Advance(state.NextAttemptDueTimeUtc!.Value - time.GetUtcNow());
        }

        await hostedService.StopAsync(cts.Token);

        var failed = inspector.GetJob(handle.JobId)!;
        failed.Status.Should().Be(ScheduledJobLifecycleStatus.Failed);
        failed.Attempt.Should().Be(3);
        failed.MaxAttempts.Should().Be(3);
        failed.LastError.Should().Contain("transient");
        failed.CompletedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task DeferredRetry_NonRetryableExceptionRecordsFailedWithoutRetry()
    {
        using var cts = new CancellationTokenSource(WaitTimeout);
        var time = new FakeTimeProvider();
        await using var provider = CreateProvider(time);
        var hostedService = provider.GetRequiredService<IHostedService>();
        var scheduler = provider.GetRequiredService<IJobScheduler>();
        var inspector = provider.GetRequiredService<IJobSchedulerInspector>();

        await hostedService.StartAsync(cts.Token);
        var handle = await scheduler.EnqueueAsync<FlakyRuntimeJob, TestPayload>(
            new TestPayload { Value = "terminal", FailAttempts = 10, NonRetryable = true },
            new ScheduledJobOptions {
                ResiliencePolicy = new ResiliencePolicyReference { Name = RetryPolicyName },
                ResilienceMode = ScheduledJobResilienceMode.DeferredRetry
            },
            cts.Token);

        await WaitUntilAsync(() =>
            inspector.GetJob(handle.JobId)?.Status == ScheduledJobLifecycleStatus.Failed);
        await hostedService.StopAsync(cts.Token);

        var failed = inspector.GetJob(handle.JobId)!;
        failed.Attempt.Should().Be(1);
        failed.MaxAttempts.Should().Be(3);
        failed.LastError.Should().Contain("terminal");
    }

    [Fact]
    public async Task InlineResilience_RuntimeJob_RetriesInsideSingleSchedulerRun()
    {
        using var cts = new CancellationTokenSource(WaitTimeout);
        var time = new FakeTimeProvider();
        await using var provider = CreateProvider(time);
        var hostedService = provider.GetRequiredService<IHostedService>();
        var scheduler = provider.GetRequiredService<IJobScheduler>();
        var inspector = provider.GetRequiredService<IJobSchedulerInspector>();
        var recorder = provider.GetRequiredService<SchedulerRecorder>();

        await hostedService.StartAsync(cts.Token);
        var handle = await scheduler.EnqueueAsync<FlakyRuntimeJob, TestPayload>(
            new TestPayload { Value = "inline-success", FailAttempts = 1 },
            new ScheduledJobOptions {
                ResiliencePolicy = new ResiliencePolicyReference { Name = InlineRetryPolicyName }
            },
            cts.Token);

        var observed = await recorder.WaitAsync(cts.Token);
        await WaitUntilAsync(() =>
            inspector.GetJob(handle.JobId)?.Status == ScheduledJobLifecycleStatus.Succeeded);
        await hostedService.StopAsync(cts.Token);

        observed.Should().Be("inline-success");
        var succeeded = inspector.GetJob(handle.JobId)!;
        succeeded.CurrentRunId.Should().Be(handle.RunId);
        succeeded.Attempt.Should().Be(1);
        succeeded.MaxAttempts.Should().Be(1);
    }

    [Fact]
    public async Task DeferredRetry_UnknownPolicyMetadata_ThrowsWhenScheduling()
    {
        using var cts = new CancellationTokenSource(WaitTimeout);
        var time = new FakeTimeProvider();
        await using var provider = CreateProvider(time);
        var scheduler = provider.GetRequiredService<IJobScheduler>();

        var act = async () => await scheduler.EnqueueAsync<FlakyRuntimeJob, TestPayload>(
            new TestPayload { Value = "unknown", FailAttempts = 1 },
            new ScheduledJobOptions {
                ResiliencePolicy = new ResiliencePolicyReference { Name = "unknown-policy" },
                ResilienceMode = ScheduledJobResilienceMode.DeferredRetry
            },
            cts.Token);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(act);
        exception.Message.Should().Contain("generated resilience metadata");
    }

    [Fact]
    public async Task DisabledRuntimeJob_RecordsSkippedState()
    {
        using var cts = new CancellationTokenSource(WaitTimeout);
        var time = new FakeTimeProvider();
        await using var provider = CreateProvider(time);
        var hostedService = provider.GetRequiredService<IHostedService>();
        var scheduler = provider.GetRequiredService<IJobScheduler>();
        var inspector = provider.GetRequiredService<IJobSchedulerInspector>();
        var recorder = provider.GetRequiredService<SchedulerRecorder>();

        await hostedService.StartAsync(cts.Token);
        var handle = await scheduler.EnqueueAsync<DisabledRuntimeJob, TestPayload>(
            new TestPayload { Value = "disabled" },
            cts.Token);

        await WaitUntilAsync(() =>
            inspector.GetJob(handle.JobId)?.Status == ScheduledJobLifecycleStatus.Skipped);
        await hostedService.StopAsync(cts.Token);

        var skipped = inspector.GetJob(handle.JobId)!;
        skipped.Attempt.Should().Be(1);
        skipped.LastError.Should().Be("disabled");
        skipped.CompletedAtUtc.Should().NotBeNull();
        recorder.HasObserved.Should().BeFalse();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition()) {
            if (stopwatch.Elapsed > WaitTimeout) {
                throw new TimeoutException("The expected scheduler state was not reached in time.");
            }

            await Task.Delay(10);
        }
    }

    private static async Task AdvanceUntilAsync(FakeTimeProvider time, TimeSpan step, Func<bool> condition)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition()) {
            if (stopwatch.Elapsed > WaitTimeout) {
                throw new TimeoutException("The expected scheduler state was not reached in time.");
            }

            time.Advance(step);
            await Task.Delay(10);
        }
    }

    private static ServiceProvider CreateProvider(
        FakeTimeProvider timeProvider,
        params ScheduledJobDescriptor[] descriptors) =>
        CreateProvider(
            timeProvider,
            new ConfigurationBuilder().Build(),
            new SchedulerOptions { Enabled = true, MaxConcurrentExecutions = 8 },
            descriptors);

    private static ServiceProvider CreateProvider(
        FakeTimeProvider timeProvider,
        SchedulerOptions options,
        params ScheduledJobDescriptor[] descriptors) =>
        CreateProvider(
            timeProvider,
            new ConfigurationBuilder().Build(),
            options,
            descriptors);

    private static ServiceProvider CreateProvider(
        FakeTimeProvider timeProvider,
        IConfiguration configuration,
        params ScheduledJobDescriptor[] descriptors) =>
        CreateProvider(
            timeProvider,
            configuration,
            new SchedulerOptions { Enabled = true, MaxConcurrentExecutions = 8 },
            descriptors);

    private static ServiceProvider CreateProvider(
        FakeTimeProvider timeProvider,
        IConfiguration configuration,
        SchedulerOptions options,
        params ScheduledJobDescriptor[] descriptors)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(configuration);
        services.AddSingleton<TimeProvider>(timeProvider);
        services.AddSingleton<SchedulerRecorder>();
        services.AddSingleton<OverlapProbe>();
        services.AddSingleton<RunCounter>();
        services.AddSingleton<CancellationProbe>();
        services.AddSingleton<FlakyRuntimeProbe>();
        services.AddScoped<TestRuntimeJob>();
        services.AddScoped<CancellableRuntimeJob>();
        services.AddScoped<FlakyRuntimeJob>();
        services.AddScoped<DisabledRuntimeJob>();
        services.AddElarionResiliencePolicyMetadata(new ResiliencePolicyMetadata {
            Name = RetryPolicyName,
            Retry = new ResilienceRetryOptions {
                MaxRetryAttempts = 2,
                Delay = Interval,
                Backoff = ResilienceBackoffType.Constant
            }
        });
        services.AddElarionResiliencePolicyMetadata(new ResiliencePolicyMetadata {
            Name = InlineRetryPolicyName,
            Retry = new ResilienceRetryOptions {
                MaxRetryAttempts = 2,
                Delay = TimeSpan.Zero,
                Backoff = ResilienceBackoffType.Constant
            }
        });
        services.AddSingleton(new ScheduledJobDescriptor {
            Name = "test.runtime",
            JobType = typeof(TestRuntimeJob),
            PayloadType = typeof(TestPayload),
            InvokeAsync = static (serviceProvider, payload, context, ct) => {
                var typedPayload = (TestPayload)payload!;
                var job = serviceProvider.GetRequiredService<TestRuntimeJob>();
                return job.ExecuteAsync(typedPayload, context, ct);
            }
        });
        services.AddSingleton(new ScheduledJobDescriptor {
            Name = "test.cancellableRuntime",
            JobType = typeof(CancellableRuntimeJob),
            PayloadType = typeof(TestPayload),
            InvokeAsync = static (serviceProvider, payload, context, ct) => {
                var typedPayload = (TestPayload)payload!;
                var job = serviceProvider.GetRequiredService<CancellableRuntimeJob>();
                return job.ExecuteAsync(typedPayload, context, ct);
            }
        });
        services.AddSingleton(new ScheduledJobDescriptor {
            Name = "test.flakyRuntime",
            JobType = typeof(FlakyRuntimeJob),
            PayloadType = typeof(TestPayload),
            InvokeAsync = static (serviceProvider, payload, context, ct) => {
                var typedPayload = (TestPayload)payload!;
                var job = serviceProvider.GetRequiredService<FlakyRuntimeJob>();
                return job.ExecuteAsync(typedPayload, context, ct);
            }
        });
        services.AddSingleton(new ScheduledJobDescriptor {
            Name = "test.disabledRuntime",
            JobType = typeof(DisabledRuntimeJob),
            PayloadType = typeof(TestPayload),
            Enabled = "false",
            InvokeAsync = static (serviceProvider, payload, context, ct) => {
                var typedPayload = (TestPayload)payload!;
                var job = serviceProvider.GetRequiredService<DisabledRuntimeJob>();
                return job.ExecuteAsync(typedPayload, context, ct);
            }
        });

        foreach (var descriptor in descriptors)
        {
            services.AddSingleton(descriptor);
        }

        services.AddElarionScheduler(options);
        // The scheduler registers the (core) policy catalog; the Polly-backed runner that executes deferred/inline
        // retries is opt-in via the Elarion.Resilience package, so wire it explicitly for resilience-exercising tests.
        services.AddElarionResilience();

        return services.BuildServiceProvider();
    }

    private static ScheduledJobDescriptor CreateBlockingDescriptor(
        string name,
        ScheduledJobOverlap overlap,
        ScheduledJobSchedule? schedule = null,
        int maxConcurrentRuns = 0) =>
        new() {
            Name = name,
            Schedule = schedule ?? ScheduledJobSchedule.FixedRate("50ms"),
            Overlap = overlap,
            MaxConcurrentRuns = maxConcurrentRuns,
            InvokeAsync = static (serviceProvider, payload, context, ct) =>
                serviceProvider.GetRequiredService<OverlapProbe>().RunAsync(ct)
        };

    private static ScheduledJobDescriptor CreateCountingDescriptor(
        string name,
        ScheduledJobSchedule schedule,
        ScheduledJobMisfirePolicy misfirePolicy = ScheduledJobMisfirePolicy.FireOnce) =>
        new() {
            Name = name,
            Schedule = schedule,
            Overlap = ScheduledJobOverlap.AllowConcurrent,
            MisfirePolicy = misfirePolicy,
            InvokeAsync = static (serviceProvider, payload, context, ct) => {
                serviceProvider.GetRequiredService<RunCounter>().Increment();
                return ValueTask.CompletedTask;
            }
        };

    private sealed record TestPayload {
        public required string Value { get; init; }

        public int FailAttempts { get; init; }

        public bool NonRetryable { get; init; }
    }

    private sealed class RunCounter {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void Increment() => Interlocked.Increment(ref _count);
    }

    private sealed class SchedulerRecorder {
        private readonly TaskCompletionSource<string> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool HasObserved => _completion.Task.IsCompleted;

        public void Record(string value) => _completion.TrySetResult(value);

        public async Task<string> WaitAsync(CancellationToken ct)
        {
            using var registration = ct.Register(static state =>
            {
                var source = (TaskCompletionSource<string>)state!;
                source.TrySetCanceled();
            }, _completion);

            return await _completion.Task;
        }
    }

    [ScheduledJob("test.runtime")]
    private sealed class TestRuntimeJob(SchedulerRecorder recorder) : IScheduledJob<TestPayload> {
        public ValueTask ExecuteAsync(TestPayload payload, IScheduledJobContext context, CancellationToken ct)
        {
            recorder.Record(payload.Value);
            return ValueTask.CompletedTask;
        }
    }

    [ScheduledJob("test.cancellableRuntime")]
    private sealed class CancellableRuntimeJob(CancellationProbe probe) : IScheduledJob<TestPayload> {
        public async ValueTask ExecuteAsync(TestPayload payload, IScheduledJobContext context, CancellationToken ct)
        {
            probe.MarkStarted();
            try {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                probe.MarkCancelled();
                throw;
            }
        }
    }

    [ScheduledJob("test.flakyRuntime")]
    private sealed class FlakyRuntimeJob(SchedulerRecorder recorder, FlakyRuntimeProbe probe) : IScheduledJob<TestPayload> {
        public ValueTask ExecuteAsync(TestPayload payload, IScheduledJobContext context, CancellationToken ct)
        {
            var attempt = probe.Record(context.RunId);
            if (attempt <= payload.FailAttempts) {
                if (payload.NonRetryable) {
                    throw new NonRetryableException("terminal failure");
                }

                throw new InvalidOperationException("transient failure");
            }

            recorder.Record(payload.Value);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FlakyRuntimeProbe {
        private int _attempts;

        public int Record(Guid runId) => Interlocked.Increment(ref _attempts);
    }

    [ScheduledJob("test.disabledRuntime")]
    private sealed class DisabledRuntimeJob(SchedulerRecorder recorder) : IScheduledJob<TestPayload> {
        public ValueTask ExecuteAsync(TestPayload payload, IScheduledJobContext context, CancellationToken ct)
        {
            recorder.Record(payload.Value);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class OverlapProbe {
        private readonly TaskCompletionSource _releaseAll = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _activeCount;
        private int _maxActiveCount;
        private int _startedCount;

        public int StartedCount => Volatile.Read(ref _startedCount);

        public int MaxActiveCount => Volatile.Read(ref _maxActiveCount);

        public async ValueTask RunAsync(CancellationToken ct)
        {
            var active = Interlocked.Increment(ref _activeCount);
            UpdateMaxActive(active);
            Interlocked.Increment(ref _startedCount);

            try {
                await _releaseAll.Task.WaitAsync(ct);
            } finally {
                Interlocked.Decrement(ref _activeCount);
            }
        }

        public void ReleaseAll() => _releaseAll.TrySetResult();

        private void UpdateMaxActive(int active)
        {
            while (true) {
                var current = Volatile.Read(ref _maxActiveCount);
                if (active <= current) {
                    return;
                }

                if (Interlocked.CompareExchange(ref _maxActiveCount, active, current) == current) {
                    return;
                }
            }
        }
    }

    private sealed class CancellationProbe {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void MarkStarted() => _started.TrySetResult();

        public void MarkCancelled() => _cancelled.TrySetResult();

        public async Task WaitStartedAsync(CancellationToken ct) => await _started.Task.WaitAsync(ct);

        public async Task WaitCancelledAsync(CancellationToken ct) => await _cancelled.Task.WaitAsync(ct);
    }
}
