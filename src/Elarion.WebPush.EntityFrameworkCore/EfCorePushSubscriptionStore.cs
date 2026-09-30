using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;

namespace Elarion.WebPush.EntityFrameworkCore;

/// <summary>
/// EF-backed <see cref="IPushSubscriptionStore"/> over a <typeparamref name="TDbContext"/> whose model includes
/// <see cref="PushSubscriptionEntity"/> via <c>UseElarionWebPush</c>.
/// </summary>
/// <remarks>
/// A singleton that opens a fresh DI scope per operation: subscribes arrive from endpoints and handlers, and
/// the dead-subscription cleanup runs in the middle of a fan-out, neither of which should join a caller's
/// unit of work. Writes are change-tracker-free — the upsert is one <c>INSERT … ON CONFLICT (endpoint) DO
/// UPDATE</c>, removal an <c>ExecuteDelete</c>.
/// </remarks>
public sealed class EfCorePushSubscriptionStore<TDbContext>(IServiceScopeFactory scopeFactory) : IPushSubscriptionStore
    where TDbContext : DbContext {
    // Provider- and schema-specific (delimited identifiers, resolved column names), so built once per model.
    private static readonly ConcurrentDictionary<IModel, string> UpsertSqlCache = new();

    /// <inheritdoc />
    public async ValueTask UpsertAsync(PushSubscription subscription, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(subscription);
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TDbContext>();
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
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TDbContext>();
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
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TDbContext>();
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
}
