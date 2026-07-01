using System.Collections.Concurrent;
using AwesomeAssertions;
using Elarion.Abstractions.Scheduling;
using Elarion.Scheduling;
using Elarion.Scheduling.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

namespace Elarion.Tests.Services;

/// <summary>
/// Cross-instance scheduler coordination tests against a real PostgreSQL instance: the claim semantics of
/// <see cref="EfCoreScheduledOccurrenceCoordinator{TDbContext}"/> and, end to end, two independent scheduler
/// "nodes" sharing one claims table executing a recurring job exactly once per occurrence (ADR-0025).
/// Skips when Docker is unavailable.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SchedulerClaimsIntegrationTests(PostgreSqlSchedulerClaimsFixture fixture)
    : IClassFixture<PostgreSqlSchedulerClaimsFixture> {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string UniqueJob() => $"job-{Guid.NewGuid():N}";

    [Fact]
    public async Task ExactClaim_SameOccurrence_WinsExactlyOnceAcrossNodes() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        using var nodeA = fixture.CreateNode();
        using var nodeB = fixture.CreateNode();
        var occurrence = new ScheduledOccurrence {
            JobName = UniqueJob(),
            DueTimeUtc = DateTimeOffset.UtcNow,
        };

        var results = await Task.WhenAll(
            nodeA.Coordinator.TryClaimAsync(occurrence, Ct).AsTask(),
            nodeB.Coordinator.TryClaimAsync(occurrence, Ct).AsTask());

        results.Count(claimed => claimed).Should().Be(1);
    }

    [Fact]
    public async Task ExactClaim_DifferentOccurrences_BothWin() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        using var node = fixture.CreateNode();
        var jobName = UniqueJob();
        var first = DateTimeOffset.UtcNow;

        (await node.Coordinator.TryClaimAsync(
            new ScheduledOccurrence { JobName = jobName, DueTimeUtc = first }, Ct)).Should().BeTrue();
        (await node.Coordinator.TryClaimAsync(
            new ScheduledOccurrence { JobName = jobName, DueTimeUtc = first.AddSeconds(1) }, Ct)).Should().BeTrue();
    }

    [Fact]
    public async Task WindowClaim_SecondClaimWithinWindow_IsRejected() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        using var nodeA = fixture.CreateNode();
        using var nodeB = fixture.CreateNode();
        var jobName = UniqueJob();
        var window = TimeSpan.FromMinutes(5);
        var due = DateTimeOffset.UtcNow;

        (await nodeA.Coordinator.TryClaimAsync(
            new ScheduledOccurrence { JobName = jobName, DueTimeUtc = due, DedupeWindow = window }, Ct))
            .Should().BeTrue();

        // A different node fires "the same" interval occurrence a moment later (node-anchored grids never
        // align exactly) — the window suppresses it.
        (await nodeB.Coordinator.TryClaimAsync(
            new ScheduledOccurrence { JobName = jobName, DueTimeUtc = due.AddMilliseconds(250), DedupeWindow = window }, Ct))
            .Should().BeFalse();

        // The next interval slot is outside the window and claims normally.
        (await nodeB.Coordinator.TryClaimAsync(
            new ScheduledOccurrence { JobName = jobName, DueTimeUtc = due + window + TimeSpan.FromMilliseconds(1), DedupeWindow = window }, Ct))
            .Should().BeTrue();
    }

    [Fact]
    public async Task WindowClaim_ConcurrentRacers_WinExactlyOnce() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        using var nodeA = fixture.CreateNode();
        using var nodeB = fixture.CreateNode();
        var jobName = UniqueJob();
        var window = TimeSpan.FromMinutes(5);
        var due = DateTimeOffset.UtcNow;

        // Different due instants inside one window, claimed concurrently: the per-job advisory lock
        // serializes them, so a bare NOT EXISTS race cannot admit both.
        var results = await Task.WhenAll(
            nodeA.Coordinator.TryClaimAsync(
                new ScheduledOccurrence { JobName = jobName, DueTimeUtc = due, DedupeWindow = window }, Ct).AsTask(),
            nodeB.Coordinator.TryClaimAsync(
                new ScheduledOccurrence { JobName = jobName, DueTimeUtc = due.AddMilliseconds(50), DedupeWindow = window }, Ct).AsTask());

        results.Count(claimed => claimed).Should().Be(1);
    }

    [Fact]
    public async Task TwoSchedulerNodes_RecurringCronJob_ExecutesEachOccurrenceExactlyOnce() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var jobName = UniqueJob();
        var executions = new ConcurrentDictionary<DateTimeOffset, int>();

        using var nodeA = fixture.CreateSchedulerNode(jobName, executions);
        using var nodeB = fixture.CreateSchedulerNode(jobName, executions);

        await nodeA.Scheduler.StartAsync(Ct);
        await nodeB.Scheduler.StartAsync(Ct);
        try {
            // A one-second cron produces wall-clock-deterministic occurrences on both nodes; give it a few.
            await Task.Delay(TimeSpan.FromSeconds(4), Ct);
        }
        finally {
            await nodeA.Scheduler.StopAsync(CancellationToken.None);
            await nodeB.Scheduler.StopAsync(CancellationToken.None);
        }

        executions.Should().NotBeEmpty();
        executions.Count.Should().BeGreaterThanOrEqualTo(2);
        // The claim table is the fence: no occurrence may have executed on both nodes.
        executions.Where(pair => pair.Value != 1).Should().BeEmpty();
    }
}

/// <summary>
/// Starts a disposable PostgreSQL container for the scheduler-claims integration tests, creates the claims
/// schema once, and builds coordinator/scheduler "nodes" — each an isolated service provider over the shared
/// database, the multi-node topology. Skips (never fails) when Docker is unavailable.
/// </summary>
public sealed class PostgreSqlSchedulerClaimsFixture : IAsyncLifetime {
    private PostgreSqlContainer? _container;

    public bool IsAvailable { get; private set; }

    public string SkipReason { get; private set; } = "";

    private string ConnectionString { get; set; } = "";

    public async ValueTask InitializeAsync() {
        PostgreSqlContainer container;
        try {
            // Build() validates the Docker endpoint, so it must run inside the guard too.
            container = new PostgreSqlBuilder("postgres:17-alpine").Build();
            await container.StartAsync();
        }
        catch (Exception ex) {
            SkipReason = $"PostgreSQL Testcontainer unavailable (Docker required): {ex.Message}";
            return;
        }

        _container = container;
        ConnectionString = container.GetConnectionString();
        await using (var provider = BuildNodeProvider()) {
            await using var scope = provider.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<SchedulerClaimsDbContext>().Database.EnsureCreatedAsync();
        }

        IsAvailable = true;
    }

    public async ValueTask DisposeAsync() {
        if (_container is not null) {
            await _container.DisposeAsync();
        }
    }

    /// <summary>Creates one coordinator "node": an isolated container-backed service provider.</summary>
    public CoordinatorNode CreateNode() {
        var provider = BuildNodeProvider();
        return new CoordinatorNode(
            provider,
            new EfCoreScheduledOccurrenceCoordinator<SchedulerClaimsDbContext>(
                provider.GetRequiredService<IServiceScopeFactory>(),
                TimeProvider.System));
    }

    /// <summary>
    /// Creates one full scheduler "node": its own <see cref="InMemoryScheduler"/> with the EF coordinator,
    /// registered with a one-second cron job that counts each executed occurrence into
    /// <paramref name="executions"/>.
    /// </summary>
    public SchedulerNode CreateSchedulerNode(string jobName, ConcurrentDictionary<DateTimeOffset, int> executions) {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddDbContext<SchedulerClaimsDbContext>(options => options.UseNpgsql(ConnectionString));
        services.AddSingleton(new ScheduledJobDescriptor {
            Name = jobName,
            Schedule = ScheduledJobSchedule.Cron("* * * * * *"),
            InvokeAsync = (_, _, context, _) => {
                executions.AddOrUpdate(context.DueTimeUtc, 1, static (_, count) => count + 1);
                return ValueTask.CompletedTask;
            },
        });
        services.AddElarionScheduler();
        services.AddElarionSchedulerEntityFrameworkCore<SchedulerClaimsDbContext>();

        var provider = services.BuildServiceProvider();
        return new SchedulerNode(provider, provider.GetRequiredService<InMemoryScheduler>());
    }

    private ServiceProvider BuildNodeProvider() {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<SchedulerClaimsDbContext>(options => options.UseNpgsql(ConnectionString));
        return services.BuildServiceProvider();
    }

    public sealed record CoordinatorNode(
        ServiceProvider Provider,
        EfCoreScheduledOccurrenceCoordinator<SchedulerClaimsDbContext> Coordinator) : IDisposable {
        public void Dispose() => Provider.Dispose();
    }

    public sealed record SchedulerNode(ServiceProvider Provider, InMemoryScheduler Scheduler) : IDisposable {
        public void Dispose() => Provider.Dispose();
    }
}
