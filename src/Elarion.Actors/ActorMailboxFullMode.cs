namespace Elarion.Actors;

/// <summary>
/// Behaviour of a facade call when the target actor's bounded mailbox is full.
/// </summary>
/// <remarks>
/// Drop modes (as found in classic actor frameworks) are deliberately absent: every facade call is
/// request/reply, so silently dropping a queued call would leave the caller awaiting forever.
/// Backpressure therefore either waits or fails fast.
/// </remarks>
public enum ActorMailboxFullMode {
    /// <summary>The caller asynchronously waits for mailbox space (default).</summary>
    Wait = 0,

    /// <summary>The call fails immediately with an <see cref="ActorMailboxFullException"/>.</summary>
    Fail = 1
}
