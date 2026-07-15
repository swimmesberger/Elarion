namespace Elarion.Actors;

/// <summary>
/// The single-homing seam (ADR-0048): a cluster-wide lease that elects exactly one instance as the
/// <b>actor home</b> — the only instance allowed to activate <c>[Actor(Placement = ActorPlacementMode.SingleHome)]</c>
/// actors. When no lease is registered, single-homing is declared but not enforced (the local-dev /
/// single-instance case); registering an implementation (e.g.
/// <c>AddElarionPostgreSqlActorHome&lt;TDbContext&gt;()</c>) turns the declaration into a gate.
/// The default implementation is a view over a named
/// <see cref="Elarion.Abstractions.Coordination.IRoleLease"/> (ADR-0049) bound via
/// <c>AddElarionActorHome("actors")</c> — the generic leader-election primitive is the role lease;
/// this interface only pins which role the actor runtime follows.
/// </summary>
/// <remarks>
/// Holdership is time-bounded and self-renewing; <see cref="IsHeld"/> must answer from local state
/// (no I/O — it is consulted on the actor call path) and must turn <see langword="false"/> before
/// the underlying lease can have expired for another instance (a safety margin inside the lease
/// duration). Brief double-holding during failover is tolerated by design: snapshot ETags plus the
/// transparent conflict retry (ADR-0047) make the overlap loud and lossless rather than corrupting.
/// </remarks>
public interface IActorHomeLease {
    /// <summary>The coarse role that homes these actors.</summary>
    string Role { get; }

    /// <summary>Whether this instance currently holds the actor home role.</summary>
    bool IsHeld { get; }

    /// <summary>
    /// The instance currently believed to hold the role (for diagnostics/errors), or
    /// <see langword="null"/> when unknown.
    /// </summary>
    string? CurrentHolder { get; }
}
