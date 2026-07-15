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
    /// Registers a fixed virtual partition backed by one PostgreSQL role lease per partition.
    /// Processes are not configured up front and may own any number of partitions.
    /// </summary>
    public static IServiceCollection AddElarionPostgreSqlRolePartition<TDbContext>(
        this IServiceCollection services,
        Action<RolePartitionOptions> configure)
        where TDbContext : DbContext {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new RolePartitionOptions { Name = string.Empty };
        configure(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Name);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.PartitionCount);
        if (options.Name.EndsWith(':')) {
            throw new ArgumentException("Role partition names must not end with ':'.", nameof(configure));
        }

        var instanceId = $"{Environment.MachineName}:{Guid.CreateVersion7():N}";
        for (var partition = 0; partition < options.PartitionCount; partition++) {
            var role = GetPartitionRole(options.Name, partition);
            var leaseOptions = new RoleLeaseOptions {
                RoleName = role,
                InstanceId = instanceId
            };
            options.ConfigureLease?.Invoke(leaseOptions);
            leaseOptions.RoleName = role;
            leaseOptions.InstanceId = instanceId;
            services.AddElarionPostgreSqlRoleLease<TDbContext>(leaseOptions);
        }

        if (services.Any(descriptor => descriptor.ServiceType == typeof(IRolePartition)
                                       && descriptor.IsKeyedService
                                       && Equals(descriptor.ServiceKey, options.Name))) {
            throw new InvalidOperationException($"A role partition named '{options.Name}' is already registered.");
        }

        services.AddKeyedSingleton<IRolePartition>(options.Name, (serviceProvider, _) => {
            var leases = new IRoleLease[options.PartitionCount];
            for (var partition = 0; partition < leases.Length; partition++) {
                leases[partition] = serviceProvider.GetRequiredKeyedService<IRoleLease>(
                    GetPartitionRole(options.Name, partition));
            }

            return new PostgreSqlRolePartition(options.Name, leases);
        });
        return services;
    }

    /// <summary>Returns the stable role name for one partition.</summary>
    public static string GetPartitionRole(string name, int partition) => $"{name}:partition-{partition}";

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
        services.TryAddSingleton<RoleLeaseRegistry>();
        services.TryAddSingleton<IRoleLeaseRegistry>(serviceProvider =>
            serviceProvider.GetRequiredService<RoleLeaseRegistry>());
        services.AddKeyedSingleton<IRoleLease>(options.RoleName, (serviceProvider, _) => {
            var lease = new PostgreSqlRoleLease<TDbContext>(
                serviceProvider.GetRequiredService<IServiceScopeFactory>(),
                options,
                serviceProvider.GetRequiredService<TimeProvider>(),
                serviceProvider.GetRequiredService<ILogger<PostgreSqlRoleLease<TDbContext>>>(),
                serviceProvider.GetService<IInstanceAddressProvider>());
            serviceProvider.GetRequiredService<RoleLeaseRegistry>().Add(lease);
            return lease;
        });
        services.AddSingleton<IHostedService>(serviceProvider => new RoleLeaseHeartbeatService<TDbContext>(
            (PostgreSqlRoleLease<TDbContext>)serviceProvider.GetRequiredKeyedService<IRoleLease>(options.RoleName),
            options,
            serviceProvider.GetRequiredService<TimeProvider>(),
            serviceProvider.GetRequiredService<ILogger<RoleLeaseHeartbeatService<TDbContext>>>()));
        return services;
    }
}
