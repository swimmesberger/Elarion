using Elarion.Abstractions.Idempotency;
using Elarion.Abstractions.Messaging;

namespace Elarion.Abstractions.Pipeline;

/// <summary>
/// Wraps a handler in a single <see cref="IUnitOfWork"/> transaction: the inner handler and every write it
/// makes commit atomically on a successful <see cref="IResultLike"/> result and roll back otherwise.
/// </summary>
/// <remarks>
/// Framework-owned and reusable (other features compose the same unit-of-work boundary). It attaches only to
/// commands and integration-event handlers — the operations that own a new unit of work — via the
/// compile-time <see cref="AppliesTo"/> predicate, and it deliberately skips <c>[Idempotent]</c> handlers,
/// which own the transaction themselves (so a handler is never wrapped in two nested transactions).
/// </remarks>
public sealed class TransactionDecorator<TRequest, TResponse>(
    IHandler<TRequest, TResponse> inner,
    IUnitOfWork unitOfWork
) : IHandler<TRequest, TResponse> {
    /// <summary>
    /// Attaches to commands and integration-event handlers, except those already carrying
    /// <see cref="IdempotentAttribute"/> (they own their own unit of work).
    /// </summary>
    public static bool AppliesTo(HandlerMetadata handler) =>
        (handler.RequestType.IsAssignableTo(typeof(ICommand)) ||
         handler.RequestType.IsAssignableTo(typeof(IIntegrationEvent)))
        && handler.GetAttribute<IdempotentAttribute>() is null;

    /// <inheritdoc />
    /// <remarks>
    /// Once the handler has returned a successful result the command is logically done, so the commit runs with
    /// <see cref="CancellationToken.None"/>: a cancellation that arrives in the gap between success and commit
    /// must not silently roll back completed work (it would be indistinguishable from cancelling the handler).
    /// The request token still cancels the handler itself; only the finalizing commit is uncancellable.
    /// </remarks>
    public async ValueTask<TResponse> HandleAsync(TRequest request, CancellationToken ct) {
        await using var scope = await unitOfWork.BeginAsync(UnitOfWorkOptions.Default, ct).ConfigureAwait(false);
        var response = await inner.HandleAsync(request, ct).ConfigureAwait(false);

        if (response is IResultLike { IsSuccess: true }) {
            await scope.CommitAsync(CancellationToken.None).ConfigureAwait(false);
        } else {
            await scope.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }

        return response;
    }
}
