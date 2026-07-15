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
    /// Registers a fixed Coordination role partition plus the actor placement resolver.
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

        services.AddElarionPostgreSqlRolePartition<TDbContext>(partition => {
            partition.Name = options.RolePrefix;
            partition.PartitionCount = options.VirtualShardCount;
            partition.ConfigureLease = options.ConfigureLease;
        });

        services.RemoveAll<IActorPlacementResolver>();
        services.AddSingleton<IActorPlacementResolver>(serviceProvider =>
            new PostgreSqlActorPlacementResolver(
                serviceProvider.GetRequiredKeyedService<IRolePartition>(options.RolePrefix)));
        return services;
    }

    internal static string GetRoleName(string rolePrefix, int shard) =>
        RoleLeaseServiceCollectionExtensions.GetPartitionRole(rolePrefix, shard);

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
                "RolePrefix must not end with ':'; partition names append ':partition-N'.",
                nameof(options.RolePrefix));
        }
    }
}
