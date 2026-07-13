using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Elarion.Abstractions.Identity;
using Elarion.Abstractions.Pipeline;
using Elarion.Abstractions;
using Elarion.Abstractions.Idempotency;
using Elarion.Diagnostics;

namespace Elarion.Pipeline;

/// <summary>
/// Makes a command handler idempotent. It owns a single <see cref="IUnitOfWork"/> transaction in which it
/// claims the idempotency key (an atomic, unique-constrained write), runs the handler so the handler's business
/// writes commit together with the key, and — on a duplicate — replays the first request's stored result.
/// </summary>
/// <remarks>
/// <para>
/// Auto-attached by the handler generator for handlers carrying <see cref="IdempotentAttribute"/>, just inside
/// caching/feature-gate/authorization and outside the handler's own work. See the idempotency concept doc.
/// </para>
/// <para>
/// The same decorator is the <b>inbox</b> for handler-form integration-event consumers (ADR-0022): the generator
/// attaches it with a <see cref="IdempotencyScope.Consumer"/>-scoped policy whose key is the delivered message id,
/// so a redelivered event replays the recorded success instead of re-running the consumer's effect.
/// <paramref name="currentUser"/> is nullable because a delivery scope has no caller — it is only consulted for
/// <see cref="IdempotencyScope.CurrentUser"/>-scoped keys.
/// </para>
/// </remarks>
public sealed class IdempotencyDecorator<TRequest, TResponse>(
    IHandler<TRequest, TResponse> inner,
    IUnitOfWork unitOfWork,
    IIdempotencyStore store,
    IIdempotencyKeyAccessor keyAccessor,
    IIdempotencyPayloadPolicy<TRequest, TResponse> policy,
    ICurrentUser? currentUser,
    JsonSerializerOptions jsonOptions
) : IHandler<TRequest, TResponse>
    where TResponse : IResultFailureFactory<TResponse> {
    // A short lock wait so a concurrent in-flight duplicate fast-fails to a 409 instead of blocking for the
    // whole handler; tolerant of momentary contention. Used in Conflict mode.
    private static readonly TimeSpan ConflictLockTimeout = TimeSpan.FromMilliseconds(250);

    // WaitThenReplay deliberately blocks on the in-flight winner's row to replay its result, but the wait is
    // still bounded so a stuck winner can never pin the duplicate's database connection forever. On timeout the
    // duplicate degrades to the same "in progress" 409 the fast path returns, rather than hanging. The ceiling
    // is shared with the in-memory store so both tiers bound the wait identically.
    private static readonly TimeSpan WaitLockTimeout = Elarion.Idempotency.IdempotencyTimeouts.WaitThenReplayCeiling;

    private const string SavepointName = "elarion_idempotency";

    // The operation identity that discriminates the stored key so two different [Idempotent] handlers sharing a
    // client key never collide. The request type is unique per handler and stable across runs.
    private static readonly string Operation = typeof(TRequest).FullName ?? typeof(TRequest).Name;

    /// <inheritdoc />
    public async ValueTask<TResponse> HandleAsync(TRequest request, CancellationToken ct) {
        var key = ResolveKey(request);
        if (string.IsNullOrEmpty(key)) {
            return policy.KeyRequired
                ? TResponse.Failure(AppError.Validation("An idempotency key is required for this operation."))
                : await inner.HandleAsync(request, ct).ConfigureAwait(false);
        }

        if (policy.Scope == IdempotencyScope.CurrentUser && currentUser is not { IsAuthenticated: true }) {
            return TResponse.Failure(
                AppError.Unauthorized("A user-scoped idempotency key requires an authenticated caller."));
        }

        var owner = policy.Scope switch {
            IdempotencyScope.CurrentUser => Hash(currentUser!.UserId),
            // The inbox (ADR-0022): the generated policy bakes in the consuming handler's identity so each
            // fan-out consumer of one event claims its own row. A null Owner here is a policy bug, and running
            // without it would collapse all consumers of an event onto one claim — fail loud instead.
            IdempotencyScope.Consumer => policy.Owner ?? throw new InvalidOperationException(
                $"IdempotencyScope.Consumer requires the policy for '{typeof(TRequest).Name}' to supply Owner."),
            _ => string.Empty,
        };
        var storeKey = new IdempotencyStoreKey(Operation, policy.Scope, owner, key);
        var fingerprint = policy.Fingerprint ? ComputeFingerprint(request) : string.Empty;

        var options = new UnitOfWorkOptions {
            // Both paths bound the wait on a concurrent in-flight duplicate's row: Conflict fast-fails to a 409,
            // WaitThenReplay blocks (to replay) but only up to a longer ceiling so a stuck winner cannot pin the
            // connection indefinitely. On timeout the store surfaces the "in progress" 409 either way.
            LockTimeout = policy.ConflictBehavior == IdempotencyConflictBehavior.Conflict
                ? ConflictLockTimeout
                : WaitLockTimeout,
        };

        await using var scope = await unitOfWork.BeginAsync(options, ct).ConfigureAwait(false);

        var begin = await store.TryBeginAsync(storeKey, fingerprint, policy.ConflictBehavior, ct).ConfigureAwait(false);
        switch (begin.Status) {
            case IdempotencyBeginStatus.Replay:
                RecordOutcome("replayed");
                await scope.RollbackAsync(ct).ConfigureAwait(false);
                return policy.Deserialize(begin.Payload!, jsonOptions);
            case IdempotencyBeginStatus.InProgress:
                RecordOutcome("conflict");
                await scope.RollbackAsync(ct).ConfigureAwait(false);
                return TResponse.Failure(
                    AppError.Conflict("A request with this idempotency key is already being processed."));
            case IdempotencyBeginStatus.FingerprintMismatch:
                RecordOutcome("fingerprint_mismatch");
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
            RecordOutcome("abandoned");
            await store.AbandonAsync(storeKey, ct).ConfigureAwait(false);
            await scope.RollbackAsync(ct).ConfigureAwait(false);
            throw;
        }

        if (response is IResultLike { IsSuccess: true }) {
            RecordOutcome("completed");
            // The handler succeeded: finalize the key and commit uncancellably. A cancellation arriving now must
            // not roll back completed work and leave the key claimable again — that would re-run a done command.
            await store.CompleteAsync(storeKey, policy.Serialize(response, jsonOptions), isFailure: false, policy.Retention, CancellationToken.None)
                .ConfigureAwait(false);
            await scope.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            return response;
        }

        if (storeFailures && IsDefinitiveFailure(response)) {
            // Discard the handler's business writes but keep the key row (claimed before the savepoint), then
            // record the definitive failure so a retry replays it — all in the one transaction. The outcome is
            // decided, so finalize uncancellably (see the success path above).
            RecordOutcome("completed");
            await scope.RollbackToSavepointAsync(SavepointName, CancellationToken.None).ConfigureAwait(false);
            await store.CompleteAsync(storeKey, policy.Serialize(response, jsonOptions), isFailure: true, policy.Retention, CancellationToken.None)
                .ConfigureAwait(false);
            await scope.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            return response;
        }

        // Transient / non-stored failure — discard the key so the same key stays retryable.
        RecordOutcome("abandoned");
        await store.AbandonAsync(storeKey, ct).ConfigureAwait(false);
        await scope.RollbackAsync(ct).ConfigureAwait(false);
        return response;
    }

    /// <summary>
    /// Surfaces the key resolution on the handler span and the idempotency counter — replays and conflicts are
    /// otherwise invisible, since the transport sees an ordinary success/409.
    /// </summary>
    private static void RecordOutcome(string outcome) {
        Activity.Current?.SetTag("elarion.idempotency.outcome", outcome);
        HandlerTelemetry.RecordIdempotency(typeof(TRequest).Name, outcome);
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
