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
    /// Attaches to commands and integration-event handlers, except those whose pipeline already owns the unit of
    /// work: <see cref="IdempotentAttribute"/> commands, and integration-event consumers under the default-on
    /// inbox (ADR-0022) — only an <c>[Inbox(Enabled = false)]</c> opt-out gets the plain transaction back.
    /// </summary>
    public static bool AppliesTo(HandlerMetadata handler) {
        if (handler.GetAttribute<IdempotentAttribute>() is not null)
            return false;

        // Mirrors the generator's inbox attachment: an integration-event consumer is inboxed by default, and the
        // inbox decorator owns the transaction (claim + business writes commit atomically). Wrapping it again
        // here would nest two units of work.
        if (handler.RequestType.IsAssignableTo(typeof(IIntegrationEvent)))
            return handler.GetAttribute<InboxAttribute>() is { Enabled: false };

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
        var response = await inner.HandleAsync(request, ct).ConfigureAwait(false);

        if (response is IResultLike { IsSuccess: true }) {
            await scope.CommitAsync(CancellationToken.None).ConfigureAwait(false);
        } else {
            await scope.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }

        return response;
    }
}
