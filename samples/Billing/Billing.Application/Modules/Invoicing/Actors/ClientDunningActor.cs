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
    private const int EscalationThreshold = 3;

    /// <summary>
    /// Relayed from <see cref="InvoiceOverdue"/> (ADR-0046). The event carries two <see cref="Guid"/>s, so the
    /// key is named explicitly: this actor is keyed by the client, not the invoice. <c>[ConsumeEvent]</c> keeps
    /// the method on the public facade (the relay calls it through <c>IActorSystem</c>, like a hand-written one).
    /// </summary>
    [ConsumeEvent]
    [ActorKey(nameof(InvoiceOverdue.ClientId))]
    public async Task OnInvoiceOverdue(InvoiceOverdue e, CancellationToken cancellationToken) {
        var current = dunning.State ?? new ClientDunningState(0, false);
        var next = current with { OverdueCount = current.OverdueCount + 1 };
        if (!next.Escalated && next.OverdueCount >= EscalationThreshold) {
            next = next with { Escalated = true };
            logger.LogWarning(
                "Client {ClientId} reached {Count} overdue invoices — escalating to collections.",
                context.Key, next.OverdueCount);
        }

        dunning.State = next;
        await dunning.WriteStateAsync(cancellationToken);
    }

    /// <summary>A hot, lock-free read of this client's current dunning state — for a dashboard tile or a
    /// handler that resolves <c>IActorSystem.Get&lt;IClientDunning&gt;(clientId)</c>.</summary>
    public Task<ClientDunningState> GetStateAsync() =>
        Task.FromResult(dunning.State ?? new ClientDunningState(0, false));
}

/// <summary>The per-client dunning state: returned by <see cref="ClientDunningActor"/>'s facade and persisted
/// as the actor's snapshot payload (registered in <c>InvoicingJsonContext</c> like any wire DTO).</summary>
public sealed record ClientDunningState(int OverdueCount, bool Escalated);
