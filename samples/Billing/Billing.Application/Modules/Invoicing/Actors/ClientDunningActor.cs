using Billing.Application.Modules.Invoicing.Events;
using Elarion.Abstractions.Messaging;
using Elarion.Actors;
using Microsoft.Extensions.Logging;

namespace Billing.Application.Modules.Invoicing.Actors;

/// <summary>
/// A per-client, in-memory <b>dunning coordinator</b> (ADR-0042 + ADR-0046). When the nightly job flags
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
/// Single-node by design (ADR-0025): the latch is node-local and ephemeral — on idle passivation or a node
/// restart it is dropped and rebuilt from the next batch. That is fine here (a duplicate escalation log is
/// harmless). Authoritative, cluster-wide "escalate once ever" would replace this actor with a durable
/// coordinator behind the same seam, never grow this in-memory default.
/// </para>
/// </remarks>
[Actor]
public sealed class ClientDunningActor(
    IActorContext<Guid> context,
    ILogger<ClientDunningActor> logger
) {
    private const int EscalationThreshold = 3;

    private int _overdueCount;
    private bool _escalated;

    /// <summary>
    /// Relayed from <see cref="InvoiceOverdue"/> (ADR-0046). The event carries two <see cref="Guid"/>s, so the
    /// key is named explicitly: this actor is keyed by the client, not the invoice. <c>[ConsumeEvent]</c> keeps
    /// the method on the public facade (the relay calls it through <c>IActorSystem</c>, like a hand-written one).
    /// </summary>
    [ConsumeEvent]
    [ActorKey(nameof(InvoiceOverdue.ClientId))]
    public Task OnInvoiceOverdue(InvoiceOverdue e) {
        _overdueCount++;
        if (!_escalated && _overdueCount >= EscalationThreshold) {
            _escalated = true;
            logger.LogWarning(
                "Client {ClientId} reached {Count} overdue invoices — escalating to collections.",
                context.Key, _overdueCount);
        }

        return Task.CompletedTask;
    }

    /// <summary>A hot, lock-free read of this client's current dunning state — for a dashboard tile or a
    /// handler that resolves <c>IActorSystem.Get&lt;IClientDunning&gt;(clientId)</c>.</summary>
    public Task<ClientDunningState> GetStateAsync() =>
        Task.FromResult(new ClientDunningState(_overdueCount, _escalated));
}

/// <summary>The per-client dunning snapshot returned by <see cref="ClientDunningActor"/>'s facade.</summary>
public sealed record ClientDunningState(int OverdueCount, bool Escalated);
