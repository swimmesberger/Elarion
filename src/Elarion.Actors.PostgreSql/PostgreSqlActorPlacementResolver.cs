using Elarion.Abstractions.Coordination;

namespace Elarion.Actors.PostgreSql;

/// <summary>Maps the stable actor key hash to the corresponding PostgreSQL role lease.</summary>
internal sealed class PostgreSqlActorPlacementResolver(IReadOnlyList<IRoleLease> leases)
    : IActorPlacementResolver {
    public ActorPlacementResolution Resolve(string actorName, string key) {
        var shard = ActorVirtualShard.GetShardIndex(actorName, key, leases.Count);
        var lease = leases[shard];
        return new(
            lease.IsHeld,
            lease.CurrentHolder,
            lease.CurrentHolderAddress,
            lease.Role);
    }
}
