using Elarion.Abstractions.Idempotency;
using Elarion.Abstractions.Messaging;
using Elarion.Abstractions;
using Elarion.Abstractions.Pipeline;

namespace Elarion.Pipeline;

/// <summary>
/// Wraps a handler in a single <see cref="IUnitOfWork"/> transaction: the inner handler and every write it
/// makes commit atomically on a successful <see cref="IResultLike"/> result and roll back otherwise.
/// </summary>
/// <remarks>
/// Framework-owned and reusable (other features compose the same unit-of-work boundary). It attaches only to
/// commands and integration-event handlers — the operations that own a new unit of work — via the
/// compile-time <see cref="AppliesTo"/> predicate, and it deliberately skips <c>[Idempotent]</c> handlers,
/// which own the transaction themselves (so a handler is never wrapped in two nested transactions), and commands
/// marked <see cref="NoTransactionAttribute"/>.
/// <para>
/// An exception from the handler or from the commit — a deferred constraint is checked only at <c>COMMIT</c> — is
/// offered to the registered <see cref="IUnitOfWorkFailureTranslator"/>s; the first error one returns becomes the
/// command's failed result after the rollback, so a constraint the caller can act on answers like any other failure
/// instead of an unexpected error.
/// </para>
/// </remarks>
public sealed class TransactionDecorator<TRequest, TResponse>(
    IHandler<TRequest, TResponse> inner,
    IUnitOfWork unitOfWork,
    IEnumerable<IUnitOfWorkFailureTranslator> failureTranslators
) : IHandler<TRequest, TResponse> {
    // Materialized once per decorator: the translators are asked only on the exception path.
    private readonly IUnitOfWorkFailureTranslator[] _failureTranslators = [.. failureTranslators];

    /// <summary>
    /// Attaches to commands and integration-event handlers, except those whose pipeline already owns the unit of
    /// work: <see cref="IdempotentAttribute"/> commands, and integration-event consumers under the default-on
    /// inbox (ADR-0022) — only an <c>[AllowDuplicates]</c> opt-out gets the plain transaction back — and commands
    /// that opted out with <see cref="NoTransactionAttribute"/>.
    /// </summary>
    public static bool AppliesTo(HandlerMetadata handler) {
        if (handler.GetAttribute<IdempotentAttribute>() is not null || handler.GetAttribute<NoTransactionAttribute>() is not null)
            return false;

        // Mirrors the generator's inbox attachment: an integration-event consumer is inboxed by default, and the
        // inbox decorator owns the transaction (claim + business writes commit atomically). Wrapping it again
        // here would nest two units of work.
        if (handler.RequestType.IsAssignableTo(typeof(IIntegrationEvent)))
            return handler.GetAttribute<AllowDuplicatesAttribute>() is not null;

        return handler.RequestType.IsAssignableTo(typeof(ICommand));
    }

    /// <inheritdoc />
    /// <remarks>
    /// Once the handler has returned a successful result the command is logically done, so the commit runs with
    /// <see cref="CancellationToken.None"/>: a cancellation that arrives in the gap between success and commit
    /// must not silently roll back completed work (it would be indistinguishable from cancelling the handler).
    /// The request token still cancels the handler itself; only the finalizing commit is uncancellable.
    /// </remarks>
    public async ValueTask<TResponse> HandleAsync(TRequest request, CancellationToken ct) {
        await using var scope = await unitOfWork.BeginAsync(UnitOfWorkOptions.Default, ct).ConfigureAwait(false);
        TResponse response;
        try {
            response = await inner.HandleAsync(request, ct).ConfigureAwait(false);

            if (ResultInspection.IsSuccess(response))
                await scope.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            else
                await scope.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (_failureTranslators.Length > 0 && TryTranslate(ex, out var failure)) {
            // Disposing the scope rolls back what the commit or the handler left open (a nested scope only to its
            // savepoint), so the failure is returned with the unit of work already discarded.
            return failure;
        }

        return response;
    }

    private bool TryTranslate(Exception exception, out TResponse failure) {
        if (exception is OperationCanceledException) {
            failure = default!;
            return false;
        }

        foreach (var translator in _failureTranslators)
            if (translator.Translate(exception) is { } error)
                return ResultInspection.TryCreateFailure(error, out failure!);

        failure = default!;
        return false;
    }
}
