namespace Elarion.Abstractions.Messaging;

/// <summary>
/// The per-scope buffer for integration events awaiting after-commit delivery.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IIntegrationEventBus.PublishAsync"/> records events into the current scope's buffer
/// rather than delivering them. The unit-of-work owner (typically the application's transaction
/// decorator) decides the outcome: <see cref="FlushAsync"/> hands the buffered events to the
/// delivery tier after a successful commit, and <see cref="Discard"/> drops them on rollback.
/// </para>
/// <para>
/// This seam is what makes in-memory Plane B delivery commit-gated without the framework owning a
/// transaction. A durable delivery tier writes events inside the transaction instead and does not
/// rely on this buffer.
/// </para>
/// <example>
/// <code>
/// // Inside the application's transaction decorator:
/// if (response is not IResultLike { IsSuccess: true }) {
///     await tx.RollbackAsync(ct);
///     dispatch.Discard();
///     return response;
/// }
/// await tx.CommitAsync(ct);
/// await dispatch.FlushAsync(ct);
/// </code>
/// </example>
/// </remarks>
public interface IEventDispatchScope {
    /// <summary>
    /// Hands every buffered integration event to the delivery tier and clears the buffer.
    /// </summary>
    /// <param name="ct">A cancellation token observed while handing off events.</param>
    /// <remarks>Call only after the unit of work has durably committed.</remarks>
    ValueTask FlushAsync(CancellationToken ct = default);

    /// <summary>Drops every buffered integration event without delivering them.</summary>
    /// <remarks>Call when the unit of work rolls back.</remarks>
    void Discard();
}
