using AwesomeAssertions;
using Elarion.Messaging.Outbox;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Elarion.Tests.Messaging;

[Trait("Category", "Integration")]
public sealed class EfCoreOutboxStoreIntegrationTests(PostgreSqlOutboxStoreFixture fixture)
    : IClassFixture<PostgreSqlOutboxStoreFixture> {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<OutboxMessage> SeedAsync(
        OutboxIntegrationDbContext context,
        string? targetRole = null) {
        var id = Guid.CreateVersion7();
        var message = new OutboxMessage {
            Id = id,
            MessageId = id,
            OccurredOnUtc = DateTimeOffset.UtcNow,
            EventType = "Test.Event",
            Payload = "{}",
            CorrelationId = Guid.CreateVersion7(),
            TargetRole = targetRole
        };
        context.Add(message);
        await context.SaveChangesAsync(Ct);
        return message;
    }

    [Fact]
    public async Task MarkProcessed_WithWrongLockToken_IsNoOp() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var context = fixture.CreateContext();
        var store = new EfCoreOutboxStore<OutboxIntegrationDbContext>(context, new OutboxOptions(),
            TimeProvider.System);
        var delivery = await SeedAsync(context);

        var lockId = Guid.NewGuid();
        var claimed = await store.ClaimPendingAsync(lockId, DateTimeOffset.UtcNow.AddMinutes(2), 10, [], Ct);
        claimed.Should().ContainSingle().Which.Id.Should().Be(delivery.Id);

        (await store.MarkProcessedAsync(delivery.Id, Guid.NewGuid(), DateTimeOffset.UtcNow, Ct)).Should().BeFalse();
        var row = await context.Set<OutboxMessage>().AsNoTracking().SingleAsync(d => d.Id == delivery.Id, Ct);
        row.ProcessedOnUtc.Should().BeNull();
        row.LockId.Should().Be(lockId);
    }

    [Fact]
    public async Task StaleWorker_CannotWipe_NewOwnersLease() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var context = fixture.CreateContext();
        var store = new EfCoreOutboxStore<OutboxIntegrationDbContext>(context, new OutboxOptions(),
            TimeProvider.System);
        var delivery = await SeedAsync(context);

        var lockA = Guid.NewGuid();
        await store.ClaimPendingAsync(lockA, DateTimeOffset.UtcNow.AddSeconds(-1), 10, [], Ct);
        var lockB = Guid.NewGuid();
        (await store.ClaimPendingAsync(lockB, DateTimeOffset.UtcNow.AddMinutes(2), 10, [], Ct))
            .Should().ContainSingle().Which.Id.Should().Be(delivery.Id);

        (await store.MarkFailedAsync(delivery.Id, lockA, "stale", DateTimeOffset.UtcNow, Ct)).Should().BeFalse();
        (await store.MarkProcessedAsync(delivery.Id, lockA, DateTimeOffset.UtcNow, Ct)).Should().BeFalse();
        var row = await context.Set<OutboxMessage>().AsNoTracking().SingleAsync(d => d.Id == delivery.Id, Ct);
        row.LockId.Should().Be(lockB);

        (await store.MarkProcessedAsync(delivery.Id, lockB, DateTimeOffset.UtcNow, Ct)).Should().BeTrue();
    }

    [Fact]
    public async Task ReleaseClaim_WithCurrentLockToken_MakesDeliveryImmediatelyClaimable() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var context = fixture.CreateContext();
        var store = new EfCoreOutboxStore<OutboxIntegrationDbContext>(context, new OutboxOptions(),
            TimeProvider.System);
        var delivery = await SeedAsync(context, "actors");

        var firstLock = Guid.NewGuid();
        await store.ClaimPendingAsync(firstLock, DateTimeOffset.UtcNow.AddMinutes(2), 10, ["actors"], Ct);

        (await store.ReleaseClaimAsync(delivery.Id, firstLock, Ct)).Should().BeTrue();
        var row = await context.Set<OutboxMessage>().AsNoTracking().SingleAsync(d => d.Id == delivery.Id, Ct);
        row.LockId.Should().BeNull();
        row.LockedUntilUtc.Should().BeNull();
        row.Attempts.Should().Be(0);

        var secondLock = Guid.NewGuid();
        (await store.ClaimPendingAsync(secondLock, DateTimeOffset.UtcNow.AddMinutes(2), 10, ["actors"], Ct))
            .Should().ContainSingle().Which.Id.Should().Be(delivery.Id);
    }

    [Fact]
    public async Task MarkFailed_SetsVisibilityTimeout_ExcludingFromNextClaim() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var context = fixture.CreateContext();
        var store = new EfCoreOutboxStore<OutboxIntegrationDbContext>(context, new OutboxOptions(),
            TimeProvider.System);
        var delivery = await SeedAsync(context);

        var lockId = Guid.NewGuid();
        await store.ClaimPendingAsync(lockId, DateTimeOffset.UtcNow.AddMinutes(2), 10, [], Ct);
        var retryAfter = DateTimeOffset.UtcNow.AddMinutes(10);
        (await store.MarkFailedAsync(delivery.Id, lockId, "boom", retryAfter, Ct)).Should().BeTrue();
        (await store.ClaimPendingAsync(Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(2), 10, [], Ct))
            .Should().BeEmpty();

        var row = await context.Set<OutboxMessage>().AsNoTracking().SingleAsync(d => d.Id == delivery.Id, Ct);
        row.Attempts.Should().Be(1);
        row.LockId.Should().BeNull();
        row.LockedUntilUtc.Should().BeCloseTo(retryAfter, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task MarkPermanentlyFailed_MakesDeliveryUnclaimable() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var context = fixture.CreateContext();
        var options = new OutboxOptions { MaxDeliveryAttempts = 10 };
        var store = new EfCoreOutboxStore<OutboxIntegrationDbContext>(context, options, TimeProvider.System);
        var delivery = await SeedAsync(context);

        var lockId = Guid.NewGuid();
        await store.ClaimPendingAsync(lockId, DateTimeOffset.UtcNow.AddMinutes(2), 10, [], Ct);
        (await store.MarkPermanentlyFailedAsync(delivery.Id, lockId, "unresolvable", Ct)).Should().BeTrue();
        (await store.ClaimPendingAsync(Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(2), 10, [], Ct))
            .Should().BeEmpty();

        var row = await context.Set<OutboxMessage>().AsNoTracking().SingleAsync(d => d.Id == delivery.Id, Ct);
        row.Attempts.Should().Be(options.MaxDeliveryAttempts);
        row.Error.Should().Be("unresolvable");
    }

    [Fact]
    public async Task RoleBoundDelivery_IsClaimedOnlyByRoleHolder() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var context = fixture.CreateContext();
        var store = new EfCoreOutboxStore<OutboxIntegrationDbContext>(context, new OutboxOptions(),
            TimeProvider.System);
        var delivery = await SeedAsync(context, "actors:partition-3");

        (await store.ClaimPendingAsync(Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(2), 10, [], Ct))
            .Should().BeEmpty();
        (await store.ClaimPendingAsync(
                Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(2), 10, ["actors:partition-2"], Ct))
            .Should().BeEmpty();
        (await store.ClaimPendingAsync(
                Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(2), 10, ["actors:partition-3"], Ct))
            .Should().ContainSingle().Which.Id.Should().Be(delivery.Id);
    }

    [Fact]
    public async Task PurgeProcessed_DeletesOnlyOldCompletedTargetGroups() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var context = fixture.CreateContext();
        var cutoff = DateTimeOffset.UtcNow.AddHours(-1);
        var eligible = await SeedAsync(context);
        eligible.ProcessedOnUtc = cutoff.AddMinutes(-1);

        var retained = await SeedAsync(context);
        retained.ProcessedOnUtc = cutoff.AddMinutes(1);
        await context.SaveChangesAsync(Ct);
        var eligibleGroupId = eligible.Id;
        var retainedGroupId = retained.Id;
        var store = new EfCoreOutboxStore<OutboxIntegrationDbContext>(context, new OutboxOptions(),
            TimeProvider.System);

        (await store.PurgeProcessedAsync(cutoff, Ct)).Should().BeGreaterThanOrEqualTo(1);

        (await context.Set<OutboxMessage>().AnyAsync(message => message.Id == eligibleGroupId, Ct)).Should().BeFalse();
        (await context.Set<OutboxMessage>().AnyAsync(message => message.Id == retainedGroupId, Ct)).Should().BeTrue();
    }
}
