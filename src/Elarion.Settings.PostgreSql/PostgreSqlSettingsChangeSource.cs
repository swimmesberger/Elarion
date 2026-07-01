using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Npgsql;

namespace Elarion.Settings.PostgreSql;

/// <summary>
/// Cross-instance <see cref="ISettingsChangeSource"/> and <see cref="ISettingsChangePublisher"/> over PostgreSQL
/// <c>LISTEN/NOTIFY</c>: a settings write on any node fires the matching <see cref="IChangeToken"/> watchers on
/// every node. Watch tokens are fired exclusively from the notification loop (the
/// <see cref="PostgreSqlSettingsChangeListener"/> hosted service) — a local publish loops back through the
/// database like any remote one, so delivery is a single consistent, commit-ordered path.
/// </summary>
/// <remarks>
/// <see cref="Publish"/> sends <c>pg_notify</c> on a dedicated pooled connection and is <b>best-effort</b>: a
/// database failure is logged, never thrown, because the write it announces has already succeeded. The EF Core
/// settings store does not go through <see cref="Publish"/> at all — it notifies through
/// <c>PostgreSqlTransactionalSettingsChangeNotifier</c> on its own connection, so a transactional write is
/// delivered only on commit.
/// </remarks>
public sealed class PostgreSqlSettingsChangeSource : ISettingsChangeSource, ISettingsChangePublisher, IDisposable, IAsyncDisposable {
    private readonly ConcurrentDictionary<WatchKey, TokenHolder> _holders = new();
    private readonly bool _ownsDataSource;
    private readonly PostgreSqlSettingsChangeOptions _options;
    private readonly ILogger<PostgreSqlSettingsChangeSource> _logger;

    internal PostgreSqlSettingsChangeSource(
        NpgsqlDataSource dataSource,
        bool ownsDataSource,
        PostgreSqlSettingsChangeOptions options,
        ILogger<PostgreSqlSettingsChangeSource> logger) {
        DataSource = dataSource;
        _ownsDataSource = ownsDataSource;
        _options = options;
        _logger = logger;
    }

    internal NpgsqlDataSource DataSource { get; }

    /// <inheritdoc />
    public IChangeToken Watch(SettingsScope scope, string? keyPrefix = null) {
        var holder = _holders.GetOrAdd(new WatchKey(scope, keyPrefix ?? string.Empty), static _ => new TokenHolder());
        return holder.GetToken();
    }

    /// <inheritdoc />
    public void Publish(SettingsScope scope, string key) {
        ArgumentNullException.ThrowIfNull(key);

        try {
            using var command = DataSource.CreateCommand("SELECT pg_notify($1, $2)");
            command.Parameters.Add(new NpgsqlParameter { Value = _options.ChannelName });
            command.Parameters.Add(new NpgsqlParameter { Value = PostgreSqlSettingsChangePayload.Serialize(scope, key) });
            command.ExecuteNonQuery();
        }
        catch (NpgsqlException exception) {
            _logger.LogWarning(
                exception,
                "Failed to publish the settings change notification for key '{Key}' in scope '{Scope}'; other " +
                "nodes will not observe this change until the next successful notification.",
                key,
                scope.Kind);
        }
    }

    /// <summary>Fires every watch whose scope and prefix match a received notification.</summary>
    internal void FireMatching(SettingsScope scope, string key) {
        foreach (var (watchKey, holder) in _holders) {
            if (watchKey.Scope == scope && SettingsPath.IsUnderPrefix(key, watchKey.Prefix)) {
                holder.Fire();
            }
        }
    }

    /// <summary>
    /// Fires every registered watch. Called by the listener after it re-establishes a dropped connection:
    /// notifications sent while disconnected are gone (PostgreSQL does not queue for absent listeners), so a
    /// blanket re-read is the only way watchers converge on the current state. A spurious reload is cheap and
    /// always safe — watchers re-read through the store.
    /// </summary>
    internal void FireAll() {
        foreach (var holder in _holders.Values) {
            holder.Fire();
        }
    }

    /// <inheritdoc />
    public void Dispose() {
        if (_ownsDataSource) {
            DataSource.Dispose();
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() {
        if (_ownsDataSource) {
            return DataSource.DisposeAsync();
        }

        return ValueTask.CompletedTask;
    }

    private readonly record struct WatchKey(SettingsScope Scope, string Prefix);

    // The same swap-on-fire token holder as the in-process source: firing cancels the active source (signalling
    // every registered token) and swaps in a fresh one, so the next GetToken observes future changes.
    private sealed class TokenHolder {
        private CancellationTokenSource _cts = new();

        public IChangeToken GetToken() => new CancellationChangeToken(Volatile.Read(ref _cts).Token);

        public void Fire() {
            var previous = Interlocked.Exchange(ref _cts, new CancellationTokenSource());
            previous.Cancel();
        }
    }
}
