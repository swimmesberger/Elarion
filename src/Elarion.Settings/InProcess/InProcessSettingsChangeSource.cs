using System.Collections.Concurrent;
using Microsoft.Extensions.Primitives;

namespace Elarion.Settings.InProcess;

/// <summary>
/// In-process <see cref="ISettingsChangeSource"/> and <see cref="ISettingsChangePublisher"/>. Hands out a
/// swappable change token per <c>(scope, prefix)</c> watch and fires the ones matching each publish. Only
/// changes made within this process are observed — a cross-instance backend (Postgres <c>LISTEN/NOTIFY</c>,
/// Redis pub/sub) is a drop-in replacement that publishes from its own transport.
/// </summary>
public sealed class InProcessSettingsChangeSource : ISettingsChangeSource, ISettingsChangePublisher {
    private readonly ConcurrentDictionary<WatchKey, TokenHolder> _holders = new();

    /// <inheritdoc />
    public IChangeToken Watch(SettingsScope scope, string? keyPrefix = null) {
        var holder = _holders.GetOrAdd(new WatchKey(scope, keyPrefix ?? string.Empty), static _ => new TokenHolder());
        return holder.GetToken();
    }

    /// <inheritdoc />
    public void Publish(SettingsScope scope, string key) {
        ArgumentNullException.ThrowIfNull(key);
        foreach (var (watchKey, holder) in _holders) {
            if (watchKey.Scope == scope && SettingsPath.IsUnderPrefix(key, watchKey.Prefix)) {
                holder.Fire();
            }
        }
    }

    private readonly record struct WatchKey(SettingsScope Scope, string Prefix);

    /// <summary>
    /// Holds the current one-shot token for a watch. Firing cancels the active source (signalling every
    /// registered token) and swaps in a fresh one, so the next <see cref="GetToken"/> observes future changes —
    /// the same swap-on-reload pattern as <c>ConfigurationReloadToken</c>.
    /// </summary>
    private sealed class TokenHolder {
        private CancellationTokenSource _cts = new();

        public IChangeToken GetToken() => new CancellationChangeToken(Volatile.Read(ref _cts).Token);

        public void Fire() {
            var previous = Interlocked.Exchange(ref _cts, new CancellationTokenSource());
            previous.Cancel();
        }
    }
}
