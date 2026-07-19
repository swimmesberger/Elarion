using System.Collections.Concurrent;
using AwesomeAssertions;
using Elarion.Abstractions.Idempotency;
using Elarion.Abstractions.Pipeline;
using Elarion.Idempotency;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Elarion.Tests.Idempotency;

public sealed class InMemoryIdempotencyStoreTests {
    private static readonly IdempotencyStoreKey Key = new("op.a", IdempotencyScope.Global, string.Empty, "k1");

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
    public async Task InMemoryStore_WarnsOnce_ThatItIsSingleProcess() {
        var log = new CapturingLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(log));
        services.AddElarionIdempotency();
        await using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<IIdempotencyStore>();

        await store.TryBeginAsync(Key, "fp", IdempotencyConflictBehavior.Conflict, Ct);
        await store.TryBeginAsync(Key, "fp", IdempotencyConflictBehavior.Conflict, Ct);

        log.Warnings.Should().ContainSingle().Which.Should().Contain("single-process");
    }

    [Fact]
    public async Task NoOpUnitOfWork_WarnsOnce_ThatItIsNonTransactional() {
        var log = new CapturingLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(log));
        services.AddElarionIdempotency();
        await using var provider = services.BuildServiceProvider();
        var uow = provider.GetRequiredService<IUnitOfWork>();

        await uow.BeginAsync(UnitOfWorkOptions.Default, Ct);
        await uow.BeginAsync(UnitOfWorkOptions.Default, Ct);

        log.Warnings.Should().ContainSingle().Which.Should().Contain("no-op");
    }

    [Fact]
    public async Task Begin_Complete_Replay() {
        var (store, _) = CreateStore();

        var first = await store.TryBeginAsync(Key, "fp", IdempotencyConflictBehavior.Conflict, Ct);
        first.Status.Should().Be(IdempotencyBeginStatus.Began);

        await store.CompleteAsync(Key, "payload", false, TimeSpan.FromHours(1), Ct);

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
        await store.CompleteAsync(Key, "payload", false, TimeSpan.FromHours(1), Ct);

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

        await store.CompleteAsync(Key, "winner", false, TimeSpan.FromHours(1), Ct);

        var replay = await waiter;
        replay.Status.Should().Be(IdempotencyBeginStatus.Replay);
        replay.Payload.Should().Be("winner");
    }

    [Fact]
    public async Task WaitThenReplay_DegradesToInProgress_WhenTheWinnerNeverCompletes() {
        var (store, time) = CreateStore();

        (await store.TryBeginAsync(Key, "fp", IdempotencyConflictBehavior.WaitThenReplay, Ct)).Status
            .Should().Be(IdempotencyBeginStatus.Began);

        // The wait registers its timer synchronously before the incomplete ValueTask is returned, so advancing
        // the fake clock past the 30 s ceiling deterministically times the waiter out.
        var waiter = store.TryBeginAsync(Key, "fp", IdempotencyConflictBehavior.WaitThenReplay, Ct).AsTask();
        waiter.IsCompleted.Should().BeFalse();

        time.Advance(TimeSpan.FromSeconds(30));

        var result = await waiter;
        result.Status.Should().Be(IdempotencyBeginStatus.InProgress);
    }

    [Fact]
    public async Task ExpiredCompletedKey_TreatedAsNew() {
        var (store, time) = CreateStore();

        await store.TryBeginAsync(Key, "fp", IdempotencyConflictBehavior.Conflict, Ct);
        await store.CompleteAsync(Key, "payload", false, TimeSpan.FromHours(1), Ct);

        time.Advance(TimeSpan.FromHours(2));

        (await store.TryBeginAsync(Key, "fp", IdempotencyConflictBehavior.Conflict, Ct)).Status
            .Should().Be(IdempotencyBeginStatus.Began);
    }

    [Fact]
    public async Task DifferentOperations_SameClientKey_DoNotCollide() {
        var (store, _) = CreateStore();
        var opA = new IdempotencyStoreKey("op.a", IdempotencyScope.Global, string.Empty, "shared-key");
        var opB = new IdempotencyStoreKey("op.b", IdempotencyScope.Global, string.Empty, "shared-key");

        (await store.TryBeginAsync(opA, "fp", IdempotencyConflictBehavior.Conflict, Ct)).Status
            .Should().Be(IdempotencyBeginStatus.Began);
        await store.CompleteAsync(opA, "payload-a", false, TimeSpan.FromHours(1), Ct);

        // A different operation with the same client key must claim its own record, not replay op.a's response.
        var beginB = await store.TryBeginAsync(opB, "fp", IdempotencyConflictBehavior.Conflict, Ct);
        beginB.Status.Should().Be(IdempotencyBeginStatus.Began);

        await store.CompleteAsync(opB, "payload-b", false, TimeSpan.FromHours(1), Ct);
        (await store.TryBeginAsync(opB, "fp", IdempotencyConflictBehavior.Conflict, Ct)).Payload.Should()
            .Be("payload-b");
        (await store.TryBeginAsync(opA, "fp", IdempotencyConflictBehavior.Conflict, Ct)).Payload.Should()
            .Be("payload-a");
    }

    [Fact]
    public async Task Purge_RemovesExpiredCompletedRecords() {
        var (store, time) = CreateStore();

        await store.TryBeginAsync(Key, "fp", IdempotencyConflictBehavior.Conflict, Ct);
        await store.CompleteAsync(Key, "payload", false, TimeSpan.FromHours(1), Ct);

        time.Advance(TimeSpan.FromHours(2));
        var purged = await store.PurgeCompletedAsync(time.GetUtcNow(), Ct);

        purged.Should().Be(1);
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider {
        public ConcurrentQueue<string> Warnings { get; } = new();

        public ILogger CreateLogger(string categoryName) {
            return new CapturingLogger(Warnings);
        }

        public void Dispose() {
        }

        private sealed class CapturingLogger(ConcurrentQueue<string> warnings) : ILogger {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull {
                return null;
            }

            public bool IsEnabled(LogLevel logLevel) {
                return true;
            }

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) {
                if (logLevel == LogLevel.Warning) warnings.Enqueue(formatter(state, exception));
            }
        }
    }
}
