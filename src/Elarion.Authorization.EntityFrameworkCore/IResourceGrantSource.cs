using Microsoft.EntityFrameworkCore;

namespace Elarion.Authorization.EntityFrameworkCore;

/// <summary>
/// Exposes the resource-grants set as an <see cref="IQueryable{T}"/> from the application's
/// <c>DbContext</c>. A generated <c>[ResourceFilter(Shared = true)]</c> authorizer injects this and references
/// <see cref="Grants"/> inside its predicate, so the shared-grant check becomes a correlated <c>EXISTS</c>
/// subquery in the same SQL statement as the list query.
/// </summary>
public interface IResourceGrantSource {
    /// <summary>The resource-grants set, from the same scoped context the query runs on.</summary>
    IQueryable<ResourceGrantEntity> Grants { get; }
}

/// <summary>The default <see cref="IResourceGrantSource"/> over the application's <typeparamref name="TDbContext"/>.</summary>
/// <typeparam name="TDbContext">The context whose model includes <see cref="ResourceGrantEntity"/>.</typeparam>
internal sealed class DbContextResourceGrantSource<TDbContext>(TDbContext dbContext) : IResourceGrantSource
    where TDbContext : DbContext {
    public IQueryable<ResourceGrantEntity> Grants => dbContext.Set<ResourceGrantEntity>();
}
