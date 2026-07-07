using Elarion.Abstractions;
using Elarion.Abstractions.Auditing;
using Elarion.Abstractions.Identity;
using Elarion.Abstractions.Pipeline;
using Elarion.Auditing;

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
/// </remarks>
public sealed class AuditDecorator<TRequest, TResponse>(
    IHandler<TRequest, TResponse> inner,
    HandlerMetadata metadata,
    string action,
    string? module,
    AuditScope scope,
    IAuditTrail trail,
    ICurrentUser? currentUser
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
                    await trail.RecordDetachedAsync(
                        scope.BuildRecord(AuditOutcome.Failed, nameof(ErrorKind.Internal)),
                        CancellationToken.None).ConfigureAwait(false);
                }

                throw;
            }

            if (!scope.Recorded && response is IResultLike { IsSuccess: false }) {
                var kind = response is IResultError { Error: { } error } ? error.Kind : (ErrorKind?)null;
                var outcome = kind is ErrorKind.Unauthorized or ErrorKind.Forbidden
                    ? AuditOutcome.Denied
                    : AuditOutcome.Failed;
                await trail.RecordDetachedAsync(
                    scope.BuildRecord(outcome, kind?.ToString()),
                    CancellationToken.None).ConfigureAwait(false);
            }

            return response;
        } finally {
            scope.End();
        }
    }
}
