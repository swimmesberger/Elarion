using AwesomeAssertions;
using Elarion.Messaging.Outbox;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Elarion.Tests.Messaging;

/// <summary>
/// End-to-end integration tests for the durable EF Core outbox store against PostgreSQL, proving the lease-guarded
/// finalize (the stale-lease race fix), the failure backoff visibility timeout, and permanent parking.
/// </summary>
[Trait("Category", "Integration")]
public sealed class EfCoreOutboxStoreIntegrationTests(PostgreSqlOutboxStoreFixture fixture)
    : IClassFixture<PostgreSqlOutboxStoreFixture> {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<OutboxMessage> SeedAsync(OutboxIntegrationDbContext context) {
        var message = new OutboxMessage {
            Id = Guid.NewGuid(),
            OccurredOnUtc = DateTimeOffset.UtcNow,
            EventType = "Test.Event",
            Payload = "{}",
            CorrelationId = Guid.NewGuid()
        };
        context.Add(message);
        await context.SaveChangesAsync(Ct);
        return message;
    }

    [Fact]
    public async Task MarkProcessed_WithWrongLockToken_IsNoOp() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var context = fixture.CreateContext();
        var store = new EfCoreOutboxStore<OutboxIntegrationDbContext>(context, new OutboxOptions(), TimeProvider.System);
        var message = await SeedAsync(context);

        var lockId = Guid.NewGuid();
        var claimed = await store.ClaimPendingAsync(lockId, DateTimeOffset.UtcNow.AddMinutes(2), 10, Ct);
        claimed.Should().ContainSingle().Which.Id.Should().Be(message.Id);

        var wrongLock = await store.MarkProcessedAsync(message.Id, Guid.NewGuid(), DateTimeOffset.UtcNow, Ct);
        wrongLock.Should().BeFalse();

        var row = await context.Set<OutboxMessage>().AsNoTracking().SingleAsync(m => m.Id == message.Id, Ct);
        row.ProcessedOnUtc.Should().BeNull();
        row.LockId.Should().Be(lockId);
    }

    [Fact]
    public async Task StaleWorker_CannotWipe_NewOwnersLease() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var context = fixture.CreateContext();
        var options = new OutboxOptions();
        var store = new EfCoreOutboxStore<OutboxIntegrationDbContext>(context, options, TimeProvider.System);
        var message = await SeedAsync(context);

        // Node A claims with a lease already in the past, then stalls.
        var lockA = Guid.NewGuid();
        await store.ClaimPendingAsync(lockA, DateTimeOffset.UtcNow.AddSeconds(-1), 10, Ct);

        // Node B legitimately reclaims the expired lease and holds an active lease.
        var lockB = Guid.NewGuid();
        var claimedByB = await store.ClaimPendingAsync(lockB, DateTimeOffset.UtcNow.AddMinutes(2), 10, Ct);
        claimedByB.Should().ContainSingle().Which.Id.Should().Be(message.Id);

        // Node A returns and tries to finalize under its stale lease: must not touch B's active lease.
        var aFailed = await store.MarkFailedAsync(message.Id, lockA, "stale", DateTimeOffset.UtcNow.AddMinutes(5), Ct);
        aFailed.Should().BeFalse();
        var aProcessed = await store.MarkProcessedAsync(message.Id, lockA, DateTimeOffset.UtcNow, Ct);
        aProcessed.Should().BeFalse();

        var row = await context.Set<OutboxMessage>().AsNoTracking().SingleAsync(m => m.Id == message.Id, Ct);
        row.LockId.Should().Be(lockB);
        row.ProcessedOnUtc.Should().BeNull();

        // B finalizes cleanly.
        (await store.MarkProcessedAsync(message.Id, lockB, DateTimeOffset.UtcNow, Ct)).Should().BeTrue();
    }

    [Fact]
    public async Task MarkFailed_SetsVisibilityTimeout_ExcludingFromNextClaim() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var context = fixture.CreateContext();
        var store = new EfCoreOutboxStore<OutboxIntegrationDbContext>(context, new OutboxOptions(), TimeProvider.System);
        var message = await SeedAsync(context);

        var lockId = Guid.NewGuid();
        await store.ClaimPendingAsync(lockId, DateTimeOffset.UtcNow.AddMinutes(2), 10, Ct);
        var retryAfter = DateTimeOffset.UtcNow.AddMinutes(10);
        (await store.MarkFailedAsync(message.Id, lockId, "boom", retryAfter, Ct)).Should().BeTrue();

        // A poll now must not re-claim the failed row: its visibility timeout is in the future.
        var reclaimed = await store.ClaimPendingAsync(Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(2), 10, Ct);
        reclaimed.Should().BeEmpty();

        var row = await context.Set<OutboxMessage>().AsNoTracking().SingleAsync(m => m.Id == message.Id, Ct);
        row.Attempts.Should().Be(1);
        row.LockId.Should().BeNull();
        row.LockedUntilUtc.Should().BeCloseTo(retryAfter, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task MarkPermanentlyFailed_MakesRowUnclaimable() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var context = fixture.CreateContext();
        var options = new OutboxOptions { MaxDeliveryAttempts = 10 };
        var store = new EfCoreOutboxStore<OutboxIntegrationDbContext>(context, options, TimeProvider.System);
        var message = await SeedAsync(context);

        var lockId = Guid.NewGuid();
        await store.ClaimPendingAsync(lockId, DateTimeOffset.UtcNow.AddMinutes(2), 10, Ct);
        (await store.MarkPermanentlyFailedAsync(message.Id, lockId, "unresolvable", Ct)).Should().BeTrue();

        var reclaimed = await store.ClaimPendingAsync(Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(2), 10, Ct);
        reclaimed.Should().BeEmpty();

        var row = await context.Set<OutboxMessage>().AsNoTracking().SingleAsync(m => m.Id == message.Id, Ct);
        row.ProcessedOnUtc.Should().BeNull();
        row.Attempts.Should().Be(options.MaxDeliveryAttempts);
        row.Error.Should().Be("unresolvable");
    }
}
