using Elarion.Abstractions.Coordination;
using Elarion.Actors;
using Elarion.Coordination.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Elarion.Actors.PostgreSql;

/// <summary>Registers the fixed virtual-shard actor placement recipe over PostgreSQL role leases.</summary>
public static class ActorShardingServiceCollectionExtensions {
    /// <summary>
    /// Registers one role lease and heartbeat per virtual shard, plus the actor placement resolver.
    /// The default has 16 virtual shards and does not require the process count up front. Each
    /// process can hold any number of shards; there is no rebalancing or activation forwarding.
    /// <typeparamref name="TDbContext"/> must map the role lease table — annotate the context with
    /// <c>[GenerateElarionRoleLeases]</c> or call <c>modelBuilder.UseElarionRoleLeases()</c>.
    /// </summary>
    public static IServiceCollection AddElarionPostgreSqlActorSharding<TDbContext>(
        this IServiceCollection services,
        Action<PostgreSqlActorShardingOptions>? configure = null)
        where TDbContext : DbContext {
        ArgumentNullException.ThrowIfNull(services);

        var options = new PostgreSqlActorShardingOptions();
        configure?.Invoke(options);
        Validate(options);

        // One process should present one stable identity across all of its shard roles. Individual
        // role registration still remains independent, so several roles may be held together.
        var instanceId = $"{Environment.MachineName}:{Guid.CreateVersion7():N}";
        for (var shard = 0; shard < options.VirtualShardCount; shard++) {
            var roleName = GetRoleName(options.RolePrefix, shard);
            var leaseOptions = new RoleLeaseOptions {
                RoleName = roleName,
                InstanceId = instanceId
            };
            options.ConfigureLease?.Invoke(leaseOptions);
            // RoleName is topology, not per-role tuning; do not let a callback change the key.
            leaseOptions.RoleName = roleName;
            services.AddElarionPostgreSqlRoleLease<TDbContext>(leaseOptions);
        }

        services.RemoveAll<IActorPlacementResolver>();
        services.AddSingleton<IActorPlacementResolver>(serviceProvider => {
            var leases = new IRoleLease[options.VirtualShardCount];
            for (var shard = 0; shard < leases.Length; shard++) {
                leases[shard] = serviceProvider.GetRequiredKeyedService<IRoleLease>(
                    GetRoleName(options.RolePrefix, shard));
            }

            return new PostgreSqlActorPlacementResolver(leases);
        });
        return services;
    }

    internal static string GetRoleName(string rolePrefix, int shard) => $"{rolePrefix}:shard-{shard}";

    private static void Validate(PostgreSqlActorShardingOptions options) {
        if (options.VirtualShardCount <= 0) {
            throw new ArgumentOutOfRangeException(
                nameof(options.VirtualShardCount),
                options.VirtualShardCount,
                "VirtualShardCount must be greater than zero.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(options.RolePrefix);
        if (options.RolePrefix.EndsWith(':')) {
            throw new ArgumentException(
                "RolePrefix must not end with ':'; shard names append ':shard-N'.",
                nameof(options.RolePrefix));
        }
    }
}
