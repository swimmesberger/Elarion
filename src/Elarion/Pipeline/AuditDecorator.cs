using System.Diagnostics;
using Elarion.Abstractions;
using Elarion.Abstractions.Auditing;
using Elarion.Abstractions.Identity;
using Elarion.Abstractions.Pipeline;
using Elarion.Auditing;
using Microsoft.Extensions.Logging;

namespace Elarion.Pipeline;

/// <summary>
/// The outer audit decorator (ADR-0045): opens the invocation's <see cref="AuditScope"/> frame (actor +
/// action) just outside authorization — so denied attempts are observed — and records every non-success
/// outcome on the detached path (<see cref="IAuditTrail.RecordDetachedAsync"/>), which must survive the
/// business transaction's rollback. Success records are written by <see cref="AuditCommitDecorator{TRequest,TResponse}"/>
/// inside the transaction; the <see cref="AuditScope.Recorded"/> flag keeps the two paths exclusive.
/// </summary>
/// <remarks>
/// A failed result whose <see cref="ErrorKind"/> is <see cref="ErrorKind.Unauthorized"/> or
/// <see cref="ErrorKind.Forbidden"/> is recorded as <see cref="AuditOutcome.Denied"/>; every other failure
/// (and any unexpected exception, which is rethrown) as <see cref="AuditOutcome.Failed"/>. Cooperative
/// cancellation is not an auditable outcome. Detached writes run with <see cref="CancellationToken.None"/> —
/// once an outcome exists, its record must not be lost to a caller that is already leaving.
/// A <b>failing detached write never masks the handler's outcome</b>: the audit exception is logged at Error
/// and swallowed, so the handler's original exception still propagates and a domain failure (a 4xx-shaped
/// <c>Result</c>) is still returned instead of turning into a 500.
/// </remarks>
public sealed class AuditDecorator<TRequest, TResponse>(
    IHandler<TRequest, TResponse> inner,
    HandlerMetadata metadata,
    string action,
    string? module,
    AuditScope scope,
    IAuditTrail trail,
    ICurrentUser? currentUser,
    ILogger<AuditDecorator<TRequest, TResponse>>? logger = null
) : IHandler<TRequest, TResponse> {
    /// <inheritdoc />
    public async ValueTask<TResponse> HandleAsync(TRequest request, CancellationToken ct) {
        var auditable = metadata.GetAttribute<AuditableAttribute>();
        var userId = currentUser is { IsAuthenticated: true } user ? user.UserId : null;
        scope.Begin(action, module, userId, auditable?.Resource);
        try {
            TResponse response;
            try {
                response = await inner.HandleAsync(request, ct).ConfigureAwait(false);
            } catch (Exception ex) {
                if (!scope.Recorded && (ex is not OperationCanceledException || !ct.IsCancellationRequested)) {
                    await RecordDetachedSafelyAsync(AuditOutcome.Failed, nameof(ErrorKind.Internal))
                        .ConfigureAwait(false);
                }

                throw;
            }

            if (!scope.Recorded && response is IResultLike { IsSuccess: false }) {
                var kind = response is IResultError { Error: { } error } ? error.Kind : (ErrorKind?)null;
                var outcome = kind is ErrorKind.Unauthorized or ErrorKind.Forbidden
                    ? AuditOutcome.Denied
                    : AuditOutcome.Failed;
                await RecordDetachedSafelyAsync(outcome, kind?.ToString()).ConfigureAwait(false);
            }

            return response;
        } finally {
            scope.End();
        }
    }

    /// <summary>
    /// Writes the detached record without letting an audit-sink failure replace the handler's outcome: an
    /// exception from the trail is logged at Error and swallowed. The invocation already has a decided outcome
    /// (a thrown exception or a failed result) and that outcome must reach the caller unchanged.
    /// </summary>
    private async ValueTask RecordDetachedSafelyAsync(AuditOutcome outcome, string? errorKind) {
        try {
            await trail.RecordDetachedAsync(scope.BuildRecord(outcome, errorKind), CancellationToken.None)
                .ConfigureAwait(false);
        } catch (Exception auditEx) {
            logger?.LogError(
                auditEx,
                "Detached audit write failed for action '{Action}' (outcome {Outcome}); the handler's original outcome is returned unchanged.",
                action,
                outcome);
            // Also surface the loss on the handler span so it stays observable when no logger was supplied.
            Activity.Current?.AddEvent(new ActivityEvent(
                "audit detached write failed",
                tags: new ActivityTagsCollection {
                    { "exception.type", auditEx.GetType().FullName },
                    { "exception.message", auditEx.Message },
                }));
        }
    }
}
