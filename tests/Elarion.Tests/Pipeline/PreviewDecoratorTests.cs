using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.Abstractions.Authorization;
using Elarion.Abstractions.Identity;
using Elarion.Abstractions.Idempotency;
using Elarion.Abstractions.Pipeline;
using Elarion.Abstractions.Previews;
using Elarion.Abstractions.Serialization;
using Elarion.Idempotency;
using Elarion.Pipeline;
using Elarion.Sql;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elarion.Tests.Pipeline;

public sealed class PreviewDecoratorTests {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly IElarionJsonSerialization Json = PreviewTestJson.Create();

    // The bank-statement import of the consuming application: at preview time row "b" is new, but before the commit
    // it becomes a possible duplicate and would be held back. The commit must not silently act on that.
    private static readonly PreviewImportOutcome AsPreviewed = new(["a", "b"], []);
    private static readonly PreviewImportOutcome AfterTheDataChanged = new(["a"], ["b"]);

    private static PreviewDecorator<PreviewImportCommand, PreviewImportOutcome> Decorate(
        IHandler<PreviewImportCommand, Result<PreviewImportOutcome>> inner, IUnitOfWork? unitOfWork) {
        return new PreviewDecorator<PreviewImportCommand, PreviewImportOutcome>(inner, unitOfWork, Json);
    }

    [Fact]
    public async Task OrdinaryCall_PassesStraightThrough_WithoutAUnitOfWorkOrAFingerprint() {
        var handler = new DecidingHandler(AsPreviewed);
        var unitOfWork = new RecordingUnitOfWork();

        var result = await Decorate(handler, unitOfWork).HandleAsync(new PreviewImportCommand("s1"), Ct);

        result.IsSuccess.Should().BeTrue();
        result.Value.PreviewFingerprint.Should().BeNull();
        handler.Invocations.Should().Be(1);
        unitOfWork.Log.Should().BeEmpty();
    }

    [Fact]
    public async Task Preview_RunsTheHandler_FlushesItsWritesInANestedScope_AndRollsEverythingBack() {
        var handler = new DecidingHandler(AsPreviewed);
        var unitOfWork = new RecordingUnitOfWork();

        var result = await Decorate(handler, unitOfWork).HandleAsync(new PreviewImportCommand("s1", true), Ct);

        result.IsSuccess.Should().BeTrue();
        result.Value.NewRows.Should().Equal("a", "b");
        result.Value.PreviewFingerprint.Should().Be(PreviewTestJson.FingerprintOf(AsPreviewed));
        handler.Invocations.Should().Be(1);
        // The nested scope commits (EF flushes with SaveChanges there); the decorator's own scope rolls back.
        unitOfWork.Log.Should().Equal("begin:1", "begin:2", "commit:2", "rollback:1");
    }

    [Fact]
    public async Task Fingerprint_IsTheSha256OfTheCanonicalJson_OfTheOutcomeWithoutAFingerprint() {
        // A handler that sets a fingerprint itself does not change the hash: it is computed with the member cleared.
        var handler = new DecidingHandler(AsPreviewed with { PreviewFingerprint = "set-by-the-handler" });

        var result = await Decorate(handler, new RecordingUnitOfWork())
            .HandleAsync(new PreviewImportCommand("s1", true), Ct);

        var fingerprint = result.Value.PreviewFingerprint;
        fingerprint.Should().Be(PreviewTestJson.FingerprintOf(AsPreviewed));
        fingerprint.Should().HaveLength(43).And.MatchRegex("^[A-Za-z0-9_-]+$");
    }

    [Fact]
    public async Task Fingerprint_CoversTheOutcomeAsTheCallerSeesIt_WhenMembersAreVisibleByPermission() {
        // Rows held back are visible only with a permission this caller lacks: two outcomes that differ only there
        // must fingerprint alike, or the fingerprint would confirm a guess of what the caller may not see.
        static PreviewImportOutcome HideHeldBack(PreviewImportOutcome outcome, IPermissionChecker permissions) =>
            permissions.Holds("banking.read") ? outcome : outcome with { HeldBack = [] };

        async Task<string?> FingerprintAsync(PreviewImportOutcome outcome, IPermissionChecker? permissions) {
            var decorator = new PreviewDecorator<PreviewImportCommand, PreviewImportOutcome>(
                new DecidingHandler(outcome), new RecordingUnitOfWork(), Json, permissions, HideHeldBack);
            return (await decorator.HandleAsync(new PreviewImportCommand("s1", true), Ct)).Value.PreviewFingerprint;
        }

        var withoutRead = new Permissions();
        var withRead = new Permissions("banking.read");
        var a = new PreviewImportOutcome(["a"], ["x"]);
        var b = new PreviewImportOutcome(["a"], ["y"]);

        (await FingerprintAsync(a, withoutRead)).Should().Be(await FingerprintAsync(b, withoutRead));
        (await FingerprintAsync(a, withoutRead)).Should().Be(PreviewTestJson.FingerprintOf(new(["a"], [])));
        // Without a checker nothing is held, as for the hidden members themselves.
        (await FingerprintAsync(a, null)).Should().Be(await FingerprintAsync(b, withoutRead));
        (await FingerprintAsync(a, withRead)).Should().NotBe(await FingerprintAsync(b, withRead));
    }

    [Fact]
    public async Task Commit_WithTheFingerprintOfAnUnchangedOutcome_Commits() {
        var handler = new DecidingHandler(AsPreviewed);
        var preview = await Decorate(handler, new RecordingUnitOfWork())
            .HandleAsync(new PreviewImportCommand("s1", true), Ct);
        var unitOfWork = new RecordingUnitOfWork();

        var result = await Decorate(handler, unitOfWork)
            .HandleAsync(new PreviewImportCommand("s1", PreviewFingerprint: preview.Value.PreviewFingerprint), Ct);

        result.IsSuccess.Should().BeTrue();
        result.Value.PreviewFingerprint.Should().Be(preview.Value.PreviewFingerprint);
        unitOfWork.Log.Should().Equal("begin:1", "begin:2", "commit:2", "commit:1");
    }

    [Fact]
    public async Task Commit_AfterTheDecisionChanged_RollsBack_AndAnswersPreviewChangedWithTheNewOutcome() {
        var handler = new DecidingHandler(AsPreviewed);
        var preview = await Decorate(handler, new RecordingUnitOfWork())
            .HandleAsync(new PreviewImportCommand("s1", true), Ct);
        handler.Outcome = AfterTheDataChanged;
        var unitOfWork = new RecordingUnitOfWork();

        var result = await Decorate(handler, unitOfWork)
            .HandleAsync(new PreviewImportCommand("s1", PreviewFingerprint: preview.Value.PreviewFingerprint), Ct);

        result.IsSuccess.Should().BeFalse();
        result.Error.Kind.Should().Be(ErrorKind.Conflict);
        result.Error.Code.Should().Be(ErrorCodes.PreviewChanged);
        // The client can show what changed without another call, and confirm the new outcome with its fingerprint.
        var current = result.Error.Data.Should().BeOfType<PreviewImportOutcome>().Subject;
        current.HeldBack.Should().Equal("b");
        current.PreviewFingerprint.Should().Be(PreviewTestJson.FingerprintOf(AfterTheDataChanged))
            .And.NotBe(preview.Value.PreviewFingerprint);
        unitOfWork.Log.Should().Equal("begin:1", "begin:2", "commit:2", "rollback:1");
    }

    [Fact]
    public async Task Preview_WithAFingerprint_OnlyPreviews() {
        var unitOfWork = new RecordingUnitOfWork();

        var result = await Decorate(new DecidingHandler(AsPreviewed), unitOfWork)
            .HandleAsync(new PreviewImportCommand("s1", true, "stale"), Ct);

        result.IsSuccess.Should().BeTrue();
        unitOfWork.Log.Should().EndWith("rollback:1");
    }

    [Fact]
    public async Task FailedOutcome_RollsBackBothScopes_AndIsReturnedUnchanged() {
        var error = AppError.BusinessRule("The statement is already imported.", "statement.imported");
        var unitOfWork = new RecordingUnitOfWork();

        var result = await Decorate(new DecidingHandler(error), unitOfWork)
            .HandleAsync(new PreviewImportCommand("s1", true), Ct);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(error);
        unitOfWork.Log.Should().Equal("begin:1", "begin:2", "rollback:2", "rollback:1");
    }

    [Fact]
    public async Task HandlerException_DisposesBothScopesWithoutCommitting() {
        var unitOfWork = new RecordingUnitOfWork();

        var act = async () => await Decorate(new ThrowingHandler(), unitOfWork)
            .HandleAsync(new PreviewImportCommand("s1", PreviewFingerprint: "f"), Ct);

        await act.Should().ThrowAsync<InvalidOperationException>();
        unitOfWork.Log.Should().Equal("begin:1", "begin:2", "dispose-rollback:2", "dispose-rollback:1");
    }

    [Theory]
    [InlineData(true, null)]
    [InlineData(false, "fingerprint")]
    public async Task UnitOfWorkThatCannotRollBack_RefusesWithoutRunningTheHandler(bool preview, string? fingerprint) {
        var handler = new DecidingHandler(AsPreviewed);
        var unitOfWork = new RecordingUnitOfWork(false);

        var refused = await Decorate(handler, unitOfWork)
            .HandleAsync(new PreviewImportCommand("s1", preview, fingerprint), Ct);
        var withoutUnitOfWork = await Decorate(handler, null)
            .HandleAsync(new PreviewImportCommand("s1", preview, fingerprint), Ct);

        foreach (var result in new[] { refused, withoutUnitOfWork }) {
            result.IsSuccess.Should().BeFalse();
            result.Error.Kind.Should().Be(ErrorKind.Internal);
            result.Error.Code.Should().Be(ErrorCodes.PreviewUnsupported);
        }

        handler.Invocations.Should().Be(0);
        unitOfWork.Log.Should().BeEmpty();
    }

    [Fact]
    public void OnlyRealTransactionsClaimRollback() {
        // Fail closed: the seam defaults to false, the no-op in-memory unit of work keeps it, the SQL tier claims it
        // (the EF unit of work is covered by the PostgreSQL integration test).
        ((IUnitOfWork)new InMemoryUnitOfWork()).SupportsRollback.Should().BeFalse();
        ((IUnitOfWork)new RecordingUnitOfWork(false)).SupportsRollback.Should().BeFalse();
        ((IUnitOfWork)new SqlUnitOfWork(null!)).SupportsRollback.Should().BeTrue();
    }

    [Fact]
    public async Task ApplicationTransactionDecorator_NestsInsideThePreviewScope() {
        // IPreviewableRequest is an ICommand, so the application's transaction decorator attaches too: inside the
        // preview decorator, its scope nests and commits, and the preview still rolls the whole thing back.
        var unitOfWork = new RecordingUnitOfWork();
        var transaction = new TransactionDecorator<PreviewImportCommand, Result<PreviewImportOutcome>>(
            new DecidingHandler(AsPreviewed), unitOfWork, []);

        var result = await Decorate(transaction, unitOfWork).HandleAsync(new PreviewImportCommand("s1", true), Ct);

        result.IsSuccess.Should().BeTrue();
        unitOfWork.Log.Should().Equal("begin:1", "begin:2", "begin:3", "commit:3", "commit:2", "rollback:1");
    }

    [Fact]
    public async Task Preview_UnderIdempotency_RollsBackOnlyItsOwnScope_AndTheKeyStoresThePreview() {
        // [Idempotent] owns the root transaction: the preview's scope nests in it (a savepoint on a real backend) and
        // rolls back, while the idempotency key and the preview response commit. A retry of the same preview call
        // replays it; the commit is a different call with its own key.
        var unitOfWork = new RecordingUnitOfWork();
        var store = new RecordingStore();
        var decorator = new IdempotencyDecorator<PreviewImportCommand, Result<PreviewImportOutcome>>(
            Decorate(new DecidingHandler(AsPreviewed), unitOfWork),
            unitOfWork,
            store,
            new FixedKeyAccessor("preview-1"),
            new OutcomePolicy(),
            new AuthenticatedUser(),
            Json.Options);

        var result = await decorator.HandleAsync(new PreviewImportCommand("s1", true), Ct);

        result.IsSuccess.Should().BeTrue();
        unitOfWork.Log.Should().Equal("begin:1", "begin:2", "begin:3", "commit:3", "rollback:2", "commit:1");
        store.Payload.Should().Contain(result.Value.PreviewFingerprint!);
    }

    private sealed class DecidingHandler(Result<PreviewImportOutcome> outcome)
        : IHandler<PreviewImportCommand, Result<PreviewImportOutcome>> {
        public Result<PreviewImportOutcome> Outcome { get; set; } = outcome;

        public int Invocations { get; private set; }

        public DecidingHandler(PreviewImportOutcome outcome) : this(Result<PreviewImportOutcome>.Success(outcome)) { }

        public DecidingHandler(AppError error) : this(Result<PreviewImportOutcome>.Failure(error)) { }

        public ValueTask<Result<PreviewImportOutcome>> HandleAsync(PreviewImportCommand request, CancellationToken ct) {
            Invocations++;
            return ValueTask.FromResult(Outcome);
        }
    }

    private sealed class Permissions(params string[] held) : IPermissionChecker {
        public bool Holds(string permission) {
            return held.Contains(permission);
        }
    }

    private sealed class ThrowingHandler : IHandler<PreviewImportCommand, Result<PreviewImportOutcome>> {
        public ValueTask<Result<PreviewImportOutcome>> HandleAsync(PreviewImportCommand request, CancellationToken ct) {
            throw new InvalidOperationException("boom");
        }
    }

    // Records every scope's begin and end in order; scopes are numbered by when they began, so the log shows nesting.
    private sealed class RecordingUnitOfWork(bool supportsRollback = true) : IUnitOfWork {
        private int _scopes;

        public List<string> Log { get; } = [];

        public bool SupportsRollback => supportsRollback;

        public ValueTask<IUnitOfWorkScope> BeginAsync(UnitOfWorkOptions options, CancellationToken ct) {
            var id = ++_scopes;
            Log.Add($"begin:{id}");
            return new ValueTask<IUnitOfWorkScope>(new Scope(this, id));
        }

        private sealed class Scope(RecordingUnitOfWork owner, int id) : IUnitOfWorkScope {
            private bool _completed;

            public ValueTask CommitAsync(CancellationToken ct) {
                ct.ThrowIfCancellationRequested();
                owner.Log.Add($"commit:{id}");
                _completed = true;
                return default;
            }

            public ValueTask RollbackAsync(CancellationToken ct) {
                ct.ThrowIfCancellationRequested();
                owner.Log.Add($"rollback:{id}");
                _completed = true;
                return default;
            }

            public ValueTask CreateSavepointAsync(string name, CancellationToken ct) {
                return default;
            }

            public ValueTask RollbackToSavepointAsync(string name, CancellationToken ct) {
                return default;
            }

            public ValueTask DisposeAsync() {
                if (!_completed) owner.Log.Add($"dispose-rollback:{id}");
                return default;
            }
        }
    }

    private sealed class RecordingStore : IIdempotencyStore {
        public string? Payload { get; private set; }

        public ValueTask<IdempotencyBeginResult> TryBeginAsync(
            IdempotencyStoreKey key, string fingerprint, IdempotencyConflictBehavior conflictBehavior,
            CancellationToken ct) {
            return new ValueTask<IdempotencyBeginResult>(IdempotencyBeginResult.Began());
        }

        public ValueTask CompleteAsync(IdempotencyStoreKey key, string payload, bool isFailure, TimeSpan retention,
            CancellationToken ct) {
            Payload = payload;
            return default;
        }

        public ValueTask AbandonAsync(IdempotencyStoreKey key, CancellationToken ct) {
            return default;
        }

        public ValueTask<int> PurgeCompletedAsync(DateTimeOffset olderThanUtc, CancellationToken ct) {
            return new ValueTask<int>(0);
        }
    }

    private sealed class OutcomePolicy : IIdempotencyPayloadPolicy<PreviewImportCommand, Result<PreviewImportOutcome>> {
        public IdempotencyScope Scope => IdempotencyScope.Global;
        public bool KeyRequired => true;
        public bool Fingerprint => false;
        public IdempotencyConflictBehavior ConflictBehavior => IdempotencyConflictBehavior.Conflict;
        public IdempotencyFailureStorage StoreFailures => IdempotencyFailureStorage.None;
        public TimeSpan Retention => TimeSpan.FromHours(1);

        public string Serialize(Result<PreviewImportOutcome> response, JsonSerializerOptions options) {
            return JsonSerializer.Serialize(response.Value, PreviewTestJsonContext.Default.PreviewImportOutcome);
        }

        public Result<PreviewImportOutcome> Deserialize(string payload, JsonSerializerOptions options) {
            return JsonSerializer.Deserialize(payload, PreviewTestJsonContext.Default.PreviewImportOutcome)!;
        }
    }

    private sealed class FixedKeyAccessor(string key) : IIdempotencyKeyAccessor {
        public bool TryGetKey([NotNullWhen(true)] out string? resolvedKey) {
            resolvedKey = key;
            return true;
        }
    }

    private sealed class AuthenticatedUser : ICurrentUser {
        public string UserId => "user-1";
        public string? Email => null;
        public IReadOnlyList<string> Roles => [];
        public bool IsAuthenticated => true;

        public bool IsInRole(string role) {
            return false;
        }
    }
}

/// <summary>A previewable bank-statement import: the same command previews and commits.</summary>
internal sealed record PreviewImportCommand(string Statement, bool Preview = false, string? PreviewFingerprint = null)
    : IPreviewableRequest;

/// <summary>The import's decision: which rows are new and which are held back as possible duplicates.</summary>
internal sealed record PreviewImportOutcome(IReadOnlyList<string> NewRows, IReadOnlyList<string> HeldBack)
    : IPreviewOutcome<PreviewImportOutcome> {
    public string? PreviewFingerprint { get; init; }

    public PreviewImportOutcome WithPreviewFingerprint(string? fingerprint) {
        return this with { PreviewFingerprint = fingerprint };
    }
}

[JsonSerializable(typeof(PreviewImportOutcome))]
internal sealed partial class PreviewTestJsonContext : JsonSerializerContext;

internal static class PreviewTestJson {
    /// <summary>The canonical JSON accessor composed the way a host composes a module's source-generated context.</summary>
    public static IElarionJsonSerialization Create() {
        var services = new ServiceCollection();
        services.ConfigureElarionJson(options => options.TypeInfoResolvers.Add(PreviewTestJsonContext.Default));
        return services.BuildServiceProvider().GetRequiredService<IElarionJsonSerialization>();
    }

    /// <summary>The fingerprint computed independently of the decorator: SHA-256, base64url, over canonical JSON.</summary>
    public static string FingerprintOf(PreviewImportOutcome outcome) {
        var json = Create();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            outcome with { PreviewFingerprint = null }, json.GetTypeInfo<PreviewImportOutcome>());
        return Base64Url.EncodeToString(SHA256.HashData(bytes));
    }
}
