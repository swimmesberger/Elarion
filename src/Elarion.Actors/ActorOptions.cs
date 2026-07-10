namespace Elarion.Actors;

/// <summary>
/// Per-actor runtime options, normally produced by the registration generator from
/// <see cref="ActorAttribute"/> knobs.
/// </summary>
public sealed class ActorOptions {
    /// <summary>Runtime default for <see cref="IdleTimeout"/>.</summary>
    public static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromMinutes(5);

    /// <summary>Runtime default for <see cref="CallTimeout"/>.</summary>
    public static readonly TimeSpan DefaultCallTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Bounded mailbox capacity; <see langword="null"/> (default) is unbounded.</summary>
    public int? MailboxCapacity { get; init; }

    /// <summary>Behaviour when a bounded mailbox is full. Ignored for unbounded mailboxes.</summary>
    public ActorMailboxFullMode MailboxFullMode { get; init; } = ActorMailboxFullMode.Wait;

    /// <summary>
    /// Inactivity window after which an activation is passivated; <see langword="null"/> keeps the
    /// activation alive for the process lifetime.
    /// </summary>
    public TimeSpan? IdleTimeout { get; init; } = DefaultIdleTimeout;

    /// <summary>
    /// End-to-end facade call timeout (queue wait + execution) after which the call fails with a
    /// <see cref="TimeoutException"/> — the deadlock backstop for actor→actor call cycles.
    /// <see langword="null"/> disables the timeout.
    /// </summary>
    public TimeSpan? CallTimeout { get; init; } = DefaultCallTimeout;

    /// <summary>Orleans-style turn interleaving (see <see cref="ReentrantAttribute"/>).</summary>
    public bool Reentrant { get; init; }

    /// <summary>
    /// Whether this actor only runs on the instance holding the actor home lease (ADR-0048);
    /// enforced when an <see cref="IActorHomeLease"/> is registered.
    /// </summary>
    public bool SingleHomed { get; init; }
}
