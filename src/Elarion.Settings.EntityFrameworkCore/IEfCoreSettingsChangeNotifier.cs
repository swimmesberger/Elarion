using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Elarion.Settings.EntityFrameworkCore;

/// <summary>
/// How <see cref="EfCoreSettingsStore{TDbContext}"/> signals a successful write to watchers. The store hands the
/// notifier its <see cref="DbContext"/> so a backend-aware implementation can publish <b>through the store's own
/// connection</b> — the PostgreSQL implementation issues <c>NOTIFY</c> on that connection, which PostgreSQL makes
/// transactional: inside a caller-owned transaction the notification is delivered only on commit and discarded on
/// rollback, so watchers never observe a value that was rolled back, and transactional writes are no longer
/// silently unnotified.
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
/// The default notifier over the in-process <see cref="ISettingsChangePublisher"/>. It signals immediately after a
/// non-transactional write, and <b>skips</b> a write running inside a caller-owned ambient transaction — signalling
/// immediately would fire watchers for a value a later rollback discards (a phantom notification). A
/// transaction-aware backend (the PostgreSQL <c>LISTEN/NOTIFY</c> change source) replaces this with a notifier
/// whose delivery is commit-gated by the database itself.
/// </summary>
internal sealed class ChangePublisherSettingsChangeNotifier(
    ISettingsChangePublisher changePublisher,
    ILogger<ChangePublisherSettingsChangeNotifier> logger) : IEfCoreSettingsChangeNotifier {
    /// <inheritdoc />
    public ValueTask NotifyAsync(DbContext dbContext, SettingsScope scope, string key, CancellationToken cancellationToken) {
        if (dbContext.Database.CurrentTransaction is not null) {
            logger.LogDebug(
                "Skipping settings change notification for key '{Key}' in scope '{Scope}' because the write is " +
                "inside a caller-owned transaction; register a commit-hooked change source (for example the " +
                "PostgreSQL LISTEN/NOTIFY source) to notify transactional writes.",
                key,
                scope.Kind);
            return ValueTask.CompletedTask;
        }

        changePublisher.Publish(scope, key);
        return ValueTask.CompletedTask;
    }
}
