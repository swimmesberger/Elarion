using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.Abstractions.Messaging;
using Elarion.Abstractions.Pipeline;
using Xunit;
using Elarion.Pipeline;

namespace Elarion.Tests.Pipeline;

public sealed class TransactionDecoratorTests {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SuccessfulResult_Commits() {
        var uow = new RecordingUnitOfWork();
        var decorator = new TransactionDecorator<TestCommand, Result<string>>(
            new StubHandler(Result<string>.Success("ok")), uow, []);

        var result = await decorator.HandleAsync(new TestCommand(), Ct);

        result.IsSuccess.Should().BeTrue();
        uow.Scope!.Commits.Should().Be(1);
        uow.Scope.Rollbacks.Should().Be(0);
    }

    [Fact]
    public async Task FailedResult_RollsBack() {
        var uow = new RecordingUnitOfWork();
        var decorator = new TransactionDecorator<TestCommand, Result<string>>(
            new StubHandler(Result<string>.Failure(AppError.BusinessRule("no"))), uow, []);

        await decorator.HandleAsync(new TestCommand(), Ct);

        uow.Scope!.Commits.Should().Be(0);
        uow.Scope.Rollbacks.Should().Be(1);
    }

    [Fact]
    public async Task SuccessfulHandler_ThenCancellation_StillCommits_NotRollBack() {
        var uow = new RecordingUnitOfWork();
        using var cts = new CancellationTokenSource();
        var decorator = new TransactionDecorator<TestCommand, Result<string>>(
            new CancelAfterSuccessHandler(cts, Result<string>.Success("ok")), uow, []);

        // A cancellation arriving after the handler succeeds must not roll back a completed command — the commit
        // runs uncancellably (M8). The recording scope throws if a cancelled token reaches commit.
        var result = await decorator.HandleAsync(new TestCommand(), cts.Token);

        result.IsSuccess.Should().BeTrue();
        uow.Scope!.Commits.Should().Be(1);
        uow.Scope.Rollbacks.Should().Be(0);
    }

    [Fact]
    public void AppliesTo_Command_True_ButNotWhenIdempotent() {
        AppliesTo(typeof(PlainCommandHandler)).Should().BeTrue();
        // [Idempotent] owns the unit of work — a second transaction would nest.
        AppliesTo(typeof(IdempotentCommandHandler)).Should().BeFalse();
    }

    [Fact]
    public void AppliesTo_IntegrationConsumer_False_ByDefault_TheInboxOwnsTheTransaction() {
        // ADR-0022: the default-on inbox decorator claims the message id and runs the consumer inside its own
        // unit of work, so the plain transaction decorator must stay off — mirroring the [Idempotent] exclusion.
        AppliesTo(typeof(PlainIntegrationConsumer)).Should().BeFalse();
    }

    [Fact]
    public void AppliesTo_IntegrationConsumer_True_WhenDuplicatesAreAllowed() {
        // [AllowDuplicates] hands the plain transaction back — the consumer has declared redelivery harmless.
        AppliesTo(typeof(OptedOutIntegrationConsumer)).Should().BeTrue();
    }

    [Fact]
    public void AppliesTo_DomainConsumer_False() {
        AppliesTo(typeof(DomainConsumer)).Should().BeFalse();
    }

    [Fact]
    public void AppliesTo_Command_False_WhenItOptsOutWithNoTransaction() {
        AppliesTo(typeof(NoTransactionCommandHandler)).Should().BeFalse();
    }

    [Fact]
    public async Task CommitFailure_TheFirstTranslatorThatAnswers_BecomesTheFailedResult() {
        var uow = new RecordingUnitOfWork { CommitFailure = new InvalidOperationException("23P01") };
        var decorator = new TransactionDecorator<TestCommand, Result<string>>(
            new StubHandler(Result<string>.Success("ok")), uow,
            [new Translator(_ => null), new Translator(_ => AppError.Conflict("overlap", "periods.overlap")),
                new Translator(_ => AppError.Internal("never asked"))]);

        var result = await decorator.HandleAsync(new TestCommand(), Ct);

        result.IsSuccess.Should().BeFalse();
        result.Error.Code.Should().Be("periods.overlap");
        uow.Scope!.Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task HandlerException_IsTranslatedToo_AndTheScopeIsNotCommitted() {
        var uow = new RecordingUnitOfWork();
        var decorator = new TransactionDecorator<TestCommand, Result<string>>(
            new ThrowingHandler(new InvalidOperationException("unique")), uow,
            [new Translator(_ => AppError.Conflict("taken"))]);

        var result = await decorator.HandleAsync(new TestCommand(), Ct);

        result.Error.Kind.Should().Be(ErrorKind.Conflict);
        uow.Scope!.Commits.Should().Be(0);
        uow.Scope.Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task CommitFailure_NoTranslatorAnswers_Propagates() {
        var uow = new RecordingUnitOfWork { CommitFailure = new InvalidOperationException("boom") };
        var decorator = new TransactionDecorator<TestCommand, Result<string>>(
            new StubHandler(Result<string>.Success("ok")), uow, [new Translator(_ => null)]);

        var act = async () => await decorator.HandleAsync(new TestCommand(), Ct);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");
    }

    [Fact]
    public async Task Failure_ForAResponseThatIsNotAResult_Propagates() {
        var uow = new RecordingUnitOfWork();
        var decorator = new TransactionDecorator<PlainCommand, string>(
            new PlainHandler(), uow, [new Translator(_ => AppError.Conflict("taken"))]);

        var act = async () => await decorator.HandleAsync(new PlainCommand(), Ct);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Cancellation_IsNeverTranslated() {
        var uow = new RecordingUnitOfWork();
        var decorator = new TransactionDecorator<TestCommand, Result<string>>(
            new ThrowingHandler(new OperationCanceledException()), uow,
            [new Translator(_ => AppError.Conflict("taken"))]);

        var act = async () => await decorator.HandleAsync(new TestCommand(), Ct);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private sealed class Translator(Func<Exception, AppError?> translate) : IUnitOfWorkFailureTranslator {
        public AppError? Translate(Exception exception) => translate(exception);
    }

    private sealed record PlainCommand : ICommand;

    private sealed class PlainHandler : IHandler<PlainCommand, string> {
        public ValueTask<string> HandleAsync(PlainCommand request, CancellationToken ct) =>
            throw new InvalidOperationException("boom");
    }

    private sealed class ThrowingHandler(Exception exception) : IHandler<TestCommand, Result<string>> {
        public ValueTask<Result<string>> HandleAsync(TestCommand request, CancellationToken ct) => throw exception;
    }

    [NoTransaction]
    private sealed class NoTransactionCommandHandler : IHandler<TestCommand, Result<string>> {
        public ValueTask<Result<string>> HandleAsync(TestCommand request, CancellationToken ct) {
            return ValueTask.FromResult(Result<string>.Success("ok"));
        }
    }

    private static bool AppliesTo(Type handlerType) {
        var handlerInterface = handlerType.GetInterfaces()
            .First(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IHandler<,>));
        var args = handlerInterface.GetGenericArguments();
        return TransactionDecorator<TestCommand, Result<string>>.AppliesTo(
            new HandlerMetadata(handlerType, args[0], args[1]));
    }

    private sealed record TestIntegrationEvent : IIntegrationEvent;

    private sealed record TestDomainEvent : IDomainEvent;

    private sealed class PlainCommandHandler : IHandler<TestCommand, Result<string>> {
        public ValueTask<Result<string>> HandleAsync(TestCommand request, CancellationToken ct) {
            return ValueTask.FromResult(Result<string>.Success("ok"));
        }
    }

    [Elarion.Abstractions.Idempotency.Idempotent]
    private sealed class IdempotentCommandHandler : IHandler<TestCommand, Result<string>> {
        public ValueTask<Result<string>> HandleAsync(TestCommand request, CancellationToken ct) {
            return ValueTask.FromResult(Result<string>.Success("ok"));
        }
    }

    private sealed class PlainIntegrationConsumer : IHandler<TestIntegrationEvent> {
        public ValueTask<Result> HandleAsync(TestIntegrationEvent request, CancellationToken ct) {
            return ValueTask.FromResult(Result.Success());
        }
    }

    [AllowDuplicates]
    private sealed class OptedOutIntegrationConsumer : IHandler<TestIntegrationEvent> {
        public ValueTask<Result> HandleAsync(TestIntegrationEvent request, CancellationToken ct) {
            return ValueTask.FromResult(Result.Success());
        }
    }

    private sealed class DomainConsumer : IHandler<TestDomainEvent> {
        public ValueTask<Result> HandleAsync(TestDomainEvent request, CancellationToken ct) {
            return ValueTask.FromResult(Result.Success());
        }
    }

    private sealed record TestCommand : ICommand;

    private sealed class StubHandler(Result<string> response) : IHandler<TestCommand, Result<string>> {
        public ValueTask<Result<string>> HandleAsync(TestCommand request, CancellationToken ct) {
            return ValueTask.FromResult(response);
        }
    }

    private sealed class CancelAfterSuccessHandler(CancellationTokenSource cts, Result<string> response)
        : IHandler<TestCommand, Result<string>> {
        public ValueTask<Result<string>> HandleAsync(TestCommand request, CancellationToken ct) {
            cts.Cancel();
            return ValueTask.FromResult(response);
        }
    }

    private sealed class RecordingUnitOfWork : IUnitOfWork {
        public RecordingScope? Scope { get; private set; }

        public Exception? CommitFailure { get; init; }

        public ValueTask<IUnitOfWorkScope> BeginAsync(UnitOfWorkOptions options, CancellationToken ct) {
            Scope = new RecordingScope { CommitFailure = CommitFailure };
            return new ValueTask<IUnitOfWorkScope>(Scope);
        }

        public sealed class RecordingScope : IUnitOfWorkScope {
            public int Commits { get; private set; }
            public int Rollbacks { get; private set; }

            public bool Disposed { get; private set; }

            public Exception? CommitFailure { get; init; }

            public ValueTask CommitAsync(CancellationToken ct) {
                ct.ThrowIfCancellationRequested();
                if (CommitFailure is not null) throw CommitFailure;
                Commits++;
                return default;
            }

            public ValueTask RollbackAsync(CancellationToken ct) {
                ct.ThrowIfCancellationRequested();
                Rollbacks++;
                return default;
            }

            public ValueTask CreateSavepointAsync(string name, CancellationToken ct) {
                return default;
            }

            public ValueTask RollbackToSavepointAsync(string name, CancellationToken ct) {
                return default;
            }

            public ValueTask DisposeAsync() {
                Disposed = true;
                return default;
            }
        }
    }
}
