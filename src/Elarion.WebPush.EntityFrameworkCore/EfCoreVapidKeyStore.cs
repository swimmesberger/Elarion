using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;

namespace Elarion.WebPush.EntityFrameworkCore;

/// <summary>
/// EF-backed <see cref="IVapidKeyStore"/> over a <typeparamref name="TDbContext"/> whose model includes
/// <see cref="VapidKeyEntity"/> via <c>UseElarionWebPush</c>.
/// </summary>
/// <remarks>
/// The first-use race is settled by the primary key: every node inserts its candidate with
/// <c>ON CONFLICT DO NOTHING</c> and then reads the row back, so all of them adopt the single winner.
/// </remarks>
public sealed class EfCoreVapidKeyStore<TDbContext>(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider) : IVapidKeyStore
    where TDbContext : DbContext {
    private static readonly ConcurrentDictionary<IModel, string> InsertSqlCache = new();

    /// <inheritdoc />
    public async ValueTask<VapidKeys?> GetAsync(CancellationToken cancellationToken = default) {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TDbContext>();
        return await ReadAsync(dbContext, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<VapidKeys> GetOrAddAsync(VapidKeys candidate, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(candidate);
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TDbContext>();
        var sql = InsertSqlCache.GetOrAdd(
            dbContext.Model,
            static (_, context) => WebPushEntitySql.BuildVapidKeyInsertSql(context),
            dbContext);
        await dbContext.Database.ExecuteSqlRawAsync(
                sql,
                [VapidKeyEntity.DefaultName, candidate.PublicKey, candidate.PrivateKey, timeProvider.GetUtcNow()],
                cancellationToken)
            .ConfigureAwait(false);
        return await ReadAsync(dbContext, cancellationToken).ConfigureAwait(false)
               ?? throw new InvalidOperationException("The VAPID key row vanished right after it was inserted.");
    }

    private static Task<VapidKeys?> ReadAsync(TDbContext dbContext, CancellationToken cancellationToken) {
        return dbContext.Set<VapidKeyEntity>()
            .AsNoTracking()
            .Where(entity => entity.Name == VapidKeyEntity.DefaultName)
            .Select(entity => (VapidKeys?)new VapidKeys { PublicKey = entity.PublicKey, PrivateKey = entity.PrivateKey })
            .FirstOrDefaultAsync(cancellationToken);
    }
}
