using Elarion.Abstractions.Auditing;
using Elarion.Auditing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Elarion.Auditing.EntityFrameworkCore;

/// <summary>
/// Wires the durable EF Core audit trail over <typeparamref name="TDbContext"/> (ADR-0045): the sink, the
/// audit scope, automatic change capture, and the opt-in retention worker. The host maps the table in
/// <c>OnModelCreating</c> (via <c>[GenerateElarionAuditing]</c> or <c>UseElarionAuditing</c>) and owns the
/// migration; handlers opt in with <c>[Auditable]</c> (or <c>[assembly: ElarionAuditDefaults]</c>).
/// </summary>
public static class AuditingEntityFrameworkCoreServiceCollectionExtensions {
    /// <summary>
    /// Registers the EF Core audit trail: the <see cref="EfCoreAuditTrail{TDbContext}"/> sink, the scoped audit
    /// scope, the change-capture interceptor with the default change-tracker contributor, and — when
    /// <see cref="AuditRetentionOptions.RetainFor"/> is configured — the retention purge worker.
    /// </summary>
    /// <remarks>
    /// The default contributor is an additive <c>TryAddEnumerable</c> registration: add specialists with
    /// <see cref="AddElarionAuditChangeContributor{TContributor}"/>, or remove the default's
    /// <see cref="IAuditChangeContributor"/> descriptor for action-records-only auditing — the composition
    /// levers are plain DI (ADR-0045).
    /// </remarks>
    public static IServiceCollection AddElarionAuditingEntityFrameworkCore<TDbContext>(
        this IServiceCollection services,
        Action<AuditRetentionOptions>? configure = null)
        where TDbContext : DbContext {
        ArgumentNullException.ThrowIfNull(services);

        // Core building blocks (the scoped AuditScope the decorators and the interceptor share).
        services.AddElarionAuditing();

        var options = new AuditRetentionOptions();
        configure?.Invoke(options);
        services.TryAddSingleton(options);

        services.TryAddScoped<IAuditTrail, EfCoreAuditTrail<TDbContext>>();

        // Change capture: the scoped interceptor is attached to the context through the options-configuration
        // seam (no manual AddInterceptors), and the default change-tracker contributor is additive.
        services.TryAddScoped<AuditSaveChangesInterceptor>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDbContextOptionsConfiguration<TDbContext>,
            AuditingDbContextOptionsConfiguration<TDbContext>>());
        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<IAuditChangeContributor, ChangeTrackerAuditChangeContributor>());

        if (options.RetainFor is not null) {
            services.AddHostedService<AuditRetentionService<TDbContext>>();
        }

        return services;
    }

    /// <summary>
    /// Adds a custom <see cref="IAuditChangeContributor"/> alongside the default change-tracker contributor.
    /// Contributions are additive; registration order is deterministic but carries no semantics.
    /// </summary>
    public static IServiceCollection AddElarionAuditChangeContributor<TContributor>(this IServiceCollection services)
        where TContributor : class, IAuditChangeContributor {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddEnumerable(ServiceDescriptor.Scoped<IAuditChangeContributor, TContributor>());
        return services;
    }
}
