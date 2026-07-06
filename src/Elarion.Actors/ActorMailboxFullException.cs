namespace Elarion.Actors;

/// <summary>
/// Thrown by a facade call when the target actor's bounded mailbox is full and the actor uses
/// <see cref="ActorMailboxFullMode.Fail"/>.
/// </summary>
public sealed class ActorMailboxFullException(string message) : InvalidOperationException(message);
