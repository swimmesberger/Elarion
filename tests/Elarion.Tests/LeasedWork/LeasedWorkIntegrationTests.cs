using AwesomeAssertions;
using Elarion.EntityFrameworkCore.LeasedWork;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Elarion.Tests.LeasedWork;

/// <summary>
/// The ADR-0073 leased-work-row invariants against real PostgreSQL, on a delivery table that is not the outbox. Time
/// is passed explicitly (<see cref="LeasedWorkClaim{TRow}.NowUtc"/>), so lease expiry and retry backoff are exact
/// without waiting.
/// </summary>
[Trait("Category", "Integration")]
public sealed class LeasedWorkIntegrationTests(PostgreSqlLeasedWorkFixture fixture)
    : IClassFixture<PostgreSqlLeasedWorkFixture> {
    private const int MaxAttempts = 3;
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan[] Ladder = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5)];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static LeasedWorkClaim<TestDelivery> Claim(string source, Guid lockId, DateTimeOffset now, int batchSize = 10) =>
        new() {
            LockId = lockId,
            NowUtc = now,
            LeaseUntilUtc = now + Lease,
            BatchSize = batchSize,
            Eligible = delivery => delivery.Source == source
                                   && delivery.CompletedAtUtc == null
                                   && delivery.Attempts < MaxAttempts
                                   && delivery.ExpiresAtUtc > now,
            OrderBy = deliveries => deliveries.OrderBy(delivery => delivery.CreatedAtUtc),
            OnClaim = setters => setters.SetProperty(delivery => delivery.Attempts, delivery => delivery.Attempts + 1)
        };

    private async Task<(string Source, List<Guid> Ids)> SeedAsync(int count, TimeSpan? expiresAfter = null) {
        var source = $"test-{Guid.CreateVersion7():N}";
        var ids = new List<Guid>();
        await using var context = fixture.CreateContext();
        for (var i = 0; i < count; i++) {
            var id = Guid.CreateVersion7();
            ids.Add(id);
            context.Deliveries.Add(new TestDelivery {
                Id = id,
                Source = source,
                CreatedAtUtc = T0.AddSeconds(i),
                ExpiresAtUtc = T0 + (expiresAfter ?? TimeSpan.FromDays(7))
            });
        }

        await context.SaveChangesAsync(Ct);
        return (source, ids);
    }

    private async Task<TestDelivery> ReadAsync(Guid id) {
        await using var context = fixture.CreateContext();
        return await context.Deliveries.AsNoTracking().SingleAsync(delivery => delivery.Id == id, Ct);
    }

    [Fact]
    public async Task Claim_ReturnsWonRowsInQueueOrder_StampsLease_AndCountsAttempt() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var (source, ids) = await SeedAsync(3);
        await using var context = fixture.CreateContext();
        var lockId = Guid.CreateVersion7();

        var claimed = await context.Deliveries.ClaimPendingAsync(Claim(source, lockId, T0), Ct);

        claimed.Select(delivery => delivery.Id).Should().Equal(ids);
        claimed.Should().AllSatisfy(delivery => {
            delivery.LockId.Should().Be(lockId);
            delivery.LockedUntilUtc.Should().Be(T0 + Lease);
            delivery.Attempts.Should().Be(1);
        });
    }

    [Fact]
    public async Task Claim_RespectsBatchSize_AndSkipsRowsUnderAnotherLease() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var (source, ids) = await SeedAsync(3);
        await using var context = fixture.CreateContext();

        var first = await context.Deliveries.ClaimPendingAsync(Claim(source, Guid.CreateVersion7(), T0, 2), Ct);
        var second = await context.Deliveries.ClaimPendingAsync(Claim(source, Guid.CreateVersion7(), T0, 2), Ct);
        var third = await context.Deliveries.ClaimPendingAsync(Claim(source, Guid.CreateVersion7(), T0, 2), Ct);

        first.Select(delivery => delivery.Id).Should().Equal(ids[0], ids[1]);
        second.Select(delivery => delivery.Id).Should().Equal(ids[2]);
        third.Should().BeEmpty();
    }

    [Fact]
    public async Task Claim_SkipsRowsTheConsumerFilterExcludes() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var (source, ids) = await SeedAsync(1, expiresAfter: TimeSpan.FromMinutes(10));
        await using var context = fixture.CreateContext();

        (await context.Deliveries.ClaimPendingAsync(Claim(source, Guid.CreateVersion7(), T0.AddMinutes(11)), Ct))
            .Should().BeEmpty();
        (await ReadAsync(ids[0])).LockId.Should().BeNull();
    }

    [Fact]
    public async Task ConcurrentWorkers_NeverClaimTheSameRow() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        const int rows = 60;
        const int workers = 6;
        var (source, ids) = await SeedAsync(rows);
        using var start = new SemaphoreSlim(0);

        var tasks = Enumerable.Range(0, workers).Select(_ => Task.Run(async () => {
            await using var context = fixture.CreateContext();
            var won = new List<(Guid Id, Guid LockId)>();
            await start.WaitAsync(Ct);
            while (true) {
                var lockId = Guid.CreateVersion7();
                var claimed = await context.Deliveries.ClaimPendingAsync(Claim(source, lockId, T0, 4), Ct);
                if (claimed.Count == 0) return won;
                won.AddRange(claimed.Select(delivery => (delivery.Id, lockId)));
            }
        }, Ct)).ToArray();
        start.Release(workers);
        var results = await Task.WhenAll(tasks);

        var all = results.SelectMany(result => result).ToList();
        all.Select(claim => claim.Id).Should().OnlyHaveUniqueItems().And.BeEquivalentTo(ids);

        await using var verify = fixture.CreateContext();
        var stored = await verify.Deliveries.AsNoTracking()
            .Where(delivery => delivery.Source == source)
            .ToDictionaryAsync(delivery => delivery.Id, Ct);
        all.Should().AllSatisfy(claim => {
            stored[claim.Id].LockId.Should().Be(claim.LockId);
            stored[claim.Id].Attempts.Should().Be(1);
        });
    }

    [Fact]
    public async Task ExpiredLease_IsReclaimed_AndTheStaleWorkersFinalizeIsRejected() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var (source, ids) = await SeedAsync(1);
        await using var context = fixture.CreateContext();
        var stale = Guid.CreateVersion7();
        var current = Guid.CreateVersion7();

        await context.Deliveries.ClaimPendingAsync(Claim(source, stale, T0), Ct);
        (await context.Deliveries.ClaimPendingAsync(Claim(source, current, T0 + Lease / 2), Ct)).Should().BeEmpty();
        var reclaimed = await context.Deliveries.ClaimPendingAsync(Claim(source, current, T0 + Lease + TimeSpan.FromSeconds(1)), Ct);
        reclaimed.Should().ContainSingle().Which.Attempts.Should().Be(2);

        (await context.Deliveries.FinalizeClaimAsync(ids[0], stale, MarkSent(T0), null, Ct)).Should().BeFalse();
        (await context.Deliveries.ReleaseClaimAsync(ids[0], stale, Ct)).Should().BeFalse();
        var row = await ReadAsync(ids[0]);
        row.State.Should().Be(TestDeliveryStates.Queued);
        row.LockId.Should().Be(current);

        (await context.Deliveries.FinalizeClaimAsync(ids[0], current, MarkSent(T0), null, Ct)).Should().BeTrue();
        row = await ReadAsync(ids[0]);
        row.State.Should().Be(TestDeliveryStates.Sent);
        row.CompletedAtUtc.Should().Be(T0);
        row.LockId.Should().BeNull();
        row.LockedUntilUtc.Should().BeNull();
        (await context.Deliveries.ClaimPendingAsync(Claim(source, Guid.CreateVersion7(), T0.AddDays(1)), Ct))
            .Should().BeEmpty();
    }

    [Fact]
    public async Task FailedAttempt_StaysInvisibleUntilItsBackoffElapses() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var (source, ids) = await SeedAsync(1);
        await using var context = fixture.CreateContext();
        var lockId = Guid.CreateVersion7();
        var claimed = await context.Deliveries.ClaimPendingAsync(Claim(source, lockId, T0), Ct);
        var retryAt = T0 + LeasedWorkBackoff.Ladder(claimed[0].Attempts, Ladder);

        (await context.Deliveries.FinalizeClaimAsync(
                ids[0],
                lockId,
                setters => setters.SetProperty(delivery => delivery.Error, "remote unavailable"),
                retryAt,
                Ct))
            .Should().BeTrue();

        var row = await ReadAsync(ids[0]);
        row.LockId.Should().BeNull();
        row.LockedUntilUtc.Should().Be(T0.AddMinutes(1));
        row.Error.Should().Be("remote unavailable");
        (await context.Deliveries.ClaimPendingAsync(Claim(source, Guid.CreateVersion7(), T0.AddSeconds(59)), Ct))
            .Should().BeEmpty();
        (await context.Deliveries.ClaimPendingAsync(Claim(source, Guid.CreateVersion7(), T0.AddSeconds(61)), Ct))
            .Should().ContainSingle().Which.Attempts.Should().Be(2);
    }

    [Fact]
    public async Task AttemptCountedAtClaim_StopsARowThatCrashesEveryWorker() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var (source, ids) = await SeedAsync(1);
        await using var context = fixture.CreateContext();

        // Each worker claims and dies without finalizing; only lease expiry hands the row on.
        var now = T0;
        for (var attempt = 1; attempt <= MaxAttempts; attempt++) {
            (await context.Deliveries.ClaimPendingAsync(Claim(source, Guid.CreateVersion7(), now), Ct))
                .Should().ContainSingle().Which.Attempts.Should().Be(attempt);
            now += Lease + TimeSpan.FromSeconds(1);
        }

        (await context.Deliveries.ClaimPendingAsync(Claim(source, Guid.CreateVersion7(), now), Ct)).Should().BeEmpty();
        (await ReadAsync(ids[0])).Attempts.Should().Be(MaxAttempts);
    }

    [Fact]
    public async Task RenewClaim_ExtendsOnlyTheHoldersLease() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var (source, ids) = await SeedAsync(1);
        await using var context = fixture.CreateContext();
        var holder = Guid.CreateVersion7();
        await context.Deliveries.ClaimPendingAsync(Claim(source, holder, T0), Ct);

        (await context.Deliveries.RenewClaimAsync(ids[0], holder, T0.AddMinutes(10), Ct)).Should().BeTrue();
        (await context.Deliveries.RenewClaimAsync(ids[0], Guid.CreateVersion7(), T0.AddHours(1), Ct)).Should().BeFalse();

        (await context.Deliveries.ClaimPendingAsync(Claim(source, Guid.CreateVersion7(), T0 + Lease + TimeSpan.FromSeconds(1)), Ct))
            .Should().BeEmpty();
        var row = await ReadAsync(ids[0]);
        row.LockId.Should().Be(holder);
        row.LockedUntilUtc.Should().Be(T0.AddMinutes(10));
    }

    [Fact]
    public async Task ReleaseClaim_MakesTheRowImmediatelyClaimable() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var (source, ids) = await SeedAsync(1);
        await using var context = fixture.CreateContext();
        var lockId = Guid.CreateVersion7();
        await context.Deliveries.ClaimPendingAsync(Claim(source, lockId, T0), Ct);

        (await context.Deliveries.ReleaseClaimAsync(ids[0], lockId, Ct)).Should().BeTrue();

        (await context.Deliveries.ClaimPendingAsync(Claim(source, Guid.CreateVersion7(), T0), Ct))
            .Should().ContainSingle().Which.Id.Should().Be(ids[0]);
    }

    [Fact]
    public async Task ProducerWithdraw_IsNotBlockedByAnInFlightClaim_AndARetryFinalizeKeepsItWithdrawn() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var (source, ids) = await SeedAsync(1);
        await using var worker = fixture.CreateContext();
        var lockId = Guid.CreateVersion7();
        await worker.Deliveries.ClaimPendingAsync(Claim(source, lockId, T0), Ct);

        // No row lock or transaction is held between claim and finalize, so a producer's update goes straight
        // through while the worker is still busy with the external call.
        await using (var producer = fixture.CreateContext()) {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var withdrawn = await producer.Deliveries
                .Where(delivery => delivery.Id == ids[0] && delivery.State == TestDeliveryStates.Queued)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(delivery => delivery.State, TestDeliveryStates.Withdrawn)
                        .SetProperty(delivery => delivery.CompletedAtUtc, T0),
                    timeout.Token);
            withdrawn.Should().Be(1);
        }

        (await worker.Deliveries.FinalizeClaimAsync(
                ids[0],
                lockId,
                setters => setters.SetProperty(delivery => delivery.Error, "remote unavailable"),
                T0.AddMinutes(1),
                Ct))
            .Should().BeTrue();
        (await worker.Deliveries.ClaimPendingAsync(Claim(source, Guid.CreateVersion7(), T0.AddHours(1)), Ct))
            .Should().BeEmpty();
        (await ReadAsync(ids[0])).State.Should().Be(TestDeliveryStates.Withdrawn);
    }

    [Fact]
    public async Task ExhaustedRows_AreGivenUpAtClaim_NotReturned_AndReported() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var (source, ids) = await SeedAsync(2);
        await using (var seed = fixture.CreateContext()) {
            // The first row's previous workers all died mid-call: this claim counts its last allowed attempt + 1.
            await seed.Deliveries.Where(delivery => delivery.Id == ids[0])
                .ExecuteUpdateAsync(setters => setters.SetProperty(delivery => delivery.Attempts, MaxAttempts), Ct);
        }

        await using var context = fixture.CreateContext();
        var reported = new List<TestDelivery>();
        var claim = Claim(source, Guid.CreateVersion7(), T0) with {
            Eligible = delivery => delivery.Source == source && delivery.CompletedAtUtc == null,
            Exhausted = delivery => delivery.Attempts > MaxAttempts,
            GiveUp = setters => setters
                .SetProperty(delivery => delivery.State, TestDeliveryStates.Withdrawn)
                .SetProperty(delivery => delivery.CompletedAtUtc, T0)
                .SetProperty(delivery => delivery.Error, "attempts exhausted"),
            OnGivenUp = (delivery, _) => {
                reported.Add(delivery);
                return ValueTask.CompletedTask;
            }
        };

        var claimed = await context.Deliveries.ClaimPendingAsync(claim, Ct);

        claimed.Select(delivery => delivery.Id).Should().Equal(ids[1]);
        reported.Should().ContainSingle().Which.Attempts.Should().Be(MaxAttempts + 1);
        var givenUp = await ReadAsync(ids[0]);
        givenUp.State.Should().Be(TestDeliveryStates.Withdrawn);
        givenUp.Error.Should().Be("attempts exhausted");
        givenUp.LockId.Should().BeNull();
        givenUp.LockedUntilUtc.Should().BeNull();
    }

    [Fact]
    public async Task Exhausted_WithoutGiveUp_IsRejected() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var context = fixture.CreateContext();
        var claim = Claim("none", Guid.CreateVersion7(), T0) with { Exhausted = delivery => delivery.Attempts > 3 };

        var act = async () => await context.Deliveries.ClaimPendingAsync(claim, Ct);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*GiveUp*");
    }

    [Fact]
    public async Task ProjectedClaim_ReturnsTheProjectionOfTheWonRowsInQueueOrder() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var (source, ids) = await SeedAsync(3);
        await using var context = fixture.CreateContext();

        var claimed = await context.Deliveries.ClaimPendingAsync(
            Claim(source, Guid.CreateVersion7(), T0, batchSize: 2),
            delivery => new { delivery.Id, delivery.Attempts, Label = delivery.Source + "/" + delivery.State },
            Ct);

        claimed.Select(row => row.Id).Should().Equal(ids[0], ids[1]);
        claimed.Should().AllSatisfy(row => {
            row.Attempts.Should().Be(1);
            row.Label.Should().Be($"{source}/{TestDeliveryStates.Queued}");
        });
    }

    [Fact]
    public async Task GuardedFinalize_AppliesOnlyWhileTheRowIsStillEligible() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var (source, ids) = await SeedAsync(2);
        await using var context = fixture.CreateContext();
        var lockId = Guid.CreateVersion7();
        await context.Deliveries.ClaimPendingAsync(Claim(source, lockId, T0), Ct);
        // A producer withdraws the second row while the worker holds no lock on it.
        await context.Deliveries.Where(delivery => delivery.Id == ids[1])
            .ExecuteUpdateAsync(setters => setters.SetProperty(delivery => delivery.State, TestDeliveryStates.Withdrawn), Ct);
        var finalize = new LeasedWorkFinalize<TestDelivery> {
            Update = MarkSent(T0),
            StillEligible = delivery => delivery.State == TestDeliveryStates.Queued
        };

        (await context.Deliveries.FinalizeClaimAsync(ids[0], lockId, finalize, Ct))
            .Should().Be(LeasedWorkFinalizeOutcome.Finalized);
        (await context.Deliveries.FinalizeClaimAsync(ids[1], lockId, finalize, Ct))
            .Should().Be(LeasedWorkFinalizeOutcome.Ineligible);
        (await context.Deliveries.FinalizeClaimAsync(ids[1], lockId, finalize, Ct))
            .Should().Be(LeasedWorkFinalizeOutcome.ClaimLost);

        (await ReadAsync(ids[0])).State.Should().Be(TestDeliveryStates.Sent);
        var withdrawn = await ReadAsync(ids[1]);
        withdrawn.State.Should().Be(TestDeliveryStates.Withdrawn);
        withdrawn.CompletedAtUtc.Should().BeNull();
        withdrawn.LockId.Should().BeNull();
    }

    [Fact]
    public async Task ReleaseClaims_GivesABatchBack_UndoingTheClaimsBookkeeping_AndLeavesLostClaimsAlone() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var (source, ids) = await SeedAsync(3);
        await using var context = fixture.CreateContext();
        var lockId = Guid.CreateVersion7();
        await context.Deliveries.ClaimPendingAsync(Claim(source, lockId, T0), Ct);
        (await context.Deliveries.FinalizeClaimAsync(ids[0], lockId, MarkSent(T0), null, Ct)).Should().BeTrue();

        // Shutdown mid-batch: hand the unfinished rows back without burning the attempt the claim counted.
        var released = await context.Deliveries.ReleaseClaimsAsync(
            ids, lockId, setters => setters.SetProperty(delivery => delivery.Attempts, delivery => delivery.Attempts - 1),
            Ct);

        released.Should().Be(2);
        (await ReadAsync(ids[0])).Attempts.Should().Be(1, "the finalized row is no longer this worker's");
        foreach (var id in ids.Skip(1)) {
            var row = await ReadAsync(id);
            row.Attempts.Should().Be(0);
            row.LockId.Should().BeNull();
        }

        (await context.Deliveries.ClaimPendingAsync(Claim(source, Guid.CreateVersion7(), T0), Ct))
            .Select(delivery => delivery.Id).Should().Equal(ids[1], ids[2]);
    }

    [Fact]
    public async Task Exhausted_WithoutOnGivenUp_GivesUpWithoutReporting() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var (source, ids) = await SeedAsync(2);
        await SetAttemptsAsync(ids[0], MaxAttempts);
        await using var context = fixture.CreateContext();

        var claimed = await context.Deliveries.ClaimPendingAsync(ExhaustingClaim(source, onGivenUp: null), Ct);

        claimed.Select(delivery => delivery.Id).Should().Equal(ids[1]);
        var givenUp = await ReadAsync(ids[0]);
        givenUp.State.Should().Be(TestDeliveryStates.Withdrawn);
        givenUp.LockId.Should().BeNull();
    }

    [Fact]
    public async Task GiveUp_RacingAWriterThatResetsTheRow_ReportsOnlyWhatItGaveUp_AndReturnsTheRest() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var (source, ids) = await SeedAsync(2);
        await SetAttemptsAsync(ids[0], MaxAttempts);
        await SetAttemptsAsync(ids[1], MaxAttempts);
        // Right before the give-up statement, another writer resets the first row's attempts: it is no longer exhausted.
        var race = new BeforeGiveUp(() => SetAttemptsAsync(ids[0], 0));
        await using var context = fixture.CreateContext(race);
        var reported = new List<Guid>();

        var claimed = await context.Deliveries.ClaimPendingAsync(
            ExhaustingClaim(source, (delivery, _) => {
                reported.Add(delivery.Id);
                return ValueTask.CompletedTask;
            }),
            Ct);

        race.Ran.Should().BeTrue();
        reported.Should().Equal(ids[1]);
        claimed.Select(delivery => delivery.Id).Should().Equal(ids[0]);
        (await ReadAsync(ids[0])).State.Should().Be(TestDeliveryStates.Queued);
        (await ReadAsync(ids[1])).State.Should().Be(TestDeliveryStates.Withdrawn);
    }

    [Fact]
    public async Task ThrowingOnGivenUp_ReportsEveryRow_ReleasesTheWonRows_AndThrows() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var (source, ids) = await SeedAsync(3);
        await SetAttemptsAsync(ids[0], MaxAttempts);
        await SetAttemptsAsync(ids[1], MaxAttempts);
        await using var context = fixture.CreateContext();
        var calls = 0;

        var act = async () => await context.Deliveries.ClaimPendingAsync(
            ExhaustingClaim(source, (_, _) => {
                calls++;
                throw new InvalidOperationException("log sink down");
            }),
            Ct);

        var thrown = await act.Should().ThrowAsync<AggregateException>();
        thrown.Which.InnerExceptions.Should().HaveCount(2);
        calls.Should().Be(2);
        var won = await ReadAsync(ids[2]);
        won.LockId.Should().BeNull("the won row is released, not stranded until its lease lapses");
        won.Attempts.Should().Be(1);
    }

    [Fact]
    public async Task GuardedFinalize_HonorsVisibleAfterWhenFinalized_ButReleasesAnIneligibleRowAtOnce() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var (source, ids) = await SeedAsync(2);
        await using var context = fixture.CreateContext();
        var lockId = Guid.CreateVersion7();
        await context.Deliveries.ClaimPendingAsync(Claim(source, lockId, T0), Ct);
        await context.Deliveries.Where(delivery => delivery.Id == ids[1])
            .ExecuteUpdateAsync(setters => setters.SetProperty(delivery => delivery.State, TestDeliveryStates.Withdrawn), Ct);
        var retryLater = new LeasedWorkFinalize<TestDelivery> {
            Update = setters => setters.SetProperty(delivery => delivery.Error, "timeout"),
            VisibleAfterUtc = T0 + Ladder[1],
            StillEligible = delivery => delivery.State == TestDeliveryStates.Queued
        };

        (await context.Deliveries.FinalizeClaimAsync(ids[0], lockId, retryLater, Ct))
            .Should().Be(LeasedWorkFinalizeOutcome.Finalized);
        (await context.Deliveries.FinalizeClaimAsync(ids[1], lockId, retryLater, Ct))
            .Should().Be(LeasedWorkFinalizeOutcome.Ineligible);

        (await ReadAsync(ids[0])).LockedUntilUtc.Should().Be(T0 + Ladder[1]);
        var ineligible = await ReadAsync(ids[1]);
        ineligible.LockedUntilUtc.Should().BeNull();
        ineligible.Error.Should().BeNull();
    }

    [Fact]
    public async Task ProjectedClaim_ReadsANavigationInTheClaimsQuery() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var (source, ids) = await SeedAsync(2);
        var endpointId = Guid.CreateVersion7();
        await using (var seed = fixture.CreateContext()) {
            seed.Endpoints.Add(new TestEndpoint { Id = endpointId, Url = "https://example.test/hook" });
            await seed.SaveChangesAsync(Ct);
            await seed.Deliveries.Where(delivery => delivery.Id == ids[0])
                .ExecuteUpdateAsync(setters => setters.SetProperty(delivery => delivery.EndpointId, endpointId), Ct);
        }

        await using var context = fixture.CreateContext();
        var claimed = await context.Deliveries.ClaimPendingAsync(
            Claim(source, Guid.CreateVersion7(), T0),
            delivery => new { delivery.Id, Url = delivery.Endpoint == null ? null : delivery.Endpoint.Url },
            Ct);

        claimed.Select(row => (row.Id, row.Url)).Should().Equal((ids[0], "https://example.test/hook"), (ids[1], null));
    }

    private LeasedWorkClaim<TestDelivery> ExhaustingClaim(
        string source, Func<TestDelivery, CancellationToken, ValueTask>? onGivenUp) =>
        Claim(source, Guid.CreateVersion7(), T0) with {
            Eligible = delivery => delivery.Source == source && delivery.CompletedAtUtc == null,
            Exhausted = delivery => delivery.Attempts > MaxAttempts,
            GiveUp = setters => setters
                .SetProperty(delivery => delivery.State, TestDeliveryStates.Withdrawn)
                .SetProperty(delivery => delivery.CompletedAtUtc, T0),
            OnGivenUp = onGivenUp
        };

    private async Task SetAttemptsAsync(Guid id, int attempts) {
        await using var context = fixture.CreateContext();
        await context.Deliveries.Where(delivery => delivery.Id == id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(delivery => delivery.Attempts, attempts), Ct);
    }

    // Runs a concurrent write once, right before the claim's give-up statement (the UPDATE that sets the state).
    private sealed class BeforeGiveUp(Func<Task> write) : DbCommandInterceptor {
        public bool Ran { get; private set; }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default) {
            if (!Ran && command.CommandText.StartsWith("UPDATE", StringComparison.Ordinal) &&
                command.CommandText.Contains("\"State\"", StringComparison.Ordinal)) {
                Ran = true;
                await write();
            }

            return result;
        }
    }

    private static Action<Microsoft.EntityFrameworkCore.Query.UpdateSettersBuilder<TestDelivery>> MarkSent(
        DateTimeOffset now) =>
        setters => setters
            .SetProperty(delivery => delivery.State, TestDeliveryStates.Sent)
            .SetProperty(delivery => delivery.CompletedAtUtc, now);
}
