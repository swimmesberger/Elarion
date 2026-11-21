using System.Linq.Expressions;
using Elarion.Abstractions.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Elarion.Authorization.EntityFrameworkCore;

/// <summary>Registers the EF Core resource-grants backend for data-level authorization.</summary>
public static class ResourceAuthorizationServiceCollectionExtensions {
    /// <summary>
    /// Registers the resource-grants source and store over <typeparamref name="TDbContext"/>, and the
    /// grants-backed <see cref="IResourceAuthorizer"/> for <c>[RequireResource]</c> point checks (replacing the
    /// core fail-closed default). A resource type with a filter check
    /// (<see cref="AddElarionResourceFilterCheck{TDbContext,TEntity,TKey}"/>) is still answered by that check. The context must map <see cref="ResourceGrantEntity"/> (via
    /// <c>[GenerateElarionResourceGrants]</c> or a hand-written <c>ApplyElarionResourceGrants</c> call). Named
    /// consistently with <c>AddElarionIdentity</c> / <c>AddElarionOutbox</c> / <c>AddElarionSettings</c>.
    /// </summary>
    /// <typeparam name="TDbContext">The context whose model includes <see cref="ResourceGrantEntity"/>.</typeparam>
    public static IServiceCollection AddElarionResourceAuthorization<TDbContext>(this IServiceCollection services)
        where TDbContext : DbContext {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<IResourceGrantSource, DbContextResourceGrantSource<TDbContext>>();
        services.TryAddScoped<IResourceGrantStore, EfCoreResourceGrantStore<TDbContext>>();
        // Replace the core fail-closed default so it wins regardless of registration order.
        services.Replace(ServiceDescriptor.Scoped<IResourceAuthorizer, GrantResourceAuthorizer>());

        return services;
    }

    /// <summary>
    /// Makes <c>[RequireResource(typeof(<typeparamref name="TEntity"/>))]</c> — and an injected
    /// <see cref="IResourceAuthorizer"/> asked about that type — check a resource through the entity's registered
    /// <see cref="IQueryAuthorizer{TEntity}"/>: the caller is authorized when the row with that key is among the rows
    /// the filter lets them access for the operation. The point check and the list filter are then one rule, whatever
    /// it is composed of (owner, sharing, bypass, application-specific alternatives).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The check is one <c>EXISTS</c> over <typeparamref name="TDbContext"/>'s set, so a row that does not exist is
    /// denied like a hidden one; declare <c>DenyAs = ResourceDenial.NotFound</c> to answer both as a missing record.
    /// The requirement's operation is passed to the filter, so a rule that is narrower for writing
    /// (<see cref="QueryAuthorizers.ForOperations{TEntity}"/>) is enforced by
    /// <c>[RequireResource(Operation = "update")]</c> without the handler repeating it.
    /// </para>
    /// <para>
    /// The set's query filters apply — a soft-deleted row is denied, and ambient tenant scoping still holds. Pass
    /// <paramref name="rows"/> to check a different set, for example
    /// <c>rows =&gt; rows.IgnoreQueryFilters(["SoftDelete"])</c> when a hidden record must stay authorized for the
    /// accounts that could see it (a restore command).
    /// </para>
    /// <para>
    /// Needs the core authorization runtime (<c>AddElarionAuthorization</c>); the grants table is <b>not</b> required.
    /// The request's id may be the key type itself or, for <see cref="Guid"/>, <see cref="int"/> and
    /// <see cref="long"/> keys, its invariant text. Registering the same entity again replaces the earlier check.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="key">The entity's key (<c>c =&gt; c.Id</c>), matched against the id the request names.</param>
    /// <param name="rows">Shapes the set the check runs on; <see langword="null"/> uses the context's set as it is.</param>
    /// <typeparam name="TDbContext">The context the entity is read from.</typeparam>
    /// <typeparam name="TEntity">The resource type, as <c>[RequireResource]</c> names it.</typeparam>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <exception cref="ArgumentException"><typeparamref name="TKey"/> has no equality operator a query can use.</exception>
    /// <example>
    /// <code>
    /// services.AddElarionResourceFilterCheck&lt;AppDbContext, Contact, Guid&gt;(c =&gt; c.Id);
    ///
    /// [RequireResource(typeof(Contact), Operation = "update", Id = nameof(Command.Id), DenyAs = ResourceDenial.NotFound)]
    /// public sealed class UpdateContact { /* ... */ }
    /// </code>
    /// </example>
    public static IServiceCollection AddElarionResourceFilterCheck<TDbContext, TEntity, TKey>(
        this IServiceCollection services,
        Expression<Func<TEntity, TKey>> key,
        Func<IQueryable<TEntity>, IQueryable<TEntity>>? rows = null)
        where TDbContext : DbContext
        where TEntity : class
        where TKey : notnull {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(key);

        // Built once here so a key type a query cannot compare fails at startup, not on the first guarded request.
        try {
            _ = ResourceKey.Matches(key, default(TKey)!);
        }
        catch (InvalidOperationException exception) {
            throw new ArgumentException(
                $"'{typeof(TKey)}' cannot be compared with ==, so it cannot be the key of a resource check.",
                nameof(key),
                exception);
        }

        services.RemoveAllKeyed<IResourceAuthorizer>(typeof(TEntity));
        services.AddKeyedScoped<IResourceAuthorizer>(
            typeof(TEntity),
            (provider, _) => new FilterResourceAuthorizer<TDbContext, TEntity, TKey>(
                provider.GetRequiredService<TDbContext>(),
                provider.GetRequiredService<IQueryAuthorizer<TEntity>>(),
                key,
                rows));
        return services;
    }
}
