using System.Transactions;
using Microsoft.EntityFrameworkCore;

namespace Elarion.Settings.EntityFrameworkCore;

/// <summary>
/// How <see cref="EfCoreSettingsStore{TDbContext}"/> signals a successful write to watchers. The store hands the
/// notifier its <see cref="DbContext"/> so the notification can be commit-gated by the caller's transaction: a
/// backend-aware implementation can publish <b>through the store's own connection</b> — the PostgreSQL
/// implementation issues <c>NOTIFY</c> on that connection, which PostgreSQL makes transactional and cross-instance —
/// while the in-process default reads the context's transaction lifecycle to defer an in-process notification until
/// commit. Either way, inside a caller-owned transaction the notification is delivered only on commit and dropped on
/// rollback, so watchers never observe a value that was rolled back and a transactional write is never silently
/// unnotified.
/// </summary>
public interface IEfCoreSettingsChangeNotifier {
    /// <summary>Signals that <paramref name="key"/> changed in <paramref name="scope"/> after a successful store write.</summary>
    /// <param name="dbContext">The context the write ran on; its current connection/transaction may carry the notification.</param>
    /// <param name="scope">The scope the setting changed in.</param>
    /// <param name="key">The changed setting key.</param>
    /// <param name="cancellationToken">A token to cancel the notification.</param>
    ValueTask NotifyAsync(DbContext dbContext, SettingsScope scope, string key, CancellationToken cancellationToken);
}

/// <summary>
/// The default notifier over the in-process <see cref="ISettingsChangePublisher"/>. A write with no ambient
/// transaction is already durable, so it is announced immediately; a write inside a caller-owned transaction is
/// deferred into the scoped <see cref="SettingsChangeDispatchScope"/> and announced by the
/// <see cref="SettingsChangeDispatchTransactionInterceptor"/> only after the transaction commits (and dropped on
/// rollback), so watchers never observe a value a rollback discards. A write inside a System.Transactions ambient
/// transaction (<c>TransactionScope</c>) is likewise commit-gated, via the ambient transaction's completion event.
/// A backend-aware notifier (the PostgreSQL <c>LISTEN/NOTIFY</c> change source) replaces this with one whose
/// delivery the database commit-gates and which also crosses process boundaries.
/// </summary>
internal sealed class ChangePublisherSettingsChangeNotifier(SettingsChangeDispatchScope dispatch)
    : IEfCoreSettingsChangeNotifier {
    /// <inheritdoc />
    public ValueTask NotifyAsync(DbContext dbContext, SettingsScope scope, string key,
        CancellationToken cancellationToken) {
        if (dbContext.Database.CurrentTransaction is not null)
            // Inside a caller-owned transaction: defer until commit so a rollback drops the notification rather than
            // announcing a phantom change. The dispatch interceptor flushes the buffer once the transaction commits.
            dispatch.Defer(scope, key);
        else if (Transaction.Current is { } ambient)
            // Inside a System.Transactions ambient transaction (TransactionScope): the write is enlisted and not yet
            // durable, but EF exposes no DbTransaction, so the dispatch interceptor would never flush a deferral.
            // Announce on the ambient transaction's own completion instead — committed publishes, aborted drops — so
            // the notification neither fires pre-commit nor survives a rollback.
            ambient.TransactionCompleted += (_, args) => {
                if (args.Transaction?.TransactionInformation.Status is TransactionStatus.Committed)
                    dispatch.PublishNow(scope, key);
            };
        else
            // No ambient transaction: the ExecuteUpdate/raw INSERT already committed, so announce immediately.
            dispatch.PublishNow(scope, key);

        return ValueTask.CompletedTask;
    }
}
