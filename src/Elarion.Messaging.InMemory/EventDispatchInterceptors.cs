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
        if (eventData.Context?.Database.CurrentTransaction is null) {
            await scope.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    /// <inheritdoc />
    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result) {
        if (eventData.Context?.Database.CurrentTransaction is null) {
            scope.FlushAsync().AsTask().GetAwaiter().GetResult();
        }

        return result;
    }

    /// <inheritdoc />
    public override void SaveChangesFailed(DbContextErrorEventData eventData) => scope.Discard();

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
/// </remarks>
internal sealed class EventDispatchTransactionInterceptor(EventDispatchScope scope) : DbTransactionInterceptor {
    /// <inheritdoc />
    public override async Task TransactionCommittedAsync(
        DbTransaction transaction,
        TransactionEndEventData eventData,
        CancellationToken cancellationToken = default) =>
        await scope.FlushAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData) =>
        scope.FlushAsync().AsTask().GetAwaiter().GetResult();

    /// <inheritdoc />
    public override Task TransactionRolledBackAsync(
        DbTransaction transaction,
        TransactionEndEventData eventData,
        CancellationToken cancellationToken = default) {
        scope.Discard();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData) =>
        scope.Discard();
}
