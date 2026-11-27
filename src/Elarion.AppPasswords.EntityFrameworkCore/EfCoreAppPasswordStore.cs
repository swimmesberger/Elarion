using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Elarion.AppPasswords.EntityFrameworkCore;

/// <summary>
/// The EF-backed <see cref="IAppPasswordStore"/> over a <typeparamref name="TDbContext"/> whose model includes
/// <see cref="AppPasswordEntity"/> through <c>[GenerateElarionAppPasswords]</c> or <c>UseElarionAppPasswords</c>.
/// </summary>
/// <remarks>
/// A singleton that opens a fresh DI scope, and so its own context, per operation: verification runs inside
/// authentication, before any handler, and creating a password must not save the caller's tracked changes. The
/// per-user cap is a count and an insert in one transaction; on PostgreSQL a transaction-scoped advisory lock on the
/// user serializes concurrent creations across nodes, on SQLite the database's write lock does. The last-use write and
/// revocation are single conditional statements.
/// </remarks>
public sealed class EfCoreAppPasswordStore<TDbContext>(IServiceScopeFactory scopeFactory) : IAppPasswordStore
    where TDbContext : DbContext {
    private const string NpgsqlProvider = "Npgsql.EntityFrameworkCore.PostgreSQL";

    /// <summary>The advisory-lock key text for one user's app passwords.</summary>
    internal static string UserLockKey(string userId) {
        return "elarion.app_passwords:" + userId;
    }

    /// <inheritdoc />
    public async ValueTask<bool> TryAddAsync(AppPasswordRecord record, int maxPerUser,
        CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(record);
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TDbContext>();
        await using var transaction =
            await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (string.Equals(dbContext.Database.ProviderName, NpgsqlProvider, StringComparison.Ordinal))
            await dbContext.Database.ExecuteSqlRawAsync(
                    "SELECT pg_advisory_xact_lock(hashtextextended({0}, 0))", [UserLockKey(record.UserId)],
                    cancellationToken)
                .ConfigureAwait(false);

        var held = await dbContext.Set<AppPasswordEntity>()
            .CountAsync(entity => entity.UserId == record.UserId, cancellationToken)
            .ConfigureAwait(false);
        if (held >= maxPerUser) {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        dbContext.Set<AppPasswordEntity>().Add(new AppPasswordEntity {
            Id = record.Id,
            UserId = record.UserId,
            Name = record.Name,
            Scope = record.Scope.Value,
            Hash = record.Hash,
            CreatedAt = record.CreatedAt,
            LastUsedAt = record.LastUsedAt,
        });
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<AppPasswordInfo>> ListAsync(string userId,
        CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(userId);
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TDbContext>();
        var rows = await dbContext.Set<AppPasswordEntity>()
            .AsNoTracking()
            .Where(entity => entity.UserId == userId)
            .OrderBy(entity => entity.CreatedAt)
            .ThenBy(entity => entity.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.Select(row => ToRecord(row, AppPasswordScope.Parse(row.Scope)).ToInfo()).ToArray();
    }

    /// <inheritdoc />
    public async ValueTask<bool> RemoveAsync(string userId, Guid id, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(userId);
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TDbContext>();
        var deleted = await dbContext.Set<AppPasswordEntity>()
            .Where(entity => entity.Id == id && entity.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
        return deleted > 0;
    }

    /// <inheritdoc />
    public async ValueTask<AppPasswordRecord?> FindByHashAsync(string hash,
        CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(hash);
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TDbContext>();
        var row = await dbContext.Set<AppPasswordEntity>()
            .AsNoTracking()
            .FirstOrDefaultAsync(entity => entity.Hash == hash, cancellationToken)
            .ConfigureAwait(false);
        // A row whose scope this version cannot read never authenticates: fail closed rather than guess.
        return row is not null && AppPasswordScope.TryParse(row.Scope, out var parsed) ? ToRecord(row, parsed) : null;
    }

    /// <inheritdoc />
    public async ValueTask TouchAsync(Guid id, DateTimeOffset usedAt, DateTimeOffset staleBefore,
        CancellationToken cancellationToken = default) {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TDbContext>();
        DateTimeOffset? value = usedAt;
        await dbContext.Set<AppPasswordEntity>()
            .Where(entity => entity.Id == id && (entity.LastUsedAt == null || entity.LastUsedAt < staleBefore))
            .ExecuteUpdateAsync(setters => setters.SetProperty(entity => entity.LastUsedAt, value), cancellationToken)
            .ConfigureAwait(false);
    }

    private static AppPasswordRecord ToRecord(AppPasswordEntity row, AppPasswordScope scope) {
        return new AppPasswordRecord {
            Id = row.Id,
            UserId = row.UserId,
            Name = row.Name,
            Scope = scope,
            Hash = row.Hash,
            CreatedAt = row.CreatedAt,
            LastUsedAt = row.LastUsedAt,
        };
    }
}
