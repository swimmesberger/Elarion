using AwesomeAssertions;
using Elarion.Abstractions.Scheduling;
using Elarion.Scheduling;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Elarion.Tests.Services;

/// <summary>
/// Verifies <see cref="JobPlacement"/> semantics against a coordinator that denies every claim (the
/// "another node always wins" extreme): an <see cref="JobPlacement.EveryNode"/> job must execute anyway
/// (it maintains process-local state, so coordination never applies), while the default
/// <see cref="JobPlacement.Cluster"/> job is suppressed with a <c>claimed-elsewhere</c> skip outcome.
/// </summary>
public sealed class SchedulerPlacementTests {
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task EveryNodeJob_ExecutesEvenWhenEveryClaimIsDenied() {
        using var cts = new CancellationTokenSource(WaitTimeout);
        var executed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var provider = CreateProvider(new ScheduledJobDescriptor {
            Name = "test.everyNode",
            Schedule = ScheduledJobSchedule.FixedRate("1h"),
            Placement = JobPlacement.EveryNode,
            InvokeAsync = (_, _, _, _) => {
                executed.TrySetResult();
                return ValueTask.CompletedTask;
            }
        });
        var hostedService = provider.GetRequiredService<IHostedService>();

        await hostedService.StartAsync(cts.Token);
        try {
            await executed.Task.WaitAsync(cts.Token);
        }
        finally {
            await hostedService.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ClusterJob_IsSkippedAsClaimedElsewhereWhenTheClaimIsDenied() {
        using var cts = new CancellationTokenSource(WaitTimeout);
        var executed = false;
        await using var provider = CreateProvider(new ScheduledJobDescriptor {
            Name = "test.cluster",
            Schedule = ScheduledJobSchedule.FixedRate("1h"),
            InvokeAsync = (_, _, _, _) => {
                executed = true;
                return ValueTask.CompletedTask;
            }
        });
        var hostedService = provider.GetRequiredService<IHostedService>();
        var inspector = provider.GetRequiredService<IJobSchedulerInspector>();

        await hostedService.StartAsync(cts.Token);
        try {
            while (!inspector.GetSnapshot().RecentOutcomes.Any(outcome =>
                       outcome.JobName == "test.cluster" && outcome.Status == ScheduledJobRunStatus.Skipped)) {
                cts.Token.ThrowIfCancellationRequested();
                await Task.Delay(25, cts.Token);
            }
        }
        finally {
            await hostedService.StopAsync(CancellationToken.None);
        }

        executed.Should().BeFalse();
    }

    private static ServiceProvider CreateProvider(ScheduledJobDescriptor descriptor) {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton<TimeProvider>(new FakeTimeProvider());
        services.AddSingleton(descriptor);
        services.AddElarionScheduler();
        services.RemoveAll<IScheduledOccurrenceCoordinator>();
        services.AddSingleton<IScheduledOccurrenceCoordinator, DenyAllCoordinator>();
        return services.BuildServiceProvider();
    }

    private sealed class DenyAllCoordinator : IScheduledOccurrenceCoordinator {
        public ValueTask<bool> TryClaimAsync(ScheduledOccurrence occurrence, CancellationToken cancellationToken) {
            return ValueTask.FromResult(false);
        }
    }
}
