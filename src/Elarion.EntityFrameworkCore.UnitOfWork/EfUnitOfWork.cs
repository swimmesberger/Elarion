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
public sealed class EfUnitOfWork<TDbContext>(TDbContext dbContext) : IUnitOfWork
    where TDbContext : DbContext {
    private const string NpgsqlProviderName = "Npgsql.EntityFrameworkCore.PostgreSQL";

    /// <inheritdoc />
    public async ValueTask<IUnitOfWorkScope> BeginAsync(UnitOfWorkOptions options, CancellationToken ct) {
        var transaction = await dbContext.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        if (options.LockTimeout is { } lockTimeout && IsPostgres(dbContext)) {
            // lock_timeout takes a value in milliseconds when no unit is given. `SET LOCAL` scopes it to this
            // transaction. The value is a bounded integer we compute, not user input.
            var milliseconds = (int)Math.Max(0, Math.Round(lockTimeout.TotalMilliseconds));
            var sql = "SET LOCAL lock_timeout = " + milliseconds.ToString(CultureInfo.InvariantCulture);
            await dbContext.Database.ExecuteSqlRawAsync(sql, ct).ConfigureAwait(false);
        }

        return new EfUnitOfWorkScope(transaction);
    }

    private static bool IsPostgres(DbContext context) =>
        string.Equals(context.Database.ProviderName, NpgsqlProviderName, StringComparison.Ordinal);

    private sealed class EfUnitOfWorkScope(IDbContextTransaction transaction) : IUnitOfWorkScope {
        public ValueTask CommitAsync(CancellationToken ct) => new(transaction.CommitAsync(ct));

        public ValueTask RollbackAsync(CancellationToken ct) => new(transaction.RollbackAsync(ct));

        public ValueTask CreateSavepointAsync(string name, CancellationToken ct) =>
            new(transaction.CreateSavepointAsync(name, ct));

        public ValueTask RollbackToSavepointAsync(string name, CancellationToken ct) =>
            new(transaction.RollbackToSavepointAsync(name, ct));

        public ValueTask DisposeAsync() => transaction.DisposeAsync();
    }
}
