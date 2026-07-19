using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.Abstractions.Auditing;
using Elarion.Abstractions.Identity;
using Elarion.Abstractions.Pipeline;
using Elarion.Auditing;
using Elarion.Pipeline;
using Xunit;

namespace Elarion.Tests.Auditing;

public sealed class AuditDecoratorTests {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Success_RecordsOneSucceededRecord_InsideTheTransactionPath() {
        var (pipeline, _, trail) = Build(new StubHandler(_ => Result<string>.Success("ok")), user: new FakeUser("max"));

        var result = await pipeline.HandleAsync(new TestCommand(), Ct);

        result.IsSuccess.Should().BeTrue();
        trail.Records.Should().HaveCount(1);
        var (record, detached) = trail.Records[0];
        detached.Should().BeFalse();
        record.Outcome.Should().Be(AuditOutcome.Succeeded);
        record.Action.Should().Be("sales.createOrder");
        record.Module.Should().Be("Sales");
        record.UserId.Should().Be("max");
        record.ErrorKind.Should().BeNull();
    }

    [Fact]
    public async Task FailedResult_RecordsFailedRecord_OnTheDetachedPath() {
        var (pipeline, _, trail) = Build(new StubHandler(_ => Result<string>.Failure(AppError.BusinessRule("no"))));

        var result = await pipeline.HandleAsync(new TestCommand(), Ct);

        result.IsSuccess.Should().BeFalse();
        trail.Records.Should().HaveCount(1);
        var (record, detached) = trail.Records[0];
        detached.Should().BeTrue();
        record.Outcome.Should().Be(AuditOutcome.Failed);
        record.ErrorKind.Should().Be(nameof(ErrorKind.BusinessRule));
    }

    [Theory]
    [InlineData(ErrorKind.Unauthorized)]
    [InlineData(ErrorKind.Forbidden)]
    public async Task AuthorizationFailure_RecordsDenied(ErrorKind kind) {
        var error = new AppError { Kind = kind, Message = "denied" };
        var (pipeline, _, trail) = Build(new StubHandler(_ => Result<string>.Failure(error)));

        await pipeline.HandleAsync(new TestCommand(), Ct);

        trail.Records.Should().HaveCount(1);
        trail.Records[0].Record.Outcome.Should().Be(AuditOutcome.Denied);
        trail.Records[0].Record.ErrorKind.Should().Be(kind.ToString());
        trail.Records[0].Detached.Should().BeTrue();
    }

    [Fact]
    public async Task Exception_RecordsFailedDetached_AndRethrows() {
        var (pipeline, _, trail) = Build(new StubHandler(_ => throw new InvalidOperationException("boom")));

        var act = async () => await pipeline.HandleAsync(new TestCommand(), Ct);

        await act.Should().ThrowAsync<InvalidOperationException>();
        trail.Records.Should().HaveCount(1);
        trail.Records[0].Record.Outcome.Should().Be(AuditOutcome.Failed);
        trail.Records[0].Detached.Should().BeTrue();
    }

    [Fact]
    public async Task CooperativeCancellation_RecordsNothing() {
        using var cts = new CancellationTokenSource();
        var (pipeline, _, trail) = Build(new StubHandler(_ => {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        }));

        var act = async () => await pipeline.HandleAsync(new TestCommand(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        trail.Records.Should().BeEmpty();
    }

    [Fact]
    public async Task HandlerScopeWrites_LandOnTheRecord() {
        AuditScope? captured = null;
        var (pipeline, scope, trail) = Build(new StubHandler(_ => {
            captured!.SetResource("property", "42", "portfolio", "7");
            captured.AddDetail("displayName", "Hauptstraße 1");
            captured.AddChange(new AuditChange {
                Entity = "Property",
                EntityId = "42",
                Property = "Street",
                OldValue = "Old",
                NewValue = "New",
                Kind = AuditChangeKind.Modified
            });
            return Result<string>.Success("ok");
        }));
        captured = scope;

        await pipeline.HandleAsync(new TestCommand(), Ct);

        var record = trail.Records.Single().Record;
        record.ResourceType.Should().Be("property");
        record.ResourceId.Should().Be("42");
        record.ParentResourceType.Should().Be("portfolio");
        record.ParentResourceId.Should().Be("7");
        record.Details.Should().ContainKey("displayName").WhoseValue.Should().Be("Hauptstraße 1");
        record.Changes.Should().ContainSingle().Which.Property.Should().Be("Street");
    }

    [Fact]
    public async Task AuditableResource_IsTheFallback_WhenTheHandlerSetsNone() {
        var (pipeline, _, trail) = Build(
            new StubHandler(_ => Result<string>.Success("ok")),
            typeof(AnnotatedHandler));

        await pipeline.HandleAsync(new TestCommand(), Ct);

        trail.Records.Single().Record.ResourceType.Should().Be("order");
    }

    [Fact]
    public async Task Retry_ResetsAccumulatedChanges_SoTheRecordCarriesOnlyTheSucceedingAttempt() {
        // Chain shaped like the generated pipeline under resilience: the retrying wrapper sits between the
        // outer audit decorator and the inner commit decorator (resilience is outside the transaction).
        var scope = new AuditScope();
        var trail = new RecordingAuditTrail();
        var attempts = 0;
        var handler = new StubHandler(_ => {
            attempts++;
            scope.AddChange(new AuditChange
                { Entity = "Property", Kind = AuditChangeKind.Modified, Property = $"attempt{attempts}" });
            return attempts == 1
                ? Result<string>.Failure(AppError.BusinessRule("transient"))
                : Result<string>.Success("ok");
        });
        var commit = new AuditCommitDecorator<TestCommand, Result<string>>(handler, scope, trail);
        var retry = new RetryOnceDecorator(commit);
        var metadata = new HandlerMetadata(typeof(PlainHandler), typeof(TestCommand), typeof(Result<string>));
        var outer = new AuditDecorator<TestCommand, Result<string>>(retry, metadata, "sales.createOrder", "Sales",
            scope, trail, null);

        var result = await outer.HandleAsync(new TestCommand(), Ct);

        result.IsSuccess.Should().BeTrue();
        var record = trail.Records.Single().Record;
        record.Outcome.Should().Be(AuditOutcome.Succeeded);
        record.Changes.Should().ContainSingle().Which.Property.Should().Be("attempt2");
    }

    [Fact]
    public async Task SuccessWithoutTheCommitDecorator_RecordsNothing_TheReplaySemantics() {
        // An idempotent replay short-circuits before the inner commit decorator: the outer decorator sees a
        // successful, un-recorded invocation and deliberately records nothing — the action executed once.
        var scope = new AuditScope();
        var trail = new RecordingAuditTrail();
        var metadata = new HandlerMetadata(typeof(PlainHandler), typeof(TestCommand), typeof(Result<string>));
        var outer = new AuditDecorator<TestCommand, Result<string>>(
            new StubHandler(_ => Result<string>.Success("replayed")), metadata,
            "sales.createOrder", "Sales", scope, trail, null);

        await outer.HandleAsync(new TestCommand(), Ct);

        trail.Records.Should().BeEmpty();
    }

    [Fact]
    public async Task CommitPhaseFailure_WritesTheDetachedFailedRecord_BecauseTheEnlistedRecordNeverBecameDurable() {
        // Chain shaped like the generated pipeline: outer audit → transaction → inner commit → handler. The
        // sink enlists the success record; the transaction then fails at COMMIT, so the enlisted write is
        // rolled back and "Recorded" must never have become true — the detached Failed record is the trace.
        var scope = new AuditScope();
        var trail = new RecordingAuditTrail { Durability = AuditRecordDurability.EnlistedInTransaction };
        var commit = new AuditCommitDecorator<TestCommand, Result<string>>(
            new StubHandler(_ => Result<string>.Success("ok")), scope, trail);
        var failingTransaction =
            new DelegatingDecorator(commit, _ => throw new InvalidOperationException("commit lost"));
        var metadata = new HandlerMetadata(typeof(PlainHandler), typeof(TestCommand), typeof(Result<string>));
        var outer = new AuditDecorator<TestCommand, Result<string>>(
            failingTransaction, metadata, "sales.createOrder", "Sales", scope, trail, null);

        var act = async () => await outer.HandleAsync(new TestCommand(), Ct);

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("commit lost");
        trail.Records.Should().HaveCount(2);
        trail.Records[0].Detached.Should().BeFalse(); // The enlisted success write, rolled back with the tx.
        trail.Records[1].Detached.Should().BeTrue();
        trail.Records[1].Record.Outcome.Should().Be(AuditOutcome.Failed);
        trail.Records[1].Record.ErrorKind.Should().Be(nameof(ErrorKind.Internal));
    }

    [Fact]
    public async Task PostCommitFailure_AfterTheRecordBecameDurable_WritesNoSpuriousDetachedRecord() {
        // A later decorator (e.g. cache invalidation) throwing AFTER a successful commit: the durability
        // callback already promoted the pending mark, so the outer decorator records nothing extra.
        var scope = new AuditScope();
        var trail = new RecordingAuditTrail { Durability = AuditRecordDurability.EnlistedInTransaction };
        var commit = new AuditCommitDecorator<TestCommand, Result<string>>(
            new StubHandler(_ => Result<string>.Success("ok")), scope, trail);
        // Simulates the transaction decorator plus the EF durability callback: commit succeeded → promote.
        var committingTransaction = new DelegatingDecorator(commit, _ => scope.MarkRecorded());
        var postCommitFailure =
            new DelegatingDecorator(committingTransaction, _ => throw new TimeoutException("cache down"));
        var metadata = new HandlerMetadata(typeof(PlainHandler), typeof(TestCommand), typeof(Result<string>));
        var outer = new AuditDecorator<TestCommand, Result<string>>(
            postCommitFailure, metadata, "sales.createOrder", "Sales", scope, trail, null);

        var act = async () => await outer.HandleAsync(new TestCommand(), Ct);

        await act.Should().ThrowAsync<TimeoutException>();
        trail.Records.Should().ContainSingle().Which.Detached.Should().BeFalse();
        trail.Records[0].Record.Outcome.Should().Be(AuditOutcome.Succeeded);
    }

    [Fact]
    public async Task NestedAuditedInvocations_RecordIndependently() {
        var scope = new AuditScope();
        var trail = new RecordingAuditTrail();
        var metadata = new HandlerMetadata(typeof(PlainHandler), typeof(TestCommand), typeof(Result<string>));

        var innerPipeline = new AuditDecorator<TestCommand, Result<string>>(
            new AuditCommitDecorator<TestCommand, Result<string>>(
                new StubHandler(_ => {
                    scope.SetResource("child", "2");
                    return Result<string>.Success("inner");
                }), scope, trail),
            metadata, "sales.child", "Sales", scope, trail, null);

        var outerPipeline = new AuditDecorator<TestCommand, Result<string>>(
            new AuditCommitDecorator<TestCommand, Result<string>>(
                new StubHandler(_ => {
                    scope.SetResource("parent", "1");
                    innerPipeline.HandleAsync(new TestCommand(), Ct).AsTask().GetAwaiter().GetResult();
                    return Result<string>.Success("outer");
                }), scope, trail),
            metadata, "sales.parent", "Sales", scope, trail, null);

        await outerPipeline.HandleAsync(new TestCommand(), Ct);

        trail.Records.Should().HaveCount(2);
        trail.Records[0].Record.Action.Should().Be("sales.child");
        trail.Records[0].Record.ResourceType.Should().Be("child");
        trail.Records[1].Record.Action.Should().Be("sales.parent");
        trail.Records[1].Record.ResourceType.Should().Be("parent");
    }

    [Fact]
    public async Task DetachedWriteFailure_DoesNotMask_TheHandlersOriginalException() {
        var scope = new AuditScope();
        var metadata = new HandlerMetadata(typeof(PlainHandler), typeof(TestCommand), typeof(Result<string>));
        var outer = new AuditDecorator<TestCommand, Result<string>>(
            new StubHandler(_ => throw new InvalidOperationException("business boom")),
            metadata, "sales.createOrder", "Sales", scope, new ThrowingAuditTrail(), null);

        var act = async () => await outer.HandleAsync(new TestCommand(), Ct);

        // The audit sink's failure must not replace the handler's exception.
        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("business boom");
    }

    [Fact]
    public async Task DetachedWriteFailure_DoesNotTurn_AFailedResultIntoAnException() {
        var scope = new AuditScope();
        var metadata = new HandlerMetadata(typeof(PlainHandler), typeof(TestCommand), typeof(Result<string>));
        var outer = new AuditDecorator<TestCommand, Result<string>>(
            new StubHandler(_ => Result<string>.Failure(AppError.BusinessRule("domain no"))),
            metadata, "sales.createOrder", "Sales", scope, new ThrowingAuditTrail(), null);

        var result = await outer.HandleAsync(new TestCommand(), Ct);

        // The domain failure (a 4xx shape) must be returned unchanged, never escalated to a 500.
        result.IsSuccess.Should().BeFalse();
        result.Error.Kind.Should().Be(ErrorKind.BusinessRule);
        result.Error.Message.Should().Be("domain no");
    }

    [Fact]
    public void InactiveScope_IgnoresWrites_AndReportsInactive() {
        var scope = new AuditScope();

        scope.IsActive.Should().BeFalse();
        scope.SetResource("x", "1");
        scope.AddDetail("a", "b");
        scope.AddChange(new AuditChange { Entity = "X", Kind = AuditChangeKind.Added });

        var act = () => scope.BuildRecord(AuditOutcome.Succeeded, null);
        act.Should().Throw<InvalidOperationException>();
    }

    private static (IHandler<TestCommand, Result<string>> Pipeline, AuditScope Scope, RecordingAuditTrail Trail) Build(
        StubHandler handler, Type? handlerType = null, ICurrentUser? user = null) {
        var scope = new AuditScope();
        var trail = new RecordingAuditTrail();
        var commit = new AuditCommitDecorator<TestCommand, Result<string>>(handler, scope, trail);
        var metadata = new HandlerMetadata(handlerType ?? typeof(PlainHandler), typeof(TestCommand),
            typeof(Result<string>));
        var outer = new AuditDecorator<TestCommand, Result<string>>(
            commit, metadata, "sales.createOrder", "Sales", scope, trail, user);
        return (outer, scope, trail);
    }

    private sealed record TestCommand : ICommand;

    private sealed class StubHandler(Func<TestCommand, Result<string>> respond)
        : IHandler<TestCommand, Result<string>> {
        public ValueTask<Result<string>> HandleAsync(TestCommand request, CancellationToken ct) {
            return ValueTask.FromResult(respond(request));
        }
    }

    /// <summary>Runs the inner handler, then a side effect — the building block for commit/post-commit shapes.</summary>
    private sealed class DelegatingDecorator(
        IHandler<TestCommand, Result<string>> inner,
        Action<Result<string>> afterInner
    ) : IHandler<TestCommand, Result<string>> {
        public async ValueTask<Result<string>> HandleAsync(TestCommand request, CancellationToken ct) {
            var response = await inner.HandleAsync(request, ct);
            afterInner(response);
            return response;
        }
    }

    private sealed class RetryOnceDecorator(IHandler<TestCommand, Result<string>> inner)
        : IHandler<TestCommand, Result<string>> {
        public async ValueTask<Result<string>> HandleAsync(TestCommand request, CancellationToken ct) {
            var first = await inner.HandleAsync(request, ct);
            return first.IsSuccess ? first : await inner.HandleAsync(request, ct);
        }
    }

    private sealed class PlainHandler : IHandler<TestCommand, Result<string>> {
        public ValueTask<Result<string>> HandleAsync(TestCommand request, CancellationToken ct) {
            return default;
        }
    }

    [Auditable(Resource = "order")]
    private sealed class AnnotatedHandler : IHandler<TestCommand, Result<string>> {
        public ValueTask<Result<string>> HandleAsync(TestCommand request, CancellationToken ct) {
            return default;
        }
    }

    private sealed class ThrowingAuditTrail : IAuditTrail {
        public ValueTask<AuditRecordDurability> RecordAsync(
            Func<AuditRecord> buildRecord, CancellationToken cancellationToken) {
            throw new TimeoutException("audit sink down");
        }

        public ValueTask RecordDetachedAsync(AuditRecord record, CancellationToken cancellationToken) {
            throw new TimeoutException("audit sink down");
        }
    }

    private sealed class RecordingAuditTrail : IAuditTrail {
        public AuditRecordDurability Durability { get; init; } = AuditRecordDurability.Durable;

        public List<(AuditRecord Record, bool Detached)> Records { get; } = [];

        public ValueTask<AuditRecordDurability> RecordAsync(
            Func<AuditRecord> buildRecord, CancellationToken cancellationToken) {
            Records.Add((buildRecord(), false));
            return ValueTask.FromResult(Durability);
        }

        public ValueTask RecordDetachedAsync(AuditRecord record, CancellationToken cancellationToken) {
            Records.Add((record, true));
            return default;
        }
    }

    private sealed class FakeUser(string userId) : ICurrentUser {
        public string UserId => userId;
        public string? Email => null;
        public IReadOnlyList<string> Roles => [];
        public bool IsAuthenticated => true;

        public bool IsInRole(string role) {
            return false;
        }
    }
}
