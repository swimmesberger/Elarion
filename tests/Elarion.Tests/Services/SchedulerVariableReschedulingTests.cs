using AwesomeAssertions;
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
            var before = await PollNextDueAsync(inspector, "test.live", _ => true, cts.Token);
            before.Should().Be(Origin + TimeSpan.FromMinutes(10));

            source.Update(new Dictionary<string, string?> { ["Jobs:Interval"] = "2m" });

            var after = await PollNextDueAsync(
                inspector, "test.live", due => due == Origin + TimeSpan.FromMinutes(2), cts.Token);
            after.Should().Be(Origin + TimeSpan.FromMinutes(2));
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
            await PollNextDueAsync(inspector, "test.literal", _ => true, cts.Token);
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

            var after = await PollNextDueAsync(inspector, "test.cron", _ => true, cts.Token);
            after.Should().Be(DateTimeOffset.Parse("2026-01-01T03:00:00Z"));
        } finally {
            await hosted.StopAsync(cts.Token);
        }
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
        services.AddInMemoryScheduler(new SchedulerOptions { Enabled = true, MaxConcurrentExecutions = 4 });
        return services.BuildServiceProvider();
    }

    private static ScheduledJobDescriptor Recurring(string name, ScheduledJobSchedule schedule) =>
        new() {
            Name = name,
            Schedule = schedule,
            Overlap = ScheduledJobOverlap.AllowConcurrent,
            InvokeAsync = static (_, _, _, _) => ValueTask.CompletedTask
        };

    private static DateTimeOffset? NextDue(IJobSchedulerInspector inspector, string name) =>
        inspector.GetSnapshot().Jobs.Single(job => job.Name == name).NextDueTimeUtc;

    private static Guid QueuedRunId(IJobSchedulerInspector inspector, string name) =>
        inspector.GetSnapshot().QueuedRuns.Single(run => run.JobName == name).RunId;

    private static async Task<DateTimeOffset> PollNextDueAsync(
        IJobSchedulerInspector inspector,
        string name,
        Func<DateTimeOffset, bool> predicate,
        CancellationToken ct) {
        while (true) {
            if (NextDue(inspector, name) is { } due && predicate(due)) {
                return due;
            }

            ct.ThrowIfCancellationRequested();
            await Task.Delay(10, ct);
        }
    }

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
