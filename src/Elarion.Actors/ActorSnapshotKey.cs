namespace Elarion.Actors;

/// <summary>
/// Identifies one actor's snapshot in an <see cref="IActorSnapshotStore"/>: the actor's logical
/// name plus the activation key's canonical text (<c>Key.ToString()</c>; <c>"singleton"</c> for
/// singleton actors). Key types must therefore have a stable, culture-independent
/// <c>ToString()</c> — <see cref="Guid"/>, <see cref="string"/>, and integral keys all qualify.
/// </summary>
/// <param name="ActorName">The actor's logical name (the facade name, e.g. <c>OrderFulfillment</c>).</param>
/// <param name="Key">The activation key rendered as text.</param>
public readonly record struct ActorSnapshotKey(string ActorName, string Key);
