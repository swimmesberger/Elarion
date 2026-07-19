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

            string? restoreLockTimeout = null;
            if (options.LockTimeout is { } nestedLockTimeout && IsPostgres(dbContext)) {
                // The requested timeout must bound the nested scope's lock waits too (a nested [Idempotent]
                // command must not block unbounded on the claim row while holding the outer scope's locks).
                // SET LOCAL persists to the end of the PHYSICAL transaction, so capture the ambient value first:
                // the commit path restores it when this nested scope exits. (The rollback paths roll back to the
                // savepoint created above, which reverts the SET LOCAL automatically.)
                restoreLockTimeout = await dbContext.Database
                    .SqlQueryRaw<string>("SELECT current_setting('lock_timeout') AS \"Value\"")
                    .SingleAsync(ct)
                    .ConfigureAwait(false);
                await ApplyLockTimeoutAsync(nestedLockTimeout, ct).ConfigureAwait(false);
            }

            return new NestedUnitOfWorkScope(dbContext, ambient, savepoint, restoreLockTimeout);
        }

        var transaction = await dbContext.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        if (options.LockTimeout is { } lockTimeout && IsPostgres(dbContext))
            await ApplyLockTimeoutAsync(lockTimeout, ct).ConfigureAwait(false);

        return new EfUnitOfWorkScope(dbContext, transaction);
    }

    private async ValueTask ApplyLockTimeoutAsync(TimeSpan lockTimeout, CancellationToken ct) {
        // lock_timeout takes a value in milliseconds when no unit is given. `SET LOCAL` scopes it to the
        // current transaction. The value is a bounded integer we compute, not user input.
        var milliseconds = (int)Math.Max(0, Math.Round(lockTimeout.TotalMilliseconds));
        var sql = "SET LOCAL lock_timeout = " + milliseconds.ToString(CultureInfo.InvariantCulture);
        await dbContext.Database.ExecuteSqlRawAsync(sql, ct).ConfigureAwait(false);
    }

    private static bool IsPostgres(DbContext context) {
        return string.Equals(context.Database.ProviderName, NpgsqlProviderName, StringComparison.Ordinal);
    }

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

        public ValueTask RollbackAsync(CancellationToken ct) {
            return new ValueTask(transaction.RollbackAsync(ct));
        }

        public ValueTask CreateSavepointAsync(string name, CancellationToken ct) {
            return new ValueTask(transaction.CreateSavepointAsync(name, ct));
        }

        public ValueTask RollbackToSavepointAsync(string name, CancellationToken ct) {
            return new ValueTask(transaction.RollbackToSavepointAsync(name, ct));
        }

        public ValueTask DisposeAsync() {
            return transaction.DisposeAsync();
        }
    }

    /// <summary>
    /// A nested scope that joins the ambient transaction via a savepoint. It never commits or rolls back the
    /// physical transaction (the outer scope owns that): commit flushes the handler's writes and releases the
    /// savepoint, rollback discards only this nested handler's writes by rolling back to the savepoint.
    /// When the nested scope applied its own <c>lock_timeout</c>, the commit path restores the captured ambient
    /// value (<c>SET LOCAL</c> would otherwise persist to the end of the physical transaction); the rollback
    /// paths revert it implicitly, because rolling back to a savepoint undoes <c>SET LOCAL</c>s issued after it.
    /// </summary>
    private sealed class NestedUnitOfWorkScope(
        TDbContext dbContext,
        IDbContextTransaction transaction,
        string savepoint,
        string? restoreLockTimeout) : IUnitOfWorkScope {
        private bool _completed;

        public async ValueTask CommitAsync(CancellationToken ct) {
            await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
            await transaction.ReleaseSavepointAsync(savepoint, ct).ConfigureAwait(false);
            if (restoreLockTimeout is not null)
                // Releasing the savepoint keeps this scope's SET LOCAL alive for the rest of the physical
                // transaction — put the ambient value back so the outer scope's statements are unaffected.
                await dbContext.Database.ExecuteSqlRawAsync(
                    "SELECT set_config('lock_timeout', {0}, true)", [restoreLockTimeout], ct).ConfigureAwait(false);

            _completed = true;
        }

        public async ValueTask RollbackAsync(CancellationToken ct) {
            await transaction.RollbackToSavepointAsync(savepoint, ct).ConfigureAwait(false);
            _completed = true;
        }

        public ValueTask CreateSavepointAsync(string name, CancellationToken ct) {
            return new ValueTask(transaction.CreateSavepointAsync(name, ct));
        }

        public ValueTask RollbackToSavepointAsync(string name, CancellationToken ct) {
            return new ValueTask(transaction.RollbackToSavepointAsync(name, ct));
        }

        public async ValueTask DisposeAsync() {
            if (!_completed)
                // Dispose without an explicit commit rolls back this nested unit of work only — never the
                // ambient transaction the outer scope still owns.
                await transaction.RollbackToSavepointAsync(savepoint, CancellationToken.None).ConfigureAwait(false);
        }
    }
}
