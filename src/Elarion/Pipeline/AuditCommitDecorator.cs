using Elarion.Abstractions;
using Elarion.Abstractions.Auditing;
using Elarion.Auditing;

namespace Elarion.Pipeline;

/// <summary>
/// The inner audit decorator (ADR-0045): sits inside the transaction boundary
/// (<see cref="TransactionDecorator{TRequest,TResponse}"/>/<see cref="IdempotencyDecorator{TRequest,TResponse}"/>)
/// and, on a successful result, drains the invocation's <see cref="Auditing.AuditScope"/> into one record via
/// <see cref="IAuditTrail.RecordAsync"/> — the EF sink enlists that write in the ambient transaction, so the
/// success record commits atomically with the handler's business writes. With no ambient transaction (queries,
/// read auditing) the sink persists immediately instead.
/// </summary>
/// <remarks>
/// Resets the scope's accumulated changes before each attempt: resilience retries re-enter this decorator,
/// and a record must never mix a rolled-back attempt's diffs with the succeeding attempt's. Failures are not
/// recorded here — the transaction is about to roll an enlisted write back — but by the outer
/// <see cref="AuditDecorator{TRequest,TResponse}"/> on the detached path. An idempotent replay short-circuits
/// before this decorator, so a duplicate deliberately produces no second record. The success write runs with
/// <see cref="CancellationToken.None"/>, mirroring the transaction decorator's uncancellable finalizing commit.
/// </remarks>
public sealed class AuditCommitDecorator<TRequest, TResponse>(
    IHandler<TRequest, TResponse> inner,
    Auditing.AuditScope scope,
    IAuditTrail trail
) : IHandler<TRequest, TResponse> {
    /// <inheritdoc />
    public async ValueTask<TResponse> HandleAsync(TRequest request, CancellationToken ct) {
        scope.BeginAttempt();
        var response = await inner.HandleAsync(request, ct).ConfigureAwait(false);

        if (scope.IsActive && response is not IResultLike { IsSuccess: false }) {
            // The record is handed over as a factory: the sink flushes the handler's pending writes first (so
            // change capture contributes their diffs to the scope) and materializes the record only after.
            await trail.RecordAsync(
                () => scope.BuildRecord(AuditOutcome.Succeeded, errorKind: null),
                CancellationToken.None).ConfigureAwait(false);
            scope.MarkRecorded();
        }

        return response;
    }
}
