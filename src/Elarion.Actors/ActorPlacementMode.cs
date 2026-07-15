namespace Elarion.Actors;

/// <summary>Controls where an actor may activate.</summary>
public enum ActorPlacementMode {
    /// <summary>The actor is local to the current process (the default).</summary>
    Local,

    /// <summary>The actor runs only on the instance holding the single actor-home role.</summary>
    SingleHome,

    /// <summary>
    /// The actor key is assigned to one of a fixed set of virtual-shard role leases. This mode is
    /// opt-in and requires a registered <see cref="IActorPlacementResolver"/> to be enforced.
    /// </summary>
    VirtualShards
}
