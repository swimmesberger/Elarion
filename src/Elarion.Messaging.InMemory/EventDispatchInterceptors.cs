using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Elarion.Messaging.InMemory;

/// <summary>
/// Flushes the scoped integration-event buffer after an autocommit <c>SaveChanges</c> (no ambient transaction), and
/// discards it when <c>SaveChanges</c> fails.
/// </summary>
/// <remarks>
/// Drives <see cref="EventDispatchScope"/> from the DbContext lifecycle so the in-memory integration tier delivers
/// after commit without the host wiring a decorator. When the command runs inside an explicit transaction, the commit
/// is owned by <see cref="EventDispatchTransactionInterceptor"/> instead, so this interceptor flushes only when no
/// transaction is active.
/// </remarks>
internal sealed class EventDispatchSaveChangesInterceptor(EventDispatchScope scope) : SaveChangesInterceptor {
    /// <inheritdoc />
    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default) {
        if (eventData.Context?.Database.CurrentTransaction is null)
            await scope.FlushAsync(cancellationToken).ConfigureAwait(false);

        return result;
    }

    /// <inheritdoc />
    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result) {
        if (eventData.Context?.Database.CurrentTransaction is null) scope.FlushSynchronously();

        return result;
    }

    /// <inheritdoc />
    public override void SaveChangesFailed(DbContextErrorEventData eventData) {
        scope.Discard();
    }

    /// <inheritdoc />
    public override Task SaveChangesFailedAsync(
        DbContextErrorEventData eventData,
        CancellationToken cancellationToken = default) {
        scope.Discard();
        return Task.CompletedTask;
    }
}

/// <summary>
/// Flushes the scoped integration-event buffer after an explicit transaction commits, and discards it on rollback.
/// </summary>
/// <remarks>
/// Pairs with <see cref="EventDispatchSaveChangesInterceptor"/>: together they cover both the autocommit and the
/// explicit-transaction cases, so buffered integration events are delivered exactly once the unit of work durably
/// commits and dropped when it does not.
/// <para>
/// It is also <b>savepoint-aware</b>: the created/rolled-back-to/released-savepoint callbacks keep the buffer's
/// high-water marks in sync so a partial rollback to a savepoint (e.g. the idempotency decorator undoing a failed
/// command's business writes while still committing the outer transaction to persist the failure record) truncates
/// exactly the events buffered after that savepoint, rather than delivering events for state that never persisted.
/// The EF Core interceptor callbacks carry no savepoint name, so the marks form a LIFO stack — see
/// <see cref="EventDispatchScope"/>.
/// </para>
/// </remarks>
internal sealed class EventDispatchTransactionInterceptor(EventDispatchScope scope) : DbTransactionInterceptor {
    /// <inheritdoc />
    public override async Task TransactionCommittedAsync(
        DbTransaction transaction,
        TransactionEndEventData eventData,
        CancellationToken cancellationToken = default) {
        await scope.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData) {
        scope.FlushSynchronously();
    }

    /// <inheritdoc />
    public override Task TransactionRolledBackAsync(
        DbTransaction transaction,
        TransactionEndEventData eventData,
        CancellationToken cancellationToken = default) {
        scope.Discard();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData) {
        scope.Discard();
    }

    /// <inheritdoc />
    public override void CreatedSavepoint(DbTransaction transaction, TransactionEventData eventData) {
        scope.PushSavepoint();
    }

    /// <inheritdoc />
    public override Task CreatedSavepointAsync(
        DbTransaction transaction,
        TransactionEventData eventData,
        CancellationToken cancellationToken = default) {
        scope.PushSavepoint();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override void RolledBackToSavepoint(DbTransaction transaction, TransactionEventData eventData) {
        scope.RollbackToSavepoint();
    }

    /// <inheritdoc />
    public override Task RolledBackToSavepointAsync(
        DbTransaction transaction,
        TransactionEventData eventData,
        CancellationToken cancellationToken = default) {
        scope.RollbackToSavepoint();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override void ReleasedSavepoint(DbTransaction transaction, TransactionEventData eventData) {
        scope.ReleaseSavepoint();
    }

    /// <inheritdoc />
    public override Task ReleasedSavepointAsync(
        DbTransaction transaction,
        TransactionEventData eventData,
        CancellationToken cancellationToken = default) {
        scope.ReleaseSavepoint();
        return Task.CompletedTask;
    }
}
