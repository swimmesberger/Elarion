using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.Abstractions.Messaging;
using Elarion.Abstractions.Pipeline;
using Xunit;

namespace Elarion.Tests.Pipeline;

public sealed class TransactionDecoratorTests {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SuccessfulResult_Commits() {
        var uow = new RecordingUnitOfWork();
        var decorator = new TransactionDecorator<TestCommand, Result<string>>(
            new StubHandler(Result<string>.Success("ok")), uow);

        var result = await decorator.HandleAsync(new TestCommand(), Ct);

        result.IsSuccess.Should().BeTrue();
        uow.Scope!.Commits.Should().Be(1);
        uow.Scope.Rollbacks.Should().Be(0);
    }

    [Fact]
    public async Task FailedResult_RollsBack() {
        var uow = new RecordingUnitOfWork();
        var decorator = new TransactionDecorator<TestCommand, Result<string>>(
            new StubHandler(Result<string>.Failure(AppError.BusinessRule("no"))), uow);

        await decorator.HandleAsync(new TestCommand(), Ct);

        uow.Scope!.Commits.Should().Be(0);
        uow.Scope.Rollbacks.Should().Be(1);
    }

    [Fact]
    public async Task SuccessfulHandler_ThenCancellation_StillCommits_NotRollBack() {
        var uow = new RecordingUnitOfWork();
        using var cts = new CancellationTokenSource();
        var decorator = new TransactionDecorator<TestCommand, Result<string>>(
            new CancelAfterSuccessHandler(cts, Result<string>.Success("ok")), uow);

        // A cancellation arriving after the handler succeeds must not roll back a completed command — the commit
        // runs uncancellably (M8). The recording scope throws if a cancelled token reaches commit.
        var result = await decorator.HandleAsync(new TestCommand(), cts.Token);

        result.IsSuccess.Should().BeTrue();
        uow.Scope!.Commits.Should().Be(1);
        uow.Scope.Rollbacks.Should().Be(0);
    }

    private sealed record TestCommand : ICommand;

    private sealed class StubHandler(Result<string> response) : IHandler<TestCommand, Result<string>> {
        public ValueTask<Result<string>> HandleAsync(TestCommand request, CancellationToken ct) =>
            ValueTask.FromResult(response);
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

        public ValueTask<IUnitOfWorkScope> BeginAsync(UnitOfWorkOptions options, CancellationToken ct) {
            Scope = new RecordingScope();
            return new ValueTask<IUnitOfWorkScope>(Scope);
        }

        public sealed class RecordingScope : IUnitOfWorkScope {
            public int Commits { get; private set; }
            public int Rollbacks { get; private set; }

            public ValueTask CommitAsync(CancellationToken ct) { ct.ThrowIfCancellationRequested(); Commits++; return default; }
            public ValueTask RollbackAsync(CancellationToken ct) { ct.ThrowIfCancellationRequested(); Rollbacks++; return default; }
            public ValueTask CreateSavepointAsync(string name, CancellationToken ct) => default;
            public ValueTask RollbackToSavepointAsync(string name, CancellationToken ct) => default;
            public ValueTask DisposeAsync() => default;
        }
    }
}
