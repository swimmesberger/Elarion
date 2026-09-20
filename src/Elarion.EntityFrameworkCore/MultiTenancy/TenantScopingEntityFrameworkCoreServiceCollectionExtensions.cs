using Elarion.Abstractions.MultiTenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Elarion.EntityFrameworkCore.MultiTenancy;

/// <summary>
/// Wires ambient tenant scoping to a context: the tenant the model's query filter reads, and the interceptor
/// that stamps and guards writes. The model side is applied by <c>[GenerateElarionTenantScoping]</c> or a
/// direct <c>modelBuilder.ApplyElarionTenantScoping(this)</c> call.
/// </summary>
public static class TenantScopingEntityFrameworkCoreServiceCollectionExtensions {
    /// <summary>
    /// Attaches tenant scoping to <typeparamref name="TDbContext"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The transport-neutral half — the scoped <see cref="ITenantContext"/> and its
    /// <see cref="ITenantResolver"/> — is registered separately by <c>AddElarionTenantScoping()</c> in
    /// <c>Elarion</c>, so an application can replace how a tenant is resolved without touching persistence, and
    /// a host with no database still gets a tenant context. This method fails fast at options-build time if
    /// that registration is missing.
    /// </para>
    /// <para>
    /// Register the context with <c>AddDbContext</c>, not <c>AddDbContextPool</c>: pooling builds the options
    /// once, which would pin every scope to the first scope's tenant.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <typeparam name="TDbContext">The application's context.</typeparam>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddElarionTenantScopingEntityFrameworkCore<TDbContext>(
        this IServiceCollection services)
        where TDbContext : DbContext {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDbContextOptionsConfiguration<TDbContext>,
            TenantScopingDbContextOptionsConfiguration<TDbContext>>());
        return services;
    }
}
