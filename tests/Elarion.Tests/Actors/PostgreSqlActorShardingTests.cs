using AwesomeAssertions;
using Elarion.Actors;
using Elarion.Actors.PostgreSql;
using Elarion.Abstractions.Coordination;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elarion.Tests.Actors;

public sealed class PostgreSqlActorShardingTests {
    [Fact]
    public void RegistersOneRolePerVirtualShardAndResolvesTheStableRole() {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddElarionPostgreSqlActorSharding<ShardingDbContext>(options => {
            options.VirtualShardCount = 3;
            options.RolePrefix = "actors";
        });

        var roleKeys = services
            .Where(static descriptor => descriptor.ServiceType == typeof(IRoleLease) && descriptor.IsKeyedService)
            .Select(static descriptor => descriptor.ServiceKey)
            .ToArray();
        roleKeys.Should().BeEquivalentTo(new object?[] {
            "actors:partition-0", "actors:partition-1", "actors:partition-2"
        });

        using var provider = services.BuildServiceProvider();
        var resolver = provider.GetRequiredService<IActorPlacementResolver>();
        var shard = ActorVirtualShard.GetShardIndex("Order", "42", 3);

        resolver.Resolve("Order", "42").Role.Should().Be($"actors:partition-{shard}");

        provider.GetRequiredKeyedService<IRolePartition>("actors").PartitionCount.Should().Be(3);
    }

    private sealed class ShardingDbContext(DbContextOptions<ShardingDbContext> options) : DbContext(options);
}
