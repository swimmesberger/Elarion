using Billing.Application.Modules.Invoicing.Events;
using Elarion.Abstractions.Messaging;
using Elarion.Actors;
using Microsoft.Extensions.Logging;

namespace Billing.Application.Modules.Invoicing.Actors;

/// <summary>
/// A per-client <b>dunning coordinator</b> (ADR-0042 + ADR-0046 + ADR-0047). When the nightly job flags
/// invoices overdue, each <see cref="InvoiceOverdue"/> integration event is relayed into this actor — one
/// activation per client, keyed by <c>ClientId</c> — which serializes the escalation decision without a lock
/// and emits the "escalate to collections" signal <b>exactly once</b> per client, no matter how many overdue
/// events arrive together in a batch.
/// </summary>
/// <remarks>
/// <para>
/// This is the shape actors are for: mutable per-key state (the running overdue count and the "already
/// escalated" latch) that many events update concurrently and that must be mutated one message at a time.
/// Done statelessly it is a read-modify-write race — two overdue events for the same client could each read
/// "below threshold" and both escalate, or neither. A lock-guarded singleton would serialize <em>every</em>
/// client against each other; the mailbox serializes each client independently, with no lock in this class.
/// </para>
/// <para>
/// The state is <b>snapshot-persisted</b> (ADR-0047): the <see cref="IActorState{TState}"/> constructor
/// parameter loads the client's latest snapshot before the first message and each mutation is made durable
/// by the explicit <c>WriteStateAsync</c> — so the latch survives idle passivation and restarts, and
/// "escalate once" holds across process lifetimes, not just within one activation. One <c>jsonb</c> row per
/// client in <c>elarion_actor_snapshots</c> (<c>Elarion.Actors.PostgreSql</c> on the billing database).
/// </para>
/// <para>
/// Still single-node by design (ADR-0025): the snapshot makes the state durable, not the actor clustered —
/// exactly one process must host these activations. If two processes ever host the same client, no write is
/// lost: the losing turn passivates its stale activation and transparently re-runs once on the winning
/// snapshot (side effects before the write — the log line here — are at-least-once on that path); sustained
/// double-hosting surfaces as <c>ActorSnapshotConcurrencyException</c> plus the
/// <c>actor.snapshot.conflicts</c> counter, never as silent divergence.
/// </para>
/// </remarks>
[Actor]
public sealed class ClientDunningActor(
    IActorContext<Guid> context,
    IActorState<ClientDunningState> dunning,
    ILogger<ClientDunningActor> logger
) {
    /// <summary>
    /// Relayed from <see cref="InvoiceOverdue"/> (ADR-0046). The event carries two <see cref="Guid"/>s, so the
    /// key is named explicitly: this actor is keyed by the client, not the invoice. <c>[ConsumeEvent]</c> keeps
    /// the method on the public facade (the relay calls it through <c>IActorSystem</c>, like a hand-written one).
    /// </summary>
    /// <remarks>
    /// The rich-state shape (ADR-0047 design rules): the <em>record</em> owns the transition and the
    /// interpretation, the actor only applies it, writes, and performs the side effect — <b>after</b> the
    /// successful write, because the write is the commit point (a conflicted turn re-runs, and a side effect
    /// before a failed write would duplicate).
    /// </remarks>
    [ConsumeEvent]
    [ActorKey(nameof(InvoiceOverdue.ClientId))]
    public async Task OnInvoiceOverdue(InvoiceOverdue e, CancellationToken cancellationToken) {
        var current = dunning.State ?? ClientDunningState.Initial;
        var next = current.RecordOverdue();
        dunning.State = next;
        await dunning.WriteStateAsync(cancellationToken);

        if (next.Escalated && !current.Escalated)
            logger.LogWarning(
                "Client {ClientId} reached {Count} overdue invoices — escalating to collections.",
                context.Key, next.OverdueCount);
    }

    /// <summary>A hot, lock-free read of this client's current dunning state — for a dashboard tile or a
    /// handler that resolves <c>IActorSystem.Get&lt;IClientDunning&gt;(clientId)</c> on the actor's home
    /// instance. Off-home queries read the same record through <c>IActorStateReader</c> instead.</summary>
    public Task<ClientDunningState> GetStateAsync() {
        return Task.FromResult(dunning.State ?? ClientDunningState.Initial);
    }
}

/// <summary>
/// The per-client dunning state: returned by <see cref="ClientDunningActor"/>'s facade, persisted as the
/// actor's snapshot payload (registered in <c>InvoicingJsonContext</c> like any wire DTO) — and therefore
/// <b>the query contract</b>. Interpretation (the threshold, the derived flags) and the pure transition live
/// on the record, not in actor methods, so a query reading the snapshot via <c>IActorStateReader</c> on any
/// instance shares exactly the logic the actor runs on its home (ADR-0047 design rules).
/// </summary>
public sealed record ClientDunningState(int OverdueCount, bool Escalated) {
    /// <summary>Overdue invoices at which a client escalates to collections.</summary>
    public const int EscalationThreshold = 3;

    /// <summary>The state of a client with no dunning history (no snapshot yet).</summary>
    public static ClientDunningState Initial { get; } = new(0, false);

    /// <summary>
    /// The snapshot identity of a client's dunning actor, kept beside the state so the query path
    /// (<c>IActorStateReader</c>) and the actor can never disagree on it. The actor name is the
    /// facade name: the class name minus the <c>Actor</c> suffix.
    /// </summary>
    public static ActorSnapshotKey SnapshotKey(Guid clientId) {
        return new ActorSnapshotKey("ClientDunning", clientId.ToString());
    }

    /// <summary>Derived interpretation, shared by the actor and off-home queries: one more overdue
    /// invoice escalates this client.</summary>
    public bool NeedsAttention => !Escalated && OverdueCount >= EscalationThreshold - 1;

    /// <summary>
    /// The pure transition for one overdue invoice: increments the count and trips the escalation
    /// latch at the threshold. Pure function of the current state — exactly the reapplyable shape
    /// the snapshot conflict retry re-runs safely (ADR-0047).
    /// </summary>
    public ClientDunningState RecordOverdue() {
        var count = OverdueCount + 1;
        return new ClientDunningState(count, Escalated || count >= EscalationThreshold);
    }
}
