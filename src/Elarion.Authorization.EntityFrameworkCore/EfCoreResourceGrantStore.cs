using Elarion.Abstractions.Authorization;
using Microsoft.EntityFrameworkCore;

namespace Elarion.Authorization.EntityFrameworkCore;

/// <summary>
/// The EF Core backend for <see cref="IResourceGrantStore"/> over the application's
/// <typeparamref name="TDbContext"/>. Grants are idempotent (a duplicate share is a no-op); revocation is a
/// change-tracker-free <c>ExecuteDelete</c>.
/// </summary>
/// <typeparam name="TDbContext">The context whose model includes <see cref="ResourceGrantEntity"/>.</typeparam>
internal sealed class EfCoreResourceGrantStore<TDbContext>(TDbContext dbContext) : IResourceGrantStore
    where TDbContext : DbContext {
    public async ValueTask GrantAsync(ResourceGrant grant, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(grant);

        var entity = ToEntity(grant);
        var exists = await dbContext.Set<ResourceGrantEntity>()
            .AnyAsync(MatchesPredicate(entity), ct)
            .ConfigureAwait(false);
        if (exists) {
            return;
        }

        dbContext.Add(entity);
        try {
            await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException) {
            // A concurrent insert won the race; the grant now exists, which is the desired idempotent outcome.
            dbContext.Entry(entity).State = EntityState.Detached;
        }
    }

    public async ValueTask RevokeAsync(ResourceGrant grant, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(grant);

        var entity = ToEntity(grant);
        await dbContext.Set<ResourceGrantEntity>()
            .Where(MatchesPredicate(entity))
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<ResourceGrant>> GetGrantsAsync(
        string resourceType, string resourceId, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(resourceType);
        ArgumentNullException.ThrowIfNull(resourceId);

        var rows = await dbContext.Set<ResourceGrantEntity>()
            .AsNoTracking()
            .Where(grant => grant.ResourceType == resourceType && grant.ResourceId == resourceId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows.Select(ToContract).ToList();
    }

    private static System.Linq.Expressions.Expression<Func<ResourceGrantEntity, bool>> MatchesPredicate(ResourceGrantEntity e)
        => grant => grant.ResourceType == e.ResourceType
            && grant.ResourceId == e.ResourceId
            && grant.PrincipalKind == e.PrincipalKind
            && grant.PrincipalId == e.PrincipalId
            && grant.Operation == e.Operation;

    private static ResourceGrantEntity ToEntity(ResourceGrant grant) => new()
    {
        ResourceType = grant.ResourceType,
        ResourceId = grant.ResourceId,
        PrincipalKind = grant.Principal.Kind,
        PrincipalId = grant.Principal.Id,
        Operation = grant.Operation.Name,
    };

    private static ResourceGrant ToContract(ResourceGrantEntity entity) => new(
        entity.ResourceType,
        entity.ResourceId,
        new ResourcePrincipal(entity.PrincipalKind, entity.PrincipalId),
        new ResourceOperation(entity.Operation));
}
