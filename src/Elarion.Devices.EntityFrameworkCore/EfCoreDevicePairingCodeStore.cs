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
    private static readonly ConcurrentDictionary<IModel, string> SupersedeSqlCache = new();
    private static readonly ConcurrentDictionary<IModel, string> ClaimSqlCache = new();

    /// <inheritdoc />
    public async ValueTask<bool> TryReplaceAsync(PairingCodeEntry entry, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(entry);
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TDbContext>();
        var insertSql = InsertSqlCache.GetOrAdd(
            dbContext.Model,
            static (_, context) => DeviceIdentityEntitySql.BuildCodeInsertSql(context),
            dbContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        // Serialize reissues for one device across nodes. The pairing-code table deliberately cannot
        // make DeviceId unique (the new hash must be inserted before old codes are removed to preserve
        // collision safety), so this transaction-scoped PostgreSQL lock is the per-device fence.
        await dbContext.Database.ExecuteSqlRawAsync(
            "SELECT pg_advisory_xact_lock(hashtextextended({0}, 0))", [entry.DeviceId], cancellationToken)
            .ConfigureAwait(false);
        // Insert first: when the astronomically unlikely code-hash collision occurs, DO NOTHING and
        // roll back without touching the device's currently valid code.
        var inserted = await dbContext.Database.ExecuteSqlRawAsync(
            insertSql, [entry.CodeHash, entry.DeviceId, entry.ExpiresAt, timeProvider.GetUtcNow()], cancellationToken)
            .ConfigureAwait(false);
        if (inserted != 1) {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        var supersedeSql = SupersedeSqlCache.GetOrAdd(
            dbContext.Model,
            static (_, context) => DeviceIdentityEntitySql.BuildCodeSupersedeSql(context),
            dbContext);
        await dbContext.Database.ExecuteSqlRawAsync(supersedeSql, [entry.DeviceId, entry.CodeHash], cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public async ValueTask<int> RevokeAsync(string deviceId, CancellationToken cancellationToken = default) {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TDbContext>();
        return await dbContext.Set<DevicePairingCodeEntity>()
            .Where(entity => entity.DeviceId == deviceId)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
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
