using Microsoft.Extensions.Primitives;

namespace Elarion.Settings;

/// <summary>
/// The listening half of the settings sink: hands out <see cref="IChangeToken"/>s that fire when matching
/// settings change. This is the consumer-facing seam that bridges cleanly to <c>IConfiguration</c> reload
/// tokens and <c>IOptionsMonitor</c>. The shipped in-process source notifies only within this process;
/// cross-instance backends (Postgres <c>LISTEN/NOTIFY</c>, Redis pub/sub) implement the same contract.
/// </summary>
public interface ISettingsChangeSource {
    /// <summary>
    /// Returns a change token that fires when a setting changes in <paramref name="scope"/> under
    /// <paramref name="keyPrefix"/> (see <see cref="SettingsPath.IsUnderPrefix"/>). A <see langword="null"/>
    /// prefix watches the whole scope. The token is one-shot — re-watch (or use
    /// <see cref="ChangeToken.OnChange(System.Func{IChangeToken}, System.Action)"/>) to keep observing.
    /// </summary>
    IChangeToken Watch(SettingsScope scope, string? keyPrefix = null);
}

/// <summary>
/// The producer side of change notification: stores call this after a successful write so the matching
/// <see cref="ISettingsChangeSource"/> tokens fire. Separating publish from watch lets a store (in-process,
/// EF Core, …) signal an in-process source, while a cross-instance backend can publish from its own transport.
/// </summary>
public interface ISettingsChangePublisher {
    /// <summary>Signals that <paramref name="key"/> changed in <paramref name="scope"/>.</summary>
    void Publish(SettingsScope scope, string key);
}
