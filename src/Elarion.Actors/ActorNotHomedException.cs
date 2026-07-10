namespace Elarion.Actors;

/// <summary>
/// Thrown when a <c>[Actor(SingleHomed = true)]</c> actor is called on an instance that does not
/// hold the actor home lease (ADR-0048). Single-homed actors are fed by work that lands on the home
/// instance — integration events (gate the outbox delivery worker on the lease) or calls made on
/// the home itself; reads that may run anywhere go through <see cref="IActorStateReader"/> or a
/// regular handler instead of the facade.
/// </summary>
public sealed class ActorNotHomedException(string actorName, string key, string? currentHolder)
    : InvalidOperationException(
        $"Single-homed actor '{actorName}' ({key}) cannot run on this instance: the actor home lease is held "
        + (currentHolder is null ? "elsewhere." : $"by '{currentHolder}'.")
        + " Route the triggering work to the home instance (e.g. gate outbox delivery on the lease) or read "
        + "state via IActorStateReader.") {
    /// <summary>The actor's logical name.</summary>
    public string ActorName { get; } = actorName;

    /// <summary>The activation key's canonical text.</summary>
    public string Key { get; } = key;

    /// <summary>The instance currently believed to hold the home lease, when known.</summary>
    public string? CurrentHolder { get; } = currentHolder;
}
