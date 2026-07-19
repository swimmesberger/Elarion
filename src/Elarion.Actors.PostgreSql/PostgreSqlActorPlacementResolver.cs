using Elarion.Abstractions.Coordination;

namespace Elarion.Actors.PostgreSql;

/// <summary>Maps the stable actor key hash to the corresponding PostgreSQL role lease.</summary>
internal sealed class PostgreSqlActorPlacementResolver(IRolePartition partition)
    : IActorPlacementResolver {
    public ActorPlacementResolution Resolve(string actorName, string key) {
        var target = partition.Resolve(actorName, key);
        return new ActorPlacementResolution(
            target.IsHeld,
            target.CurrentHolder,
            target.CurrentHolderAddress,
            target.Role);
    }
}
