using System.Buffers.Text;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Elarion.Abstractions;
using Elarion.Abstractions.Authorization;
using Elarion.Abstractions.Pipeline;
using Elarion.Abstractions.Previews;
using Elarion.Abstractions.Serialization;

namespace Elarion.Pipeline;

/// <summary>
/// Runs a previewable command (<see cref="IPreviewableRequest"/>) as a preview or as a commit checked against the
/// preview the caller saw: it owns the unit of work, runs the inner pipeline, fingerprints the outcome, and then rolls
/// back (a preview or a changed outcome) or commits (an unchanged outcome).
/// </summary>
/// <remarks>
/// <para>
/// Auto-attached by the handler generator to every handler whose request implements
/// <see cref="IPreviewableRequest"/> and whose response is <c>Result&lt;TOutcome&gt;</c>, just inside idempotency and
/// outside the application's <c>[DecoratorList]</c>: under <c>[Idempotent]</c> its scope nests in the idempotency
/// decorator's transaction (a savepoint), and an application transaction decorator inside it nests in its scope.
/// </para>
/// <para>
/// An ordinary call (no <see cref="IPreviewableRequest.Preview"/>, no
/// <see cref="IPreviewableRequest.PreviewFingerprint"/>) passes straight through: no unit of work, no hashing. Any
/// other call needs a unit of work whose rollback really discards writes
/// (<see cref="IUnitOfWork.SupportsRollback"/>); without one the decorator answers
/// <see cref="ErrorCodes.PreviewUnsupported"/> and never runs the handler, so a preview can never commit.
/// </para>
/// <para>
/// The inner pipeline runs in a nested scope that is committed on success, so a backend with a change tracker flushes
/// the handler's writes (and a constraint violation surfaces) during a preview exactly as during a commit; the
/// decorator's own scope then decides. The fingerprint is the SHA-256 (base64url) of the outcome's canonical JSON
/// with its fingerprint cleared, serialized through <see cref="IElarionJsonSerialization"/> from source-generated
/// metadata.
/// </para>
/// <para>
/// When the outcome has members visible by permission (<see cref="Abstractions.Authorization.VisibleWithAttribute"/>),
/// the generator also passes the caller's permission check and the copy that hides them, and the fingerprint covers the
/// outcome as this caller sees it. A hash over members the caller may not see would let it confirm a guess of their
/// values; a preview and its commit come from the same caller, so they hash the same view.
/// </para>
/// </remarks>
/// <typeparam name="TRequest">The previewable request type.</typeparam>
/// <typeparam name="TOutcome">The outcome type, the success value of the handler's <see cref="Result{T}"/>.</typeparam>
public sealed class PreviewDecorator<TRequest, TOutcome>(
    IHandler<TRequest, Result<TOutcome>> inner,
    IUnitOfWork? unitOfWork,
    IElarionJsonSerialization json,
    IPermissionChecker? permissions = null,
    Func<TOutcome, IPermissionChecker, TOutcome>? visibleTo = null
) : IHandler<TRequest, Result<TOutcome>>
    where TRequest : IPreviewableRequest
    where TOutcome : IPreviewOutcome<TOutcome> {
    /// <inheritdoc />
    /// <remarks>
    /// Once the handler has succeeded the outcome is decided, so the flush, the fingerprint and the final commit or
    /// rollback run with <see cref="CancellationToken.None"/>, like the transaction decorator's finalizing commit.
    /// </remarks>
    public ValueTask<Result<TOutcome>> HandleAsync(TRequest request, CancellationToken ct) {
        // An ordinary call returns the inner task as is: no unit of work, no hashing, no async state machine.
        return !request.Preview && request.PreviewFingerprint is null
            ? inner.HandleAsync(request, ct)
            : PreviewOrCommitAsync(request, request.Preview, request.PreviewFingerprint, ct);
    }

    private async ValueTask<Result<TOutcome>> PreviewOrCommitAsync(
        TRequest request, bool preview, string? expected, CancellationToken ct) {
        // Fail closed: a unit of work whose rollback does nothing would commit what the caller was told is a preview.
        if (unitOfWork is not { SupportsRollback: true }) {
            RecordOutcome("unsupported");
            return Result<TOutcome>.Failure(AppError.Internal(
                "This command cannot be previewed: the host has no unit of work that can roll back.",
                ErrorCodes.PreviewUnsupported));
        }

        await using var scope = await unitOfWork.BeginAsync(UnitOfWorkOptions.Default, ct).ConfigureAwait(false);
        var response = await RunInNestedScopeAsync(unitOfWork, request, ct).ConfigureAwait(false);
        if (!response.IsSuccess) {
            RecordOutcome("failed");
            await scope.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            return response;
        }

        var fingerprint = ComputeFingerprint(response.Value);
        var stamped = response.Value.WithPreviewFingerprint(fingerprint);

        if (preview) {
            RecordOutcome("previewed");
            await scope.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            return Result<TOutcome>.Success(stamped);
        }

        if (string.Equals(fingerprint, expected, StringComparison.Ordinal)) {
            RecordOutcome("committed");
            await scope.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            return Result<TOutcome>.Success(stamped);
        }

        RecordOutcome("changed");
        await scope.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        return Result<TOutcome>.Failure(AppError.Conflict(
            "The outcome changed since the preview; nothing was committed. Review the new outcome and confirm again.",
            ErrorCodes.PreviewChanged,
            stamped));
    }

    // The handler's writes are flushed by committing a nested scope (on EF: SaveChanges plus releasing a savepoint),
    // so they become real statements inside the decorator's transaction — and a flushed change tracker cannot write
    // them again when an outer scope (idempotency) later commits its own row after this scope rolled back.
    private async ValueTask<Result<TOutcome>> RunInNestedScopeAsync(
        IUnitOfWork transactional, TRequest request, CancellationToken ct) {
        await using var work = await transactional.BeginAsync(UnitOfWorkOptions.Default, ct).ConfigureAwait(false);
        var response = await inner.HandleAsync(request, ct).ConfigureAwait(false);
        if (response.IsSuccess)
            await work.CommitAsync(CancellationToken.None).ConfigureAwait(false);
        else
            await work.RollbackAsync(CancellationToken.None).ConfigureAwait(false);

        return response;
    }

    private string ComputeFingerprint(TOutcome outcome) {
        var cleared = outcome.WithPreviewFingerprint(null);
        var view = visibleTo is null ? cleared : visibleTo(cleared, permissions ?? NoPermissions.Instance);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(view, json.GetTypeInfo<TOutcome>());
        return Base64Url.EncodeToString(SHA256.HashData(bytes));
    }

    /// <summary>Surfaces what the preview decorator did on the handler span; the transport sees only the result.</summary>
    private static void RecordOutcome(string outcome) {
        Activity.Current?.SetTag("elarion.preview.outcome", outcome);
    }
}
