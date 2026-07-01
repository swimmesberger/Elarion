using System.Globalization;
using Elarion.Abstractions.Pipeline;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Elarion.EntityFrameworkCore.UnitOfWork;

/// <summary>
/// The EF Core <see cref="IUnitOfWork"/>: opens a real database transaction on <typeparamref name="TDbContext"/>
/// so a decorator can commit the handler's writes atomically. Provider-aware — on PostgreSQL it applies the
/// requested <see cref="UnitOfWorkOptions.LockTimeout"/> via <c>SET LOCAL lock_timeout</c> so a concurrent
/// idempotency claim fast-fails to a 409 rather than blocking; other providers ignore the timeout.
/// </summary>
/// <remarks>
/// The scope <b>joins</b> an already-open ambient transaction rather than failing: when a transactional command
/// invokes another transactional command through <c>IHandlerSender</c> on the same scope/<c>DbContext</c>, the
/// inner <see cref="BeginAsync"/> sees the outer transaction and returns a savepoint-backed nested scope
/// (savepoint on begin, release on commit, roll-back-to-savepoint on rollback) so the two never contend on a
/// second physical transaction — the outer scope owns the real commit.
/// </remarks>
public sealed class EfUnitOfWork<TDbContext>(TDbContext dbContext) : IUnitOfWork
    where TDbContext : DbContext {
    private const string NpgsqlProviderName = "Npgsql.EntityFrameworkCore.PostgreSQL";

    // Monotonic per-context counter so nested savepoint names never collide when a command chain nests more
    // than one level deep on the same DbContext.
    private int _nestingDepth;

    /// <inheritdoc />
    public async ValueTask<IUnitOfWorkScope> BeginAsync(UnitOfWorkOptions options, CancellationToken ct) {
        if (dbContext.Database.CurrentTransaction is { } ambient) {
            // A transaction is already open on this DbContext (a nested transactional handler invoked through
            // IHandlerSender on the same scope). Join it with a savepoint instead of opening a second physical
            // transaction, which the provider would reject.
            var savepoint = "elarion_uow_nested_" + Interlocked.Increment(ref _nestingDepth)
                .ToString(CultureInfo.InvariantCulture);
            await ambient.CreateSavepointAsync(savepoint, ct).ConfigureAwait(false);
            return new NestedUnitOfWorkScope(dbContext, ambient, savepoint);
        }

        var transaction = await dbContext.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        if (options.LockTimeout is { } lockTimeout && IsPostgres(dbContext)) {
            // lock_timeout takes a value in milliseconds when no unit is given. `SET LOCAL` scopes it to this
            // transaction. The value is a bounded integer we compute, not user input.
            var milliseconds = (int)Math.Max(0, Math.Round(lockTimeout.TotalMilliseconds));
            var sql = "SET LOCAL lock_timeout = " + milliseconds.ToString(CultureInfo.InvariantCulture);
            await dbContext.Database.ExecuteSqlRawAsync(sql, ct).ConfigureAwait(false);
        }

        return new EfUnitOfWorkScope(dbContext, transaction);
    }

    private static bool IsPostgres(DbContext context) =>
        string.Equals(context.Database.ProviderName, NpgsqlProviderName, StringComparison.Ordinal);

    /// <summary>
    /// The root scope that owns the physical transaction. Commit flushes the change tracker first (so a handler
    /// that forgot <c>SaveChangesAsync</c> still persists its writes atomically with the transaction — a no-op
    /// when it already saved) and then commits.
    /// </summary>
    private sealed class EfUnitOfWorkScope(TDbContext dbContext, IDbContextTransaction transaction) : IUnitOfWorkScope {
        public async ValueTask CommitAsync(CancellationToken ct) {
            await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }

        public ValueTask RollbackAsync(CancellationToken ct) => new(transaction.RollbackAsync(ct));

        public ValueTask CreateSavepointAsync(string name, CancellationToken ct) =>
            new(transaction.CreateSavepointAsync(name, ct));

        public ValueTask RollbackToSavepointAsync(string name, CancellationToken ct) =>
            new(transaction.RollbackToSavepointAsync(name, ct));

        public ValueTask DisposeAsync() => transaction.DisposeAsync();
    }

    /// <summary>
    /// A nested scope that joins the ambient transaction via a savepoint. It never commits or rolls back the
    /// physical transaction (the outer scope owns that): commit flushes the handler's writes and releases the
    /// savepoint, rollback discards only this nested handler's writes by rolling back to the savepoint.
    /// </summary>
    private sealed class NestedUnitOfWorkScope(
        TDbContext dbContext,
        IDbContextTransaction transaction,
        string savepoint) : IUnitOfWorkScope {
        private bool _completed;

        public async ValueTask CommitAsync(CancellationToken ct) {
            await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
            await transaction.ReleaseSavepointAsync(savepoint, ct).ConfigureAwait(false);
            _completed = true;
        }

        public async ValueTask RollbackAsync(CancellationToken ct) {
            await transaction.RollbackToSavepointAsync(savepoint, ct).ConfigureAwait(false);
            _completed = true;
        }

        public ValueTask CreateSavepointAsync(string name, CancellationToken ct) =>
            new(transaction.CreateSavepointAsync(name, ct));

        public ValueTask RollbackToSavepointAsync(string name, CancellationToken ct) =>
            new(transaction.RollbackToSavepointAsync(name, ct));

        public async ValueTask DisposeAsync() {
            if (!_completed) {
                // Dispose without an explicit commit rolls back this nested unit of work only — never the
                // ambient transaction the outer scope still owns.
                await transaction.RollbackToSavepointAsync(savepoint, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }
}
