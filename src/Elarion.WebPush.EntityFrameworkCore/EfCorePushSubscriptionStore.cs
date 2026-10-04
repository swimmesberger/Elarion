using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Elarion.WebPush.EntityFrameworkCore;

/// <summary>
/// EF-backed <see cref="IPushSubscriptionStore"/> over a <typeparamref name="TDbContext"/> whose model includes
/// <see cref="PushSubscriptionEntity"/> via <c>UseElarionWebPush</c>.
/// </summary>
/// <remarks>
/// <para>
/// Scoped, and it works on the caller's own context: every statement runs on that context's connection, so
/// inside a unit of work (a command handler) a subscribe, an unsubscribe or a dead-subscription cleanup joins
/// the transaction and commits or rolls back with the caller's other writes; outside one it commits on its
/// own. A second connection would not just break that atomicity — on SQLite, whose single write lock the
/// caller's open transaction already holds, its write would wait for that lock until the busy timeout.
/// </para>
/// <para>
/// Writes are change-tracker-free — the upsert is one <c>INSERT … ON CONFLICT (endpoint) DO UPDATE</c>, removal
/// an <c>ExecuteDelete</c> — so they neither flush nor disturb what the caller has tracked. Like the context,
/// the store is not safe for concurrent use; the sender calls it sequentially.
/// </para>
/// </remarks>
public sealed class EfCorePushSubscriptionStore<TDbContext>(TDbContext dbContext) : IPushSubscriptionStore
    where TDbContext : DbContext {
    // Provider- and schema-specific (delimited identifiers, resolved column names), so built once per model.
    private static readonly ConcurrentDictionary<IModel, string> UpsertSqlCache = new();

    /// <inheritdoc />
    public async ValueTask UpsertAsync(PushSubscription subscription, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(subscription);
        var sql = UpsertSqlCache.GetOrAdd(
            dbContext.Model,
            static (_, context) => WebPushEntitySql.BuildSubscriptionUpsertSql(context),
            dbContext);
        var parameters = WebPushEntitySql.Parameters(dbContext, typeof(PushSubscriptionEntity),
            (nameof(PushSubscriptionEntity.Id), Guid.CreateVersion7()),
            (nameof(PushSubscriptionEntity.Endpoint), subscription.Endpoint),
            (nameof(PushSubscriptionEntity.P256dh), subscription.P256dh),
            (nameof(PushSubscriptionEntity.Auth), subscription.Auth),
            (nameof(PushSubscriptionEntity.UserId), subscription.UserId),
            (nameof(PushSubscriptionEntity.UserAgent), string.IsNullOrEmpty(subscription.UserAgent) ? null : subscription.UserAgent),
            (nameof(PushSubscriptionEntity.CreatedOnUtc), subscription.CreatedAt),
            (nameof(PushSubscriptionEntity.LastSeenOnUtc), subscription.LastSeenAt));
        await dbContext.Database.ExecuteSqlRawAsync(sql, parameters, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<bool> RemoveAsync(string endpoint, string? userId = null,
        CancellationToken cancellationToken = default) {
        ArgumentException.ThrowIfNullOrEmpty(endpoint);
        var query = dbContext.Set<PushSubscriptionEntity>().Where(entity => entity.Endpoint == endpoint);
        if (userId is not null) query = query.Where(entity => entity.UserId == userId);
        return await query.ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<PushSubscription>> ListByUsersAsync(IReadOnlyCollection<string> userIds,
        CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(userIds);
        if (userIds.Count == 0) return [];
        var owners = userIds.Distinct(StringComparer.Ordinal).ToList();
        return await dbContext.Set<PushSubscriptionEntity>()
            .AsNoTracking()
            .Where(entity => owners.Contains(entity.UserId))
            .Select(entity => new PushSubscription {
                Endpoint = entity.Endpoint,
                P256dh = entity.P256dh,
                Auth = entity.Auth,
                UserId = entity.UserId,
                UserAgent = entity.UserAgent,
                CreatedAt = entity.CreatedOnUtc,
                LastSeenAt = entity.LastSeenOnUtc
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<string>> ListSubscribedUserIdsAsync(IReadOnlyCollection<string>? among = null,
        CancellationToken cancellationToken = default) {
        var query = dbContext.Set<PushSubscriptionEntity>().AsNoTracking().Select(entity => entity.UserId);
        if (among is not null) {
            if (among.Count == 0) return [];
            var owners = among.Distinct(StringComparer.Ordinal).ToList();
            query = query.Where(userId => owners.Contains(userId));
        }

        // Ordinal order is applied in memory: a database collation would order differently per provider.
        var ids = await query.Distinct().ToListAsync(cancellationToken).ConfigureAwait(false);
        ids.Sort(StringComparer.Ordinal);
        return ids;
    }
}
