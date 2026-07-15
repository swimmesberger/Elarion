namespace Elarion.Actors;

/// <summary>
/// Marks a plain class as an actor: a long-lived, stateful instance whose public async methods are
/// invoked through a generated typed facade and executed sequentially by a mailbox, so instance
/// state is never mutated concurrently and needs no locks.
/// </summary>
/// <remarks>
/// <para>
/// The actor registration generator emits, per <c>[Actor]</c> class, a public facade interface
/// (<c>I{Name}</c>, with a trailing <c>Actor</c> suffix stripped from the class name) mirroring the
/// class's public async methods, an internal facade implementation that enqueues each call as a
/// mailbox work item, and a per-module <c>Add{Module}Actors</c> registration wired into the module's
/// <c>ConfigureDefaultServices</c> (so a disabled module's actors disappear like its handlers).
/// Callers resolve facades through <see cref="IActorSystem"/>.
/// </para>
/// <para>
/// An actor is <em>keyed</em> when its constructor takes an <see cref="IActorContext{TKey}"/> (one
/// activation per key, Orleans grain style — activated on first message, passivated after
/// <see cref="IdleTimeoutSeconds"/>) or when <see cref="KeyType"/> is set; otherwise it is a
/// singleton. Constructor parameters other than the context are resolved from a dedicated DI scope
/// that lives for the activation.
/// </para>
/// <para>
/// Execution is single-threaded per activation: one message runs start-to-finish before the next
/// (an <c>await</c> inside a method holds the mailbox). Opt into Orleans-style turn interleaving
/// with <see cref="ReentrantAttribute"/>.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class ActorAttribute : Attribute {
    /// <summary>
    /// Logical actor name used in telemetry, logging, and the generated registration. Defaults to
    /// the class name with a trailing <c>Actor</c> suffix stripped.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Explicit key type for a keyed actor whose constructor does not take an
    /// <see cref="IActorContext{TKey}"/>. Usually unnecessary — declaring the context parameter is
    /// the idiomatic way to make an actor keyed.
    /// </summary>
    public Type? KeyType { get; init; }

    /// <summary>
    /// Bounded mailbox capacity. <c>0</c> (default) means unbounded. When bounded and full,
    /// <see cref="MailboxFullMode"/> decides whether senders wait or fail.
    /// </summary>
    public int MailboxCapacity { get; init; }

    /// <summary>Behaviour when a bounded mailbox is full. Ignored for unbounded mailboxes.</summary>
    public ActorMailboxFullMode MailboxFullMode { get; init; } = ActorMailboxFullMode.Wait;

    /// <summary>
    /// Seconds of inactivity after which the activation is passivated (deactivated and dropped; the
    /// next message re-activates it). <c>0</c> (default) uses the runtime default of 5 minutes;
    /// <c>-1</c> disables passivation for this actor.
    /// </summary>
    public double IdleTimeoutSeconds { get; init; }

    /// <summary>
    /// Seconds a facade call may take end-to-end (queue wait + execution) before it fails with a
    /// <see cref="TimeoutException"/> — the deadlock backstop for actor→actor call cycles.
    /// <c>0</c> (default) uses the runtime default of 30 seconds; <c>-1</c> disables the timeout.
    /// </summary>
    public double CallTimeoutSeconds { get; init; }

    /// <summary>
    /// Placement mode for this actor. <see cref="ActorPlacementMode.Local"/> is the default;
    /// <see cref="ActorPlacementMode.SingleHome"/> uses the actor-home role lease, while
    /// <see cref="ActorPlacementMode.VirtualShards"/> assigns each key to a fixed virtual-shard
    /// role lease when a placement provider is registered.
    /// </summary>
    public ActorPlacementMode Placement { get; init; }
}
