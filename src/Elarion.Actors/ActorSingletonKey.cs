namespace Elarion.Actors;

/// <summary>
/// The key type used internally for singleton actors (one activation per process). Appears in
/// generated registrations; application code never constructs or passes it.
/// </summary>
public readonly record struct ActorSingletonKey {
    /// <summary>The single value.</summary>
    public static ActorSingletonKey Value => default;

    /// <inheritdoc />
    public override string ToString() => "singleton";
}
