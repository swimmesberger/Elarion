using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Elarion.Abstractions.Identity;
using Elarion.Abstractions.Pipeline;

namespace Elarion.Abstractions.Idempotency;

/// <summary>
/// Makes a command handler idempotent. It owns a single <see cref="IUnitOfWork"/> transaction in which it
/// claims the idempotency key (an atomic, unique-constrained write), runs the handler so the handler's business
/// writes commit together with the key, and — on a duplicate — replays the first request's stored result.
/// </summary>
/// <remarks>
/// Auto-attached by the handler generator for handlers carrying <see cref="IdempotentAttribute"/>, just inside
/// caching/feature-gate/authorization and outside the handler's own work. See the idempotency concept doc.
/// </remarks>
public sealed class IdempotencyDecorator<TRequest, TResponse>(
    IHandler<TRequest, TResponse> inner,
    IUnitOfWork unitOfWork,
    IIdempotencyStore store,
    IIdempotencyKeyAccessor keyAccessor,
    IIdempotencyPayloadPolicy<TRequest, TResponse> policy,
    ICurrentUser currentUser,
    JsonSerializerOptions jsonOptions
) : IHandler<TRequest, TResponse>
    where TResponse : IResultFailureFactory<TResponse> {
    // A short lock wait so a concurrent in-flight duplicate fast-fails to a 409 instead of blocking for the
    // whole handler; tolerant of momentary contention. Only used in Conflict mode.
    private static readonly TimeSpan ConflictLockTimeout = TimeSpan.FromMilliseconds(250);
    private const string SavepointName = "elarion_idempotency";

    /// <inheritdoc />
    public async ValueTask<TResponse> HandleAsync(TRequest request, CancellationToken ct) {
        var key = ResolveKey(request);
        if (string.IsNullOrEmpty(key)) {
            return policy.KeyRequired
                ? TResponse.Failure(AppError.Validation("An idempotency key is required for this operation."))
                : await inner.HandleAsync(request, ct).ConfigureAwait(false);
        }

        if (policy.Scope == IdempotencyScope.CurrentUser && !currentUser.IsAuthenticated) {
            return TResponse.Failure(
                AppError.Unauthorized("A user-scoped idempotency key requires an authenticated caller."));
        }

        var owner = policy.Scope == IdempotencyScope.CurrentUser ? Hash(currentUser.UserId) : string.Empty;
        var storeKey = new IdempotencyStoreKey(policy.Scope, owner, key);
        var fingerprint = policy.Fingerprint ? ComputeFingerprint(request) : string.Empty;

        var options = new UnitOfWorkOptions {
            LockTimeout = policy.ConflictBehavior == IdempotencyConflictBehavior.Conflict ? ConflictLockTimeout : null,
        };

        await using var scope = await unitOfWork.BeginAsync(options, ct).ConfigureAwait(false);

        var begin = await store.TryBeginAsync(storeKey, fingerprint, policy.ConflictBehavior, ct).ConfigureAwait(false);
        switch (begin.Status) {
            case IdempotencyBeginStatus.Replay:
                await scope.RollbackAsync(ct).ConfigureAwait(false);
                return policy.Deserialize(begin.Payload!, jsonOptions);
            case IdempotencyBeginStatus.InProgress:
                await scope.RollbackAsync(ct).ConfigureAwait(false);
                return TResponse.Failure(
                    AppError.Conflict("A request with this idempotency key is already being processed."));
            case IdempotencyBeginStatus.FingerprintMismatch:
                await scope.RollbackAsync(ct).ConfigureAwait(false);
                return TResponse.Failure(
                    AppError.BusinessRule("This idempotency key was already used with a different request."));
        }

        // Began — run the handler inside the claimed transaction.
        var storeFailures = policy.StoreFailures == IdempotencyFailureStorage.Definitive;
        if (storeFailures) {
            await scope.CreateSavepointAsync(SavepointName, ct).ConfigureAwait(false);
        }

        TResponse response;
        try {
            response = await inner.HandleAsync(request, ct).ConfigureAwait(false);
        } catch {
            await store.AbandonAsync(storeKey, ct).ConfigureAwait(false);
            await scope.RollbackAsync(ct).ConfigureAwait(false);
            throw;
        }

        if (response is IResultLike { IsSuccess: true }) {
            await store.CompleteAsync(storeKey, policy.Serialize(response, jsonOptions), isFailure: false, policy.Retention, ct)
                .ConfigureAwait(false);
            await scope.CommitAsync(ct).ConfigureAwait(false);
            return response;
        }

        if (storeFailures && IsDefinitiveFailure(response)) {
            // Discard the handler's business writes but keep the key row (claimed before the savepoint), then
            // record the definitive failure so a retry replays it — all in the one transaction.
            await scope.RollbackToSavepointAsync(SavepointName, ct).ConfigureAwait(false);
            await store.CompleteAsync(storeKey, policy.Serialize(response, jsonOptions), isFailure: true, policy.Retention, ct)
                .ConfigureAwait(false);
            await scope.CommitAsync(ct).ConfigureAwait(false);
            return response;
        }

        // Transient / non-stored failure — discard the key so the same key stays retryable.
        await store.AbandonAsync(storeKey, ct).ConfigureAwait(false);
        await scope.RollbackAsync(ct).ConfigureAwait(false);
        return response;
    }

    private string? ResolveKey(TRequest request) {
        if (keyAccessor.TryGetKey(out var captured)) {
            return captured;
        }

        return request is IIdempotentRequest { IdempotencyKey: { Length: > 0 } inBand } ? inBand : null;
    }

    private string ComputeFingerprint(TRequest request) {
        var json = JsonSerializer.Serialize(request, jsonOptions.GetTypeInfo(typeof(TRequest)));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    private static bool IsDefinitiveFailure(TResponse response) =>
        response is IResultError {
            Error.Kind: ErrorKind.Validation or ErrorKind.BusinessRule or ErrorKind.NotFound or ErrorKind.Forbidden,
        };

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
