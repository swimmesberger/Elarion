using Elarion.Abstractions.MultiTenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Elarion.MultiTenancy;

/// <summary>
/// Registers the transport-neutral half of ambient tenancy: the scoped <see cref="ITenantContext"/> and the
/// claim-based <see cref="ITenantResolver"/> default. The persistence half — the read filter and the write
/// stamp — is registered separately by the provider package (for Entity Framework Core,
/// <c>AddElarionTenantScopingEntityFrameworkCore&lt;TDbContext&gt;</c>).
/// </summary>
public static class TenantScopingServiceCollectionExtensions {
    /// <summary>
    /// Adds the scoped <see cref="ITenantContext"/> and the claim-based <see cref="ITenantResolver"/>.
    /// </summary>
    /// <remarks>
    /// Both use <c>TryAdd</c>, so an application that registers its own resolver (a membership lookup, a host
    /// header) or its own context before calling this keeps it. Nothing here touches a database or a
    /// transport, so it is safe in any host.
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional configuration of the claim the default resolver reads.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddElarionTenantScoping(
        this IServiceCollection services,
        Action<TenantScopingOptions>? configure = null) {
        ArgumentNullException.ThrowIfNull(services);

        var options = new TenantScopingOptions();
        configure?.Invoke(options);
        services.TryAddSingleton(options);

        services.TryAddScoped<ITenantResolver, ClaimsTenantResolver>();
        services.TryAddScoped<ITenantContext, TenantContext>();
        return services;
    }
}
