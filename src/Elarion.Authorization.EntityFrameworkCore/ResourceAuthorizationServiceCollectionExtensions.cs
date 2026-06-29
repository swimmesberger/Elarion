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
    /// core fail-closed default). The context must map <see cref="ResourceGrantEntity"/> (via
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
}
