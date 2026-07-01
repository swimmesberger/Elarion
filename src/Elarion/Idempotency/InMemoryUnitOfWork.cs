using Elarion.Abstractions.Pipeline;

namespace Elarion.Idempotency;

/// <summary>
/// A no-op <see cref="IUnitOfWork"/> for single-instance dev/test hosts with no database transaction. Commit
/// and rollback do nothing; the in-memory store manages its own dedup state and honors abandon/complete
/// explicitly. The durable EF Core unit of work (opt-in package) replaces this with a real transaction.
/// </summary>
internal sealed class InMemoryUnitOfWork : IUnitOfWork {
    /// <inheritdoc />
    public ValueTask<IUnitOfWorkScope> BeginAsync(UnitOfWorkOptions options, CancellationToken ct) =>
        new(NoopScope.Instance);

    private sealed class NoopScope : IUnitOfWorkScope {
        public static readonly NoopScope Instance = new();

        public ValueTask CommitAsync(CancellationToken ct) => default;
        public ValueTask RollbackAsync(CancellationToken ct) => default;
        public ValueTask CreateSavepointAsync(string name, CancellationToken ct) => default;
        public ValueTask RollbackToSavepointAsync(string name, CancellationToken ct) => default;
        public ValueTask DisposeAsync() => default;
    }
}
