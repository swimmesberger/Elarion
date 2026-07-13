using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;

namespace Elarion.Devices.EntityFrameworkCore;

/// <summary>
/// EF-backed <see cref="IPairingCodeStore"/> over a <typeparamref name="TDbContext"/> whose model
/// includes <see cref="DevicePairingCodeEntity"/> via <c>UseElarionDeviceIdentity</c>.
/// </summary>
/// <remarks>
/// A singleton that opens a fresh DI scope per operation (redeems arrive on anonymous provisioning
/// endpoints, outside any handler scope). The claim is one <c>DELETE … RETURNING</c> statement, so
/// the primary-key constraint — not an external lock — makes concurrent redeems single-winner
/// across nodes. Expired rows are swept by <see cref="DeleteExpiredAsync"/>; schedule it with a
/// <c>[ScheduledJob]</c> or run it opportunistically.
/// </remarks>
public sealed class EfCoreDevicePairingCodeStore<TDbContext>(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider) : IPairingCodeStore
    where TDbContext : DbContext {
    private static readonly ConcurrentDictionary<IModel, string> InsertSqlCache = new();
    private static readonly ConcurrentDictionary<IModel, string> ClaimSqlCache = new();

    /// <inheritdoc />
    public async ValueTask<bool> TryCreateAsync(PairingCodeEntry entry, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(entry);
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TDbContext>();
        var sql = InsertSqlCache.GetOrAdd(
            dbContext.Model,
            static (_, context) => DeviceIdentityEntitySql.BuildCodeInsertSql(context),
            dbContext);
        var inserted = await dbContext.Database
            .ExecuteSqlRawAsync(
                sql,
                [entry.CodeHash, entry.DeviceId, entry.ExpiresAt, timeProvider.GetUtcNow()],
                cancellationToken)
            .ConfigureAwait(false);
        return inserted == 1;
    }

    /// <inheritdoc />
    public async ValueTask<PairingCodeEntry?> ClaimAsync(string codeHash, DateTimeOffset now, CancellationToken cancellationToken = default) {
        ArgumentException.ThrowIfNullOrEmpty(codeHash);
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TDbContext>();
        var sql = ClaimSqlCache.GetOrAdd(
            dbContext.Model,
            static (_, context) => DeviceIdentityEntitySql.BuildCodeClaimSql(context),
            dbContext);
        // Materialized without further LINQ composition: the raw DELETE … RETURNING must execute
        // as the top-level statement, never be wrapped in a subquery.
        var claimed = await dbContext.Database
            .SqlQueryRaw<ClaimedPairingCodeRow>(sql, codeHash, now)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return claimed is [var row]
            ? new PairingCodeEntry { CodeHash = row.CodeHash, DeviceId = row.DeviceId, ExpiresAt = row.ExpiresOnUtc }
            : null;
    }

    /// <inheritdoc />
    public async ValueTask<int> DeleteExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken = default) {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TDbContext>();
        return await dbContext.Set<DevicePairingCodeEntity>()
            .Where(entity => entity.ExpiresOnUtc <= now)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}

/// <summary>The claim statement's result shape (aliases in the RETURNING clause match by name).</summary>
internal sealed class ClaimedPairingCodeRow {
    public string CodeHash { get; set; } = "";

    public string DeviceId { get; set; } = "";

    public DateTimeOffset ExpiresOnUtc { get; set; }
}
