using Elarion.Abstractions.Pipeline;
using Microsoft.Extensions.Logging;

namespace Elarion.Idempotency;

/// <summary>
/// A no-op <see cref="IUnitOfWork"/> for single-instance dev/test hosts with no database transaction. Commit
/// and rollback do nothing; the in-memory store manages its own dedup state and honors abandon/complete
/// explicitly. The durable EF Core unit of work (opt-in package) replaces this with a real transaction.
/// </summary>
internal sealed class InMemoryUnitOfWork(ILogger<InMemoryUnitOfWork>? logger = null) : IUnitOfWork {
    private int _warned;

    /// <inheritdoc />
    public ValueTask<IUnitOfWorkScope> BeginAsync(UnitOfWorkOptions options, CancellationToken ct) {
        WarnOnce();
        return new(NoopScope.Instance);
    }

    private void WarnOnce() {
        if (logger is null || Interlocked.Exchange(ref _warned, 1) != 0) {
            return;
        }

        logger.LogWarning(
            "The in-memory (no-op) IUnitOfWork is in use: commit and rollback do nothing, so a failed command's " +
            "writes are NOT rolled back. It is for single-process dev/test only. Call " +
            "AddElarionUnitOfWork<TDbContext>() from Elarion.EntityFrameworkCore.UnitOfWork for a real " +
            "database transaction in production.");
    }

    private sealed class NoopScope : IUnitOfWorkScope {
        public static readonly NoopScope Instance = new();

        public ValueTask CommitAsync(CancellationToken ct) => default;
        public ValueTask RollbackAsync(CancellationToken ct) => default;
        public ValueTask CreateSavepointAsync(string name, CancellationToken ct) => default;
        public ValueTask RollbackToSavepointAsync(string name, CancellationToken ct) => default;
        public ValueTask DisposeAsync() => default;
    }
}
