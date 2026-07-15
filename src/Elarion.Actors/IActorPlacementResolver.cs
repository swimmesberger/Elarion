namespace Elarion.Actors;

/// <summary>
/// Resolves the virtual-shard home for one actor key. The resolver is deliberately provider-neutral:
/// the in-memory runtime only asks the local, already-cached answer and never performs I/O on the
/// actor call path.
/// </summary>
public interface IActorPlacementResolver {
    /// <summary>Returns the current local ownership view for an actor name and canonical key.</summary>
    ActorPlacementResolution Resolve(string actorName, string key);
}

/// <summary>The local ownership view returned by <see cref="IActorPlacementResolver"/>.</summary>
public readonly record struct ActorPlacementResolution(
    bool IsHeld,
    string? CurrentHolder,
    string? CurrentHolderAddress,
    string Role);
