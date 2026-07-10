using Elarion.Abstractions.Coordination;

namespace Elarion.Actors.Runtime;

/// <summary>
/// The default <see cref="IActorHomeLease"/>: a view over a registered <see cref="IRoleLease"/>
/// (ADR-0049) — the actor runtime demands <em>the actor home</em>, not "some role", so the mapping
/// from role to home is one explicit registration (<c>AddElarionActorHome("actors")</c>).
/// </summary>
internal sealed class RoleLeaseActorHome(IRoleLease lease) : IActorHomeLease {
    public bool IsHeld => lease.IsHeld;

    public string? CurrentHolder => lease.CurrentHolder;
}
