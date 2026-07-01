using Elarion.Settings.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Elarion.Settings.PostgreSql;

/// <summary>
/// The <see cref="IEfCoreSettingsChangeNotifier"/> for the PostgreSQL change source: issues <c>pg_notify</c>
/// <b>on the store's own connection</b>, so PostgreSQL's transactional notification semantics apply — a write
/// inside a caller-owned transaction is announced only when that transaction commits, and never announced on
/// rollback. This closes the gap the default notifier documents (transactional writes skipped): with this
/// backend every successful settings write reaches every node's watchers, commit-gated by the database itself.
/// </summary>
/// <remarks>
/// The store's <c>DbContext</c> must target the same PostgreSQL database the change listener is connected to —
/// true by construction when the settings table and the change source share the application's database.
/// </remarks>
internal sealed class PostgreSqlTransactionalSettingsChangeNotifier(
    PostgreSqlSettingsChangeOptions options) : IEfCoreSettingsChangeNotifier {
    /// <inheritdoc />
    public async ValueTask NotifyAsync(
        DbContext dbContext,
        SettingsScope scope,
        string key,
        CancellationToken cancellationToken) {
        var payload = PostgreSqlSettingsChangePayload.Serialize(scope, key);
        await dbContext.Database
            .ExecuteSqlRawAsync("SELECT pg_notify({0}, {1})", [options.ChannelName, payload], cancellationToken)
            .ConfigureAwait(false);
    }
}
