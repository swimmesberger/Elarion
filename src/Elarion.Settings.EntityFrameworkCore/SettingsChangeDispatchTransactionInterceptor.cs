using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Elarion.Settings.EntityFrameworkCore;

/// <summary>
/// Announces the settings changes <see cref="SettingsChangeDispatchScope"/> buffered while a caller-owned EF Core
/// transaction was open: it flushes them after the transaction commits and drops them on rollback, and keeps the
/// buffer's savepoint marks in sync so a partial rollback undoes exactly the changes made after a savepoint.
/// </summary>
/// <remarks>
/// The settings store writes through <c>ExecuteUpdate</c>/raw <c>INSERT</c> (never <c>SaveChanges</c>), so a
/// non-transactional write is already durable when the notifier runs and is announced immediately — only the
/// transactional path needs deferral, which is why (unlike the in-memory event bus) there is no companion
/// <c>SaveChanges</c> interceptor. Scoped so it shares the same <see cref="SettingsChangeDispatchScope"/> instance
/// as the notifier that buffered into it.
/// </remarks>
internal sealed class SettingsChangeDispatchTransactionInterceptor(SettingsChangeDispatchScope scope)
    : DbTransactionInterceptor {
    /// <inheritdoc />
    public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData) =>
        scope.Flush();

    /// <inheritdoc />
    public override Task TransactionCommittedAsync(
        DbTransaction transaction,
        TransactionEndEventData eventData,
        CancellationToken cancellationToken = default) {
        scope.Flush();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData) =>
        scope.Discard();

    /// <inheritdoc />
    public override Task TransactionRolledBackAsync(
        DbTransaction transaction,
        TransactionEndEventData eventData,
        CancellationToken cancellationToken = default) {
        scope.Discard();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override void CreatedSavepoint(DbTransaction transaction, TransactionEventData eventData) =>
        scope.PushSavepoint();

    /// <inheritdoc />
    public override Task CreatedSavepointAsync(
        DbTransaction transaction,
        TransactionEventData eventData,
        CancellationToken cancellationToken = default) {
        scope.PushSavepoint();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override void RolledBackToSavepoint(DbTransaction transaction, TransactionEventData eventData) =>
        scope.RollbackToSavepoint();

    /// <inheritdoc />
    public override Task RolledBackToSavepointAsync(
        DbTransaction transaction,
        TransactionEventData eventData,
        CancellationToken cancellationToken = default) {
        scope.RollbackToSavepoint();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override void ReleasedSavepoint(DbTransaction transaction, TransactionEventData eventData) =>
        scope.ReleaseSavepoint();

    /// <inheritdoc />
    public override Task ReleasedSavepointAsync(
        DbTransaction transaction,
        TransactionEventData eventData,
        CancellationToken cancellationToken = default) {
        scope.ReleaseSavepoint();
        return Task.CompletedTask;
    }
}
