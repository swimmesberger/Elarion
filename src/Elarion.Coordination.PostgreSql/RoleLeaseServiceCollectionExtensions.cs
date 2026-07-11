using Elarion.Abstractions.Coordination;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Elarion.Coordination.PostgreSql;

/// <summary>Registers PostgreSQL role leases (ADR-0049).</summary>
public static class RoleLeaseServiceCollectionExtensions {
    /// <summary>
    /// Registers one heartbeat-renewed role lease: an <see cref="IRoleLease"/> keyed by
    /// <see cref="RoleLeaseOptions.RoleName"/> plus its renewal hosted service. Call once per role.
    /// <typeparamref name="TDbContext"/> must map <see cref="RoleLeaseEntity"/> — annotate the
    /// context with <c>[GenerateElarionRoleLeases]</c> or call
    /// <c>modelBuilder.UseElarionRoleLeases()</c>.
    /// </summary>
    public static IServiceCollection AddElarionPostgreSqlRoleLease<TDbContext>(
        this IServiceCollection services,
        Action<RoleLeaseOptions> configure)
        where TDbContext : DbContext {
        ArgumentNullException.ThrowIfNull(configure);
        var options = new RoleLeaseOptions();
        configure(options);
        return services.AddElarionPostgreSqlRoleLease<TDbContext>(options);
    }

    /// <summary>Registers one heartbeat-renewed role lease from pre-built options.</summary>
    public static IServiceCollection AddElarionPostgreSqlRoleLease<TDbContext>(
        this IServiceCollection services,
        RoleLeaseOptions options)
        where TDbContext : DbContext {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.RoleName);
        if (options.LeaseDuration <= options.RenewInterval + options.HeldSafetyMargin) {
            throw new ArgumentException(
                $"RoleLeaseOptions.LeaseDuration for role '{options.RoleName}' must exceed RenewInterval + "
                + "HeldSafetyMargin, or the holder would flap between renewals.");
        }

        // Two leases for one role in one process would compete against themselves (distinct random
        // InstanceIds) — a wiring bug, surfaced at registration.
        if (services.Any(descriptor => descriptor.ServiceType == typeof(IRoleLease)
                                       && descriptor.IsKeyedService
                                       && Equals(descriptor.ServiceKey, options.RoleName))) {
            throw new InvalidOperationException(
                $"A role lease for '{options.RoleName}' is already registered; register each role once.");
        }

        services.TryAddSingleton(TimeProvider.System);
        services.AddKeyedSingleton<IRoleLease>(options.RoleName, (serviceProvider, _) =>
            new PostgreSqlRoleLease<TDbContext>(
                serviceProvider.GetRequiredService<IServiceScopeFactory>(),
                options,
                serviceProvider.GetRequiredService<TimeProvider>(),
                serviceProvider.GetRequiredService<ILogger<PostgreSqlRoleLease<TDbContext>>>(),
                serviceProvider.GetService<IInstanceAddressProvider>()));
        services.AddSingleton<IHostedService>(serviceProvider => new RoleLeaseHeartbeatService<TDbContext>(
            (PostgreSqlRoleLease<TDbContext>)serviceProvider.GetRequiredKeyedService<IRoleLease>(options.RoleName),
            options,
            serviceProvider.GetRequiredService<TimeProvider>(),
            serviceProvider.GetRequiredService<ILogger<RoleLeaseHeartbeatService<TDbContext>>>()));
        return services;
    }
}
