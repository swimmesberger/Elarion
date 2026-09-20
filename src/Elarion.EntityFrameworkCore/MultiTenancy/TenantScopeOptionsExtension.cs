using Elarion.Abstractions.MultiTenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Elarion.EntityFrameworkCore.MultiTenancy;

/// <summary>
/// Carries the scope's <see cref="ITenantContext"/> on the context's options, so a model-level query filter can
/// read the current tenant.
/// </summary>
/// <remarks>
/// <para>
/// The model is a process-wide cached artifact and a query filter is part of it, so the filter cannot close
/// over a scoped service directly: it would pin the first scope's tenant for the lifetime of the process. What
/// it <i>can</i> reference is the executing <c>DbContext</c> — Entity Framework Core substitutes the live
/// instance into the filter on every query — so the scoped value is reached through the context's own options,
/// which <see cref="TenantScopingDbContextOptionsConfiguration{TContext}"/> rebuilds per scope.
/// </para>
/// <para>
/// The extension contributes nothing to the service-provider or model cache keys: it changes no mapping and no
/// provider behavior, only which value the already-baked filter reads.
/// </para>
/// </remarks>
internal sealed class TenantScopeOptionsExtension(ITenantContext tenantContext) : IDbContextOptionsExtension {
    private DbContextOptionsExtensionInfo? _info;

    public ITenantContext TenantContext { get; } = tenantContext;

    public DbContextOptionsExtensionInfo Info => _info ??= new ExtensionInfo(this);

    public void ApplyServices(IServiceCollection services) {
    }

    public void Validate(IDbContextOptions options) {
    }

    /// <summary>
    /// The tenant context attached to <paramref name="context"/>, or <see langword="null"/> when the host has
    /// not registered tenant scoping for it.
    /// </summary>
    internal static ITenantContext? FindTenantContext(DbContext context) {
        return context.GetService<IDbContextOptions>().FindExtension<TenantScopeOptionsExtension>()?.TenantContext;
    }

    private sealed class ExtensionInfo(TenantScopeOptionsExtension extension)
        : DbContextOptionsExtensionInfo(extension) {
        public override bool IsDatabaseProvider => false;

        public override string LogFragment => "using Elarion tenant scoping ";

        public override int GetServiceProviderHashCode() {
            return 0;
        }

        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other) {
            return other is ExtensionInfo;
        }

        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo) {
            debugInfo["Elarion:TenantScoping"] = "1";
        }
    }
}
