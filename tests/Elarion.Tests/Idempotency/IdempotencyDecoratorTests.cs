using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.Abstractions.Identity;
using Elarion.Abstractions.Idempotency;
using Elarion.Abstractions.Pipeline;
using Xunit;

namespace Elarion.Tests.Idempotency;

public sealed class IdempotencyDecoratorTests {
    private static readonly JsonSerializerOptions Json = new() { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };

    private static IdempotencyDecorator<TestCommand, Result<string>> Decorate(
        IHandler<TestCommand, Result<string>> inner,
        RecordingStore store,
        RecordingUnitOfWork unitOfWork,
        string? key,
        TestPolicy? policy = null,
        ICurrentUser? user = null) =>
        new(
            inner,
            unitOfWork,
            store,
            new FakeKeyAccessor(key),
            policy ?? new TestPolicy(),
            user ?? new FakeCurrentUser(authenticated: true),
            Json);

    [Fact]
    public async Task FirstCall_RunsHandler_Completes_AndCommits() {
        var inner = new RecordingHandler(Result<string>.Success("done"));
        var store = new RecordingStore(IdempotencyBeginResult.Began());
        var uow = new RecordingUnitOfWork();

        var result = await Decorate(inner, store, uow, "k1")
            .HandleAsync(new TestCommand(1), TestContext.Current.CancellationToken);

        result.Value.Should().Be("done");
        inner.Invocations.Should().Be(1);
        store.CompleteCount.Should().Be(1);
        store.CompletedIsFailure.Should().BeFalse();
        uow.Scope!.Commits.Should().Be(1);
        uow.Scope.Rollbacks.Should().Be(0);
    }

    [Fact]
    public async Task Replay_ReturnsStoredResult_WithoutRunningHandler() {
        var payload = JsonSerializer.Serialize(
            new StoredResult { Ok = true, Value = JsonSerializer.SerializeToElement("first", Json) }, Json);
        var inner = new RecordingHandler(Result<string>.Success("second"));
        var store = new RecordingStore(IdempotencyBeginResult.Replay(payload));
        var uow = new RecordingUnitOfWork();

        var result = await Decorate(inner, store, uow, "k1")
            .HandleAsync(new TestCommand(1), TestContext.Current.CancellationToken);

        result.Value.Should().Be("first");
        inner.Invocations.Should().Be(0);
        store.CompleteCount.Should().Be(0);
        uow.Scope!.Rollbacks.Should().Be(1);
    }

    [Fact]
    public async Task InProgressDuplicate_Returns409_WithoutRunningHandler() {
        var inner = new RecordingHandler(Result<string>.Success("done"));
        var store = new RecordingStore(IdempotencyBeginResult.InProgress());
        var uow = new RecordingUnitOfWork();

        var result = await Decorate(inner, store, uow, "k1")
            .HandleAsync(new TestCommand(1), TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeFalse();
        result.Error.Kind.Should().Be(ErrorKind.Conflict);
        inner.Invocations.Should().Be(0);
        uow.Scope!.Rollbacks.Should().Be(1);
    }

    [Fact]
    public async Task FingerprintMismatch_Returns422() {
        var store = new RecordingStore(IdempotencyBeginResult.FingerprintMismatch());

        var result = await Decorate(new RecordingHandler(Result<string>.Success("x")), store, new RecordingUnitOfWork(), "k1")
            .HandleAsync(new TestCommand(1), TestContext.Current.CancellationToken);

        result.Error.Kind.Should().Be(ErrorKind.BusinessRule);
    }

    [Fact]
    public async Task MissingKey_KeyRequired_Returns400_WithoutRunningHandler() {
        var inner = new RecordingHandler(Result<string>.Success("done"));
        var store = new RecordingStore(IdempotencyBeginResult.Began());

        var result = await Decorate(inner, store, new RecordingUnitOfWork(), key: null)
            .HandleAsync(new TestCommand(1), TestContext.Current.CancellationToken);

        result.Error.Kind.Should().Be(ErrorKind.Validation);
        inner.Invocations.Should().Be(0);
        store.CompleteCount.Should().Be(0);
    }

    [Fact]
    public async Task MissingKey_KeyOptional_PassesThrough_WithoutTouchingStore() {
        var inner = new RecordingHandler(Result<string>.Success("done"));
        var store = new RecordingStore(IdempotencyBeginResult.Began());
        var policy = new TestPolicy(keyRequired: false);

        var result = await Decorate(inner, store, new RecordingUnitOfWork(), key: null, policy)
            .HandleAsync(new TestCommand(1), TestContext.Current.CancellationToken);

        result.Value.Should().Be("done");
        inner.Invocations.Should().Be(1);
        store.CompleteCount.Should().Be(0);
    }

    [Fact]
    public async Task InBandRequestKey_UsedWhenNoTransportKey() {
        var inner = new RecordingHandler(Result<string>.Success("done"));
        var store = new RecordingStore(IdempotencyBeginResult.Began());

        var result = await Decorate(inner, store, new RecordingUnitOfWork(), key: null)
            .HandleAsync(new TestCommand(1, "in-band"), TestContext.Current.CancellationToken);

        result.Value.Should().Be("done");
        store.LastKey.Key.Should().Be("in-band");
    }

    [Fact]
    public async Task FailedResult_RollsBack_AndAbandons_NotStored() {
        var inner = new RecordingHandler(Result<string>.Failure(AppError.BusinessRule("nope")));
        var store = new RecordingStore(IdempotencyBeginResult.Began());
        var uow = new RecordingUnitOfWork();

        var result = await Decorate(inner, store, uow, "k1")
            .HandleAsync(new TestCommand(1), TestContext.Current.CancellationToken);

        result.Error.Kind.Should().Be(ErrorKind.BusinessRule);
        store.CompleteCount.Should().Be(0);
        store.AbandonCount.Should().Be(1);
        uow.Scope!.Rollbacks.Should().Be(1);
        uow.Scope.Commits.Should().Be(0);
    }

    [Fact]
    public async Task HandlerThrows_Abandons_RollsBack_AndRethrows() {
        var inner = new ThrowingHandler();
        var store = new RecordingStore(IdempotencyBeginResult.Began());
        var uow = new RecordingUnitOfWork();

        var act = async () => await Decorate(inner, store, uow, "k1")
            .HandleAsync(new TestCommand(1), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();
        store.AbandonCount.Should().Be(1);
        uow.Scope!.Rollbacks.Should().Be(1);
    }

    [Fact]
    public async Task StoreFailures_DefinitiveFailure_RollsBackToSavepoint_StoresFailure_AndCommits() {
        var inner = new RecordingHandler(Result<string>.Failure(AppError.BusinessRule("declined")));
        var store = new RecordingStore(IdempotencyBeginResult.Began());
        var uow = new RecordingUnitOfWork();
        var policy = new TestPolicy(storeFailures: IdempotencyFailureStorage.Definitive);

        var result = await Decorate(inner, store, uow, "k1", policy)
            .HandleAsync(new TestCommand(1), TestContext.Current.CancellationToken);

        result.Error.Kind.Should().Be(ErrorKind.BusinessRule);
        uow.Scope!.Savepoints.Should().Be(1);
        uow.Scope.SavepointRollbacks.Should().Be(1);
        store.CompleteCount.Should().Be(1);
        store.CompletedIsFailure.Should().BeTrue();
        uow.Scope.Commits.Should().Be(1);
    }

    [Fact]
    public async Task StoreFailures_TransientFailure_StaysRetryable() {
        var inner = new RecordingHandler(Result<string>.Failure(AppError.Internal("boom")));
        var store = new RecordingStore(IdempotencyBeginResult.Began());
        var uow = new RecordingUnitOfWork();
        var policy = new TestPolicy(storeFailures: IdempotencyFailureStorage.Definitive);

        var result = await Decorate(inner, store, uow, "k1", policy)
            .HandleAsync(new TestCommand(1), TestContext.Current.CancellationToken);

        result.Error.Kind.Should().Be(ErrorKind.Internal);
        store.CompleteCount.Should().Be(0);
        store.AbandonCount.Should().Be(1);
        uow.Scope!.Rollbacks.Should().Be(1);
    }

    [Fact]
    public async Task DefinitiveFailureReplay_ReturnsStoredFailure() {
        var payload = JsonSerializer.Serialize(
            new StoredResult { Ok = false, Error = AppError.BusinessRule("declined") }, Json);
        var store = new RecordingStore(IdempotencyBeginResult.Replay(payload, isFailurePayload: true));

        var result = await Decorate(new RecordingHandler(Result<string>.Success("x")), store, new RecordingUnitOfWork(), "k1")
            .HandleAsync(new TestCommand(1), TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeFalse();
        result.Error.Kind.Should().Be(ErrorKind.BusinessRule);
        result.Error.Message.Should().Be("declined");
    }

    [Fact]
    public async Task CurrentUserScope_Unauthenticated_Returns401() {
        var policy = new TestPolicy(scope: IdempotencyScope.CurrentUser);
        var store = new RecordingStore(IdempotencyBeginResult.Began());

        var result = await Decorate(
                new RecordingHandler(Result<string>.Success("x")), store, new RecordingUnitOfWork(), "k1", policy,
                user: new FakeCurrentUser(authenticated: false))
            .HandleAsync(new TestCommand(1), TestContext.Current.CancellationToken);

        result.Error.Kind.Should().Be(ErrorKind.Unauthorized);
        store.CompleteCount.Should().Be(0);
    }

    [Fact]
    public async Task ConflictMode_SetsLockTimeout_WaitMode_DoesNot() {
        var conflictUow = new RecordingUnitOfWork();
        await Decorate(new RecordingHandler(Result<string>.Success("x")), new RecordingStore(IdempotencyBeginResult.Began()), conflictUow, "k1",
                new TestPolicy(conflict: IdempotencyConflictBehavior.Conflict))
            .HandleAsync(new TestCommand(1), TestContext.Current.CancellationToken);
        conflictUow.Scope!.LockTimeout.Should().NotBeNull();

        var waitUow = new RecordingUnitOfWork();
        await Decorate(new RecordingHandler(Result<string>.Success("x")), new RecordingStore(IdempotencyBeginResult.Began()), waitUow, "k1",
                new TestPolicy(conflict: IdempotencyConflictBehavior.WaitThenReplay))
            .HandleAsync(new TestCommand(1), TestContext.Current.CancellationToken);
        waitUow.Scope!.LockTimeout.Should().BeNull();
    }

    private sealed record TestCommand(int Id, string? IdempotencyKey = null) : ICommand, IIdempotentRequest;

    private sealed class TestPolicy(
        IdempotencyScope scope = IdempotencyScope.Global,
        bool keyRequired = true,
        bool fingerprint = true,
        IdempotencyConflictBehavior conflict = IdempotencyConflictBehavior.Conflict,
        IdempotencyFailureStorage storeFailures = IdempotencyFailureStorage.None)
        : IIdempotencyPayloadPolicy<TestCommand, Result<string>> {
        public IdempotencyScope Scope => scope;
        public bool KeyRequired => keyRequired;
        public bool Fingerprint => fingerprint;
        public IdempotencyConflictBehavior ConflictBehavior => conflict;
        public IdempotencyFailureStorage StoreFailures => storeFailures;
        public TimeSpan Retention => TimeSpan.FromHours(24);

        public string Serialize(Result<string> response, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(
                response.IsSuccess
                    ? new StoredResult { Ok = true, Value = JsonSerializer.SerializeToElement(response.Value, options) }
                    : new StoredResult { Ok = false, Error = response.Error },
                options);

        public Result<string> Deserialize(string payload, JsonSerializerOptions options) {
            var stored = JsonSerializer.Deserialize<StoredResult>(payload, options)!;
            if (!stored.Ok)
                return Result<string>.Failure(stored.Error ?? AppError.InternalError);
            var value = stored.Value is { } element ? element.Deserialize<string>(options) : null;
            return Result<string>.Success(value!);
        }
    }

    private sealed class RecordingHandler(Result<string> response) : IHandler<TestCommand, Result<string>> {
        public int Invocations { get; private set; }

        public ValueTask<Result<string>> HandleAsync(TestCommand request, CancellationToken ct) {
            Invocations++;
            return ValueTask.FromResult(response);
        }
    }

    private sealed class ThrowingHandler : IHandler<TestCommand, Result<string>> {
        public ValueTask<Result<string>> HandleAsync(TestCommand request, CancellationToken ct) =>
            throw new InvalidOperationException("boom");
    }

    private sealed class RecordingStore(IdempotencyBeginResult begin) : IIdempotencyStore {
        public int CompleteCount { get; private set; }
        public int AbandonCount { get; private set; }
        public bool CompletedIsFailure { get; private set; }
        public IdempotencyStoreKey LastKey { get; private set; }

        public ValueTask<IdempotencyBeginResult> TryBeginAsync(
            IdempotencyStoreKey key, string fingerprint, IdempotencyConflictBehavior conflictBehavior, CancellationToken ct) {
            LastKey = key;
            return new ValueTask<IdempotencyBeginResult>(begin);
        }

        public ValueTask CompleteAsync(IdempotencyStoreKey key, string payload, bool isFailure, TimeSpan retention, CancellationToken ct) {
            CompleteCount++;
            CompletedIsFailure = isFailure;
            return default;
        }

        public ValueTask AbandonAsync(IdempotencyStoreKey key, CancellationToken ct) {
            AbandonCount++;
            return default;
        }

        public ValueTask<int> PurgeCompletedAsync(DateTimeOffset olderThanUtc, CancellationToken ct) => new(0);
    }

    private sealed class RecordingUnitOfWork : IUnitOfWork {
        public RecordingScope? Scope { get; private set; }

        public ValueTask<IUnitOfWorkScope> BeginAsync(UnitOfWorkOptions options, CancellationToken ct) {
            Scope = new RecordingScope(options.LockTimeout);
            return new ValueTask<IUnitOfWorkScope>(Scope);
        }

        public sealed class RecordingScope(TimeSpan? lockTimeout) : IUnitOfWorkScope {
            public TimeSpan? LockTimeout { get; } = lockTimeout;
            public int Commits { get; private set; }
            public int Rollbacks { get; private set; }
            public int Savepoints { get; private set; }
            public int SavepointRollbacks { get; private set; }

            public ValueTask CommitAsync(CancellationToken ct) { Commits++; return default; }
            public ValueTask RollbackAsync(CancellationToken ct) { Rollbacks++; return default; }
            public ValueTask CreateSavepointAsync(string name, CancellationToken ct) { Savepoints++; return default; }
            public ValueTask RollbackToSavepointAsync(string name, CancellationToken ct) { SavepointRollbacks++; return default; }
            public ValueTask DisposeAsync() => default;
        }
    }

    private sealed class FakeKeyAccessor(string? key) : IIdempotencyKeyAccessor {
        public bool TryGetKey([NotNullWhen(true)] out string? resolvedKey) {
            resolvedKey = key;
            return !string.IsNullOrEmpty(key);
        }
    }

    private sealed class FakeCurrentUser(bool authenticated, string userId = "user-1") : ICurrentUser {
        public string UserId => userId;
        public string? Email => null;
        public IReadOnlyList<string> Roles => [];
        public bool IsAuthenticated => authenticated;
        public bool IsInRole(string role) => false;
    }
}
