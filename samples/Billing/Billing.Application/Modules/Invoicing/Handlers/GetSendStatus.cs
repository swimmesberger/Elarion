using Elarion.Abstractions;
using Elarion.Abstractions.Authorization;
using Elarion.Abstractions.Scheduling;

namespace Billing.Application.Modules.Invoicing.Handlers;

/// <summary>Exposes the scheduler's in-memory state for a send job. Takes the stable <c>JobId</c> the
/// create handler returned (not a per-attempt <c>RunId</c>), so it reports the aggregate state —
/// <c>WaitingRetry</c> between attempts, <c>Succeeded</c> once delivered, <c>Failed</c> once retries
/// are exhausted. Requires the <c>invoices:read</c> permission.</summary>
[Handler("invoices.sendStatus")]
[RequirePermission("invoices:read")]
public sealed class GetSendStatus(IJobSchedulerInspector scheduler)
    : IHandler<GetSendStatus.Query, Result<GetSendStatus.Response>> {
    public sealed record Query(Guid JobId) : IQuery;
    public sealed record Response(
        string Status, int Attempt, int MaxAttempts,
        DateTimeOffset? NextAttemptAt, string? LastError);

    public ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        var state = scheduler.GetJob(query.JobId);
        Result<Response> result = state is null
            ? AppError.NotFound($"No send job {query.JobId} is currently tracked.")
            : new Response(
                state.Status.ToString(), state.Attempt, state.MaxAttempts,
                state.NextAttemptDueTimeUtc, state.LastError);
        return ValueTask.FromResult(result);
    }
}
