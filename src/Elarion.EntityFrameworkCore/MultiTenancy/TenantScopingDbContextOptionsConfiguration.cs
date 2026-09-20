using Elarion.Abstractions.MultiTenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Elarion.EntityFrameworkCore.MultiTenancy;

/// <summary>
/// Binds the scope's <see cref="ITenantContext"/> to every <typeparamref name="TContext"/> instance: it
/// attaches the stamping interceptor and the options extension the query filter reads, so the host calls
/// neither <c>AddInterceptors</c> nor anything else by hand.
/// </summary>
/// <remarks>
/// Entity Framework Core applies every registered <see cref="IDbContextOptionsConfiguration{TContext}"/> when
/// it builds the context options, passing the context's own (scoped) service provider — which is what makes the
/// options, and therefore the tenant, per-scope. Consequently tenant scoping requires <c>AddDbContext</c>:
/// <c>AddDbContextPool</c> builds its options once for the pool, so a pooled context would serve every scope
/// the first one's tenant. That would be a silent cross-tenant leak, so the failure is made loud at startup.
/// </remarks>
/// <typeparam name="TContext">The application's context.</typeparam>
internal sealed class TenantScopingDbContextOptionsConfiguration<TContext> : IDbContextOptionsConfiguration<TContext>
    where TContext : DbContext {
    public void Configure(IServiceProvider serviceProvider, DbContextOptionsBuilder optionsBuilder) {
        var tenantContext = serviceProvider.GetService<ITenantContext>()
                            ?? throw new InvalidOperationException(
                                $"Tenant scoping is registered for '{typeof(TContext).Name}' but no "
                                + "ITenantContext is available. Call AddElarionTenantScoping() (from Elarion) "
                                + "to register the tenant context and its resolver, or register your own "
                                + "ITenantContext implementation.");

        optionsBuilder.AddInterceptors(new TenantScopeSaveChangesInterceptor(tenantContext));
        ((IDbContextOptionsBuilderInfrastructure)optionsBuilder)
            .AddOrUpdateExtension(new TenantScopeOptionsExtension(tenantContext));
    }
}
