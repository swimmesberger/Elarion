using System.Globalization;
using System.Linq.Expressions;
using Elarion.Abstractions.Authorization;
using Microsoft.EntityFrameworkCore;

namespace Elarion.Authorization.EntityFrameworkCore;

/// <summary>
/// The filter-backed point check of one entity type: a resource is authorized when the row with that key is among the
/// rows the entity's <see cref="IQueryAuthorizer{TEntity}"/> lets the caller access for the operation — one
/// <c>EXISTS</c> over the same predicate the list query uses, so the two cannot disagree.
/// </summary>
/// <remarks>
/// A row that does not exist (or that the set's query filters hide) is denied like one the rule hides, which is what
/// makes <c>[RequireResource(DenyAs = ResourceDenial.NotFound)]</c> indistinguishable from a missing record. An
/// unrestricted caller is still asked whether the row exists, for the same reason.
/// </remarks>
internal sealed class FilterResourceAuthorizer<TDbContext, TEntity, TKey>(
    TDbContext dbContext,
    IQueryAuthorizer<TEntity> authorizer,
    Expression<Func<TEntity, TKey>> key,
    Func<IQueryable<TEntity>, IQueryable<TEntity>>? rows) : IResourceAuthorizer
    where TDbContext : DbContext
    where TEntity : class
    where TKey : notnull {
    public async ValueTask<bool> AuthorizeResourceAsync(ResourceAuthorizationContext context, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(context);

        // No id, or one that cannot be this entity's key, names no row: deny rather than guess.
        if (context.ResourceId is null || !ResourceKey.TryConvert<TKey>(context.ResourceId, out var id)) return false;

        IQueryable<TEntity> candidates = dbContext.Set<TEntity>();
        if (rows is not null) candidates = rows(candidates);

        return await candidates
            .WhereAuthorized(authorizer, context.User, context.Operation)
            .AnyAsync(ResourceKey.Matches(key, id), ct)
            .ConfigureAwait(false);
    }
}

/// <summary>Converts a request's resource id to an entity key and builds the key comparison.</summary>
internal static class ResourceKey {
    /// <summary>
    /// The id as <typeparamref name="TKey"/>: the value itself when it already has that type, a string parsed with the
    /// invariant culture for the key types a request commonly carries as text, or the invariant text of the value for
    /// a string key. Anything else is not a key of this entity.
    /// </summary>
    public static bool TryConvert<TKey>(object id, out TKey key)
        where TKey : notnull {
        if (id is TKey typed) {
            key = typed;
            return true;
        }

        if (id is string text) {
            if (typeof(TKey) == typeof(Guid) && Guid.TryParse(text, out var guid)) {
                key = (TKey)(object)guid;
                return true;
            }

            if (typeof(TKey) == typeof(int) &&
                int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)) {
                key = (TKey)(object)number;
                return true;
            }

            if (typeof(TKey) == typeof(long) &&
                long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var wide)) {
                key = (TKey)(object)wide;
                return true;
            }
        }
        else if (typeof(TKey) == typeof(long) && id is int narrow) {
            key = (TKey)(object)(long)narrow;
            return true;
        }
        else if (typeof(TKey) == typeof(string) &&
                 Convert.ToString(id, CultureInfo.InvariantCulture) is { Length: > 0 } formatted) {
            key = (TKey)(object)formatted;
            return true;
        }

        key = default!;
        return false;
    }

    /// <summary><c>row =&gt; key(row) == id</c>, with the id as a captured value so the provider sends a parameter.</summary>
    public static Expression<Func<TEntity, bool>> Matches<TEntity, TKey>(Expression<Func<TEntity, TKey>> key, TKey id) {
        Expression<Func<TKey>> value = () => id;
        return Expression.Lambda<Func<TEntity, bool>>(Expression.Equal(key.Body, value.Body), key.Parameters);
    }
}
