namespace Elarion.Actors;

/// <summary>
/// Thrown when a single-homed or virtual-sharded actor is called on an instance that does not own
/// the relevant role lease. Calls are gated locally; the runtime never forwards actor methods.
/// </summary>
public sealed class ActorNotHomedException(
    string actorName,
    string key,
    string? currentHolder,
    string? placementRole = null,
    string? currentHolderAddress = null)
    : InvalidOperationException(BuildMessage(actorName, key, currentHolder, placementRole)) {
    /// <summary>The actor's logical name.</summary>
    public string ActorName { get; } = actorName;

    /// <summary>The activation key's canonical text.</summary>
    public string Key { get; } = key;

    /// <summary>The instance currently believed to hold the home lease, when known.</summary>
    public string? CurrentHolder { get; } = currentHolder;

    /// <summary>The role that owns the actor key, when the failure came from virtual sharding.</summary>
    public string? PlacementRole { get; } = placementRole;

    /// <summary>The holder's advertised address, when the placement provider knows it.</summary>
    public string? CurrentHolderAddress { get; } = currentHolderAddress;

    private static string BuildMessage(
        string actorName,
        string key,
        string? currentHolder,
        string? placementRole) {
        var leaseDescription = placementRole is null
            ? "the actor home lease"
            : $"virtual-shard role '{placementRole}'";
        var holder = currentHolder is null ? "elsewhere." : $"by '{currentHolder}'.";
        var routeHint = placementRole is null
            ? " Route the triggering work to the home instance or read state via IActorStateReader."
            : " Route the triggering work to the shard holder; actor calls are never forwarded.";
        return $"Actor '{actorName}' ({key}) cannot run on this instance: {leaseDescription} is held {holder}"
            + routeHint;
    }
}
