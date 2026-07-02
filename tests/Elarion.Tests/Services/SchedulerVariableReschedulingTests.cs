using AwesomeAssertions;
using Elarion.Abstractions.Resilience;
using Elarion.Abstractions.Scheduling;
using Elarion.Abstractions.Substitution;
using Elarion.Scheduling;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Primitives;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Elarion.Tests.Services;

public sealed class SchedulerVariableReschedulingTests {
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);
    private static readonly DateTimeOffset Origin = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

    [Fact]
    public async Task FixedRateJob_ReschedulesLive_WhenIntervalVariableChanges() {
        using var cts = new CancellationTokenSource(WaitTimeout);
        var time = new FakeTimeProvider(Origin);
        var source = new MutableVariableSource(new Dictionary<string, string?> { ["Jobs:Interval"] = "10m" });
        var descriptor = Recurring("test.live", ScheduledJobSchedule.FixedRate("${Jobs:Interval:-10m}", runOnStart: false));
        await using var provider = BuildProvider(time, source, descriptor);
        var hosted = provider.GetRequiredService<IHostedService>();
        var inspector = provider.GetRequiredService<IJobSchedulerInspector>();
        await hosted.StartAsync(cts.Token);

        try {
            // The initial occurrence is enqueued synchronously during StartAsync, and the variable change
            // resyncs synchronously inside Update (the watch token fires on the calling thread), so the state
            // is fully settled by the time each call returns — no polling or wall-clock wait is involved.
            NextDue(inspector, "test.live").Should().Be(Origin + TimeSpan.FromMinutes(10));

            source.Update(new Dictionary<string, string?> { ["Jobs:Interval"] = "2m" });

            NextDue(inspector, "test.live").Should().Be(Origin + TimeSpan.FromMinutes(2));
        } finally {
            await hosted.StopAsync(cts.Token);
        }
    }

    [Fact]
    public async Task LiteralSchedule_NotRescheduled_WhenUnrelatedVariableChanges() {
        using var cts = new CancellationTokenSource(WaitTimeout);
        var time = new FakeTimeProvider(Origin);
        var source = new MutableVariableSource(new Dictionary<string, string?> { ["Unrelated"] = "before" });
        var descriptor = Recurring("test.literal", ScheduledJobSchedule.FixedRate("10m", runOnStart: false));
        await using var provider = BuildProvider(time, source, descriptor);
        var hosted = provider.GetRequiredService<IHostedService>();
        var inspector = provider.GetRequiredService<IJobSchedulerInspector>();
        await hosted.StartAsync(cts.Token);

        try {
            // The literal-schedule occurrence is enqueued synchronously during StartAsync.
            NextDue(inspector, "test.literal").Should().Be(Origin + TimeSpan.FromMinutes(10));
            var originalRunId = QueuedRunId(inspector, "test.literal");

            source.Update(new Dictionary<string, string?> { ["Unrelated"] = "after" });

            // A literal schedule's variable signature never changes, so the occurrence is left untouched
            // (same run id) rather than superseded and re-enqueued.
            QueuedRunId(inspector, "test.literal").Should().Be(originalRunId);
            NextDue(inspector, "test.literal").Should().Be(Origin + TimeSpan.FromMinutes(10));
        } finally {
            await hosted.StopAsync(cts.Token);
        }
    }

    [Fact]
    public async Task CronJob_EnqueuesLive_WhenEnabledByVariableChange() {
        using var cts = new CancellationTokenSource(WaitTimeout);
        var time = new FakeTimeProvider(Origin);
        // ${Cron:--} resolves to the cron-disabled sentinel "-" until the variable is set.
        var source = new MutableVariableSource([]);
        var descriptor = Recurring("test.cron", ScheduledJobSchedule.Cron("${Cron:--}"));
        await using var provider = BuildProvider(time, source, descriptor);
        var hosted = provider.GetRequiredService<IHostedService>();
        var inspector = provider.GetRequiredService<IJobSchedulerInspector>();
        await hosted.StartAsync(cts.Token);

        try {
            NextDue(inspector, "test.cron").Should().BeNull();

            source.Update(new Dictionary<string, string?> { ["Cron"] = "0 0 3 * * *" });

            // Resync runs synchronously inside Update, so the newly enabled occurrence is queued before it returns.
            NextDue(inspector, "test.cron").Should().Be(DateTimeOffset.Parse("2026-01-01T03:00:00Z"));
        } finally {
            await hosted.StopAsync(cts.Token);
        }
    }

    [Fact]
    public async Task FixedRateJob_ActiveRun_ReschedulesSuccessorOnGrid_WithoutImmediateExtraRun() {
        using var cts = new CancellationTokenSource(WaitTimeout);
        var time = new FakeTimeProvider(Origin);
        var source = new MutableVariableSource(new Dictionary<string, string?> { ["Jobs:Interval"] = "10m" });
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // runOnStart => the first occurrence is due immediately; the grid successor is queued at dispatch,
        // then the run blocks on the gate so it stays active while the variable change fires.
        var descriptor = Recurring(
            "test.active",
            ScheduledJobSchedule.FixedRate("${Jobs:Interval:-10m}", runOnStart: true),
            invoke: async (_, _, _, ct) => {
                running.TrySetResult();
                await gate.Task.WaitAsync(ct);
            });
        await using var provider = BuildProvider(time, source, descriptor);
        var hosted = provider.GetRequiredService<IHostedService>();
        var inspector = provider.GetRequiredService<IJobSchedulerInspector>();
        await hosted.StartAsync(cts.Token);

        try {
            await running.Task.WaitAsync(cts.Token);
            await WaitUntilAsync(() => inspector.GetSnapshot().ActiveRuns.Count == 1, cts.Token);
            // The grid successor is queued while the first run is active.
            NextDue(inspector, "test.active").Should().Be(Origin + TimeSpan.FromMinutes(10));

            source.Update(new Dictionary<string, string?> { ["Jobs:Interval"] = "2m" });

            // The superseded successor must be recomputed on the new grid strictly after the previous
            // occurrence (Origin + 2m), NOT enqueued immediately at Origin (which would race the active run).
            NextDue(inspector, "test.active").Should().Be(Origin + TimeSpan.FromMinutes(2));
            inspector.GetSnapshot().ActiveRuns.Count.Should().Be(1);
        } finally {
            gate.TrySetResult();
            await hosted.StopAsync(cts.Token);
        }
    }

    [Fact]
    public async Task StartAsync_Throws_WhenInlineResilienceJobRegistered_ButRunnerMissing() {
        using var cts = new CancellationTokenSource(WaitTimeout);
        var time = new FakeTimeProvider(Origin);
        var source = new MutableVariableSource([]);
        var descriptor = Recurring("test.resilient", ScheduledJobSchedule.FixedRate("10m", runOnStart: false)) with {
            ResiliencePolicy = new ResiliencePolicyReference { Name = "missing-runner-policy" }
        };
        await using var provider = BuildProvider(time, source, descriptor);
        var hosted = provider.GetRequiredService<IHostedService>();

        var act = () => hosted.StartAsync(cts.Token);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .Where(ex => ex.Message.Contains("test.resilient")
                && ex.Message.Contains("IResiliencePipelineRunner")
                && ex.Message.Contains("AddElarionResilience"));
    }

    [Fact]
    public void ConfigurationVariableSource_Watch_FiresOnConfigurationReload() {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection()
            .Build();
        var source = new ConfigurationVariableSource(configuration);
        var token = source.Watch();

        configuration.Reload(); // a provider reload fires the configuration reload token

        token.HasChanged.Should().BeTrue();
    }

    private static ServiceProvider BuildProvider(
        FakeTimeProvider time,
        IVariableSource source,
        ScheduledJobDescriptor descriptor) {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(time);
        services.AddSingleton(source);
        services.AddSingleton(descriptor);
        services.AddElarionScheduler(new SchedulerOptions { Enabled = true, MaxConcurrentExecutions = 4 });
        return services.BuildServiceProvider();
    }

    private static ScheduledJobDescriptor Recurring(
        string name,
        ScheduledJobSchedule schedule,
        ScheduledJobInvokeDelegate? invoke = null) =>
        new() {
            Name = name,
            Schedule = schedule,
            Overlap = ScheduledJobOverlap.AllowConcurrent,
            InvokeAsync = invoke ?? (static (_, _, _, _) => ValueTask.CompletedTask)
        };

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct) {
        while (!condition()) {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(5, ct);
        }
    }

    private static DateTimeOffset? NextDue(IJobSchedulerInspector inspector, string name) =>
        inspector.GetSnapshot().Jobs.Single(job => job.Name == name).NextDueTimeUtc;

    private static Guid QueuedRunId(IJobSchedulerInspector inspector, string name) =>
        inspector.GetSnapshot().QueuedRuns.Single(run => run.JobName == name).RunId;

    private sealed class MutableVariableSource(Dictionary<string, string?> values) : IObservableVariableSource {
        private readonly Lock _gate = new();
        private Dictionary<string, string?> _values = values;
        private CancellationTokenSource _cts = new();

        public bool TryGetValue(string key, out string? value) {
            lock (_gate) {
                return _values.TryGetValue(key, out value);
            }
        }

        public IChangeToken Watch() {
            lock (_gate) {
                return new CancellationChangeToken(_cts.Token);
            }
        }

        public void Update(Dictionary<string, string?> values) {
            CancellationTokenSource previous;
            lock (_gate) {
                _values = values;
                previous = _cts;
                _cts = new CancellationTokenSource();
            }

            // Fires the watch token synchronously, so the scheduler's resync runs before this returns.
            previous.Cancel();
        }
    }
}
