using Billing.Application.Modules.Invoicing.Actors;
using Elarion.Abstractions;
using Elarion.Abstractions.Authorization;
using Elarion.Actors;

namespace Billing.Application.Modules.Invoicing.Handlers;

/// <summary>
/// The dunning <b>query path</b> (ADR-0047/0048 design rules): reads the client's published dunning
/// state straight from the snapshot via <see cref="IActorStateReader"/> — no <c>IActorSystem</c>, no
/// facade, no mailbox — so it runs on <em>any</em> instance, including ones that are not the actor
/// home under single-homing. It loses nothing by skipping the actor because
/// <see cref="ClientDunningState"/> is the query contract: the threshold, the escalation latch, and
/// the <c>NeedsAttention</c> interpretation live on the record the reader deserializes. A client with
/// no dunning history simply has no snapshot → <see cref="ClientDunningState.Initial"/>. Requires the
/// <c>invoices.read</c> permission.
/// </summary>
[Handler("invoices.clientDunning")]
[RequirePermission("invoices", Verbs.Read)]
public sealed class GetClientDunning(IActorStateReader dunningStates)
    : IHandler<GetClientDunning.Query, Result<GetClientDunning.Response>> {
    public sealed record Query(Guid ClientId) : IQuery;

    public sealed record Response(int OverdueCount, bool Escalated, bool NeedsAttention);

    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        var state = await dunningStates
            .ReadAsync<ClientDunningState>(ClientDunningState.SnapshotKey(query.ClientId), ct)
            ?? ClientDunningState.Initial;
        return new Response(state.OverdueCount, state.Escalated, state.NeedsAttention);
    }
}
