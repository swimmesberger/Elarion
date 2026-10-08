using Microsoft.Extensions.Primitives;

namespace Elarion.Settings.Configuration;

/// <summary>
/// The decrypted values of the secret settings an application binds onto options
/// (<see cref="SettingsOptionsBuilderExtensions.BindSecretSetting{TOptions,T}"/>), refreshed by
/// <see cref="SettingsConfigurationRefresher"/> from the resolution it already performs. Only keys that were asked for
/// are kept, and they never enter <c>IConfiguration</c>, which is routinely bound, logged and dumped.
/// </summary>
internal sealed class SettingsSecretValues {
    private readonly HashSet<string> _keys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();
    private IReadOnlyDictionary<string, string> _values = new Dictionary<string, string>();
    private CancellationTokenSource _changed = new();

    /// <summary>Asks the refresher to keep <paramref name="key"/>.</summary>
    public void Track(string key) {
        lock (_gate) _keys.Add(key);
    }

    /// <summary>The canonical JSON of the stored secret, or <see langword="null"/> when it is unset or unreadable.</summary>
    public string? GetJson(string key) {
        return _values.TryGetValue(key, out var json) ? json : null;
    }

    /// <summary>Fires when a tracked secret changed.</summary>
    public IChangeToken GetChangeToken() {
        lock (_gate) return new CancellationChangeToken(_changed.Token);
    }

    /// <summary>Takes the tracked secrets from a global resolution; signals only when one of them changed.</summary>
    public void Apply(IEnumerable<ResolvedSetting> resolved) {
        CancellationTokenSource previous;
        lock (_gate) {
            if (_keys.Count == 0) return;

            var next = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var setting in resolved)
                if (setting is { Definition.IsSecret: true, IsUnreadable: false, ValueJson: { } json }
                    && _keys.Contains(setting.Definition.Key))
                    next[setting.Definition.Key] = json;

            if (next.Count == _values.Count
                && next.All(pair => _values.TryGetValue(pair.Key, out var current)
                                    && string.Equals(current, pair.Value, StringComparison.Ordinal)))
                return;

            _values = next;
            previous = _changed;
            _changed = new CancellationTokenSource();
        }

        // Not disposed: a consumer may still register on a token handed out before the swap, which must then fire.
        previous.Cancel();
    }
}
