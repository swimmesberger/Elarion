using Elarion.Abstractions.Auditing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Elarion.Auditing;

/// <summary>Registers the audit scope building blocks (ADR-0045).</summary>
public static class AuditingServiceCollectionExtensions {
    /// <summary>
    /// Registers the scoped <see cref="AuditScope"/>/<see cref="IAuditScope"/>. Idempotent, and safe to call in
    /// a host without an <see cref="IAuditTrail"/>: the audit decorators only attach when a trail is registered,
    /// so the scope simply stays inactive and handler writes into it are ignored. Sink packages (e.g.
    /// <c>AddElarionAuditingEntityFrameworkCore</c>) call this for you.
    /// </summary>
    public static IServiceCollection AddElarionAuditing(this IServiceCollection services) {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<AuditScope>();
        services.TryAddScoped<IAuditScope>(static sp => sp.GetRequiredService<AuditScope>());
        return services;
    }
}
