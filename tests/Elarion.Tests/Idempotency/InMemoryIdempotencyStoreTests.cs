using AwesomeAssertions;
using Elarion.Abstractions.Idempotency;
using Elarion.Idempotency;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Elarion.Tests.Idempotency;

public sealed class InMemoryIdempotencyStoreTests {
    private static readonly IdempotencyStoreKey Key = new(IdempotencyScope.Global, string.Empty, "k1");

    private static (IIdempotencyStore Store, FakeTimeProvider Time) CreateStore() {
        var time = new FakeTimeProvider();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(time);
        services.AddElarionIdempotency();
        var provider = services.BuildServiceProvider();
        return (provider.GetRequiredService<IIdempotencyStore>(), time);
    }

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Begin_Complete_Replay() {
        var (store, _) = CreateStore();

        var first = await store.TryBeginAsync(Key, "fp", IdempotencyConflictBehavior.Conflict, Ct);
        first.Status.Should().Be(IdempotencyBeginStatus.Began);

        await store.CompleteAsync(Key, "payload", isFailure: false, TimeSpan.FromHours(1), Ct);

        var replay = await store.TryBeginAsync(Key, "fp", IdempotencyConflictBehavior.Conflict, Ct);
        replay.Status.Should().Be(IdempotencyBeginStatus.Replay);
        replay.Payload.Should().Be("payload");
        replay.IsFailurePayload.Should().BeFalse();
    }

    [Fact]
    public async Task Concurrent_InFlight_Conflict_ReturnsInProgress() {
        var (store, _) = CreateStore();

        (await store.TryBeginAsync(Key, "fp", IdempotencyConflictBehavior.Conflict, Ct)).Status
            .Should().Be(IdempotencyBeginStatus.Began);

        // A second claim while the first is still pending (never completed).
        (await store.TryBeginAsync(Key, "fp", IdempotencyConflictBehavior.Conflict, Ct)).Status
            .Should().Be(IdempotencyBeginStatus.InProgress);
    }

    [Fact]
    public async Task Abandon_MakesKeyClaimableAgain() {
        var (store, _) = CreateStore();

        (await store.TryBeginAsync(Key, "fp", IdempotencyConflictBehavior.Conflict, Ct)).Status
            .Should().Be(IdempotencyBeginStatus.Began);

        await store.AbandonAsync(Key, Ct);

        (await store.TryBeginAsync(Key, "fp", IdempotencyConflictBehavior.Conflict, Ct)).Status
            .Should().Be(IdempotencyBeginStatus.Began);
    }

    [Fact]
    public async Task FingerprintMismatch_WhenKeyReusedWithDifferentRequest() {
        var (store, _) = CreateStore();

        await store.TryBeginAsync(Key, "fp-a", IdempotencyConflictBehavior.Conflict, Ct);
        await store.CompleteAsync(Key, "payload", isFailure: false, TimeSpan.FromHours(1), Ct);

        (await store.TryBeginAsync(Key, "fp-b", IdempotencyConflictBehavior.Conflict, Ct)).Status
            .Should().Be(IdempotencyBeginStatus.FingerprintMismatch);
    }

    [Fact]
    public async Task WaitThenReplay_BlocksUntilWinnerCompletes() {
        var (store, _) = CreateStore();

        (await store.TryBeginAsync(Key, "fp", IdempotencyConflictBehavior.WaitThenReplay, Ct)).Status
            .Should().Be(IdempotencyBeginStatus.Began);

        var waiter = store.TryBeginAsync(Key, "fp", IdempotencyConflictBehavior.WaitThenReplay, Ct).AsTask();
        waiter.IsCompleted.Should().BeFalse();

        await store.CompleteAsync(Key, "winner", isFailure: false, TimeSpan.FromHours(1), Ct);

        var replay = await waiter;
        replay.Status.Should().Be(IdempotencyBeginStatus.Replay);
        replay.Payload.Should().Be("winner");
    }

    [Fact]
    public async Task ExpiredCompletedKey_TreatedAsNew() {
        var (store, time) = CreateStore();

        await store.TryBeginAsync(Key, "fp", IdempotencyConflictBehavior.Conflict, Ct);
        await store.CompleteAsync(Key, "payload", isFailure: false, TimeSpan.FromHours(1), Ct);

        time.Advance(TimeSpan.FromHours(2));

        (await store.TryBeginAsync(Key, "fp", IdempotencyConflictBehavior.Conflict, Ct)).Status
            .Should().Be(IdempotencyBeginStatus.Began);
    }

    [Fact]
    public async Task Purge_RemovesExpiredCompletedRecords() {
        var (store, time) = CreateStore();

        await store.TryBeginAsync(Key, "fp", IdempotencyConflictBehavior.Conflict, Ct);
        await store.CompleteAsync(Key, "payload", isFailure: false, TimeSpan.FromHours(1), Ct);

        time.Advance(TimeSpan.FromHours(2));
        var purged = await store.PurgeCompletedAsync(time.GetUtcNow(), Ct);

        purged.Should().Be(1);
    }
}
