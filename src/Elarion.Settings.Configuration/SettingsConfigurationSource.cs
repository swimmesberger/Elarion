using Microsoft.Extensions.Configuration;

namespace Elarion.Settings.Configuration;

/// <summary>
/// The <see cref="IConfigurationSource"/> for settings-backed configuration. It owns a single
/// <see cref="SettingsConfigurationProvider"/> instance, which is also registered in DI so the
/// <see cref="SettingsConfigurationRefresher"/> can push data into the very provider the configuration
/// system uses.
/// </summary>
public sealed class SettingsConfigurationSource : IConfigurationSource {
    /// <summary>Creates the source, optionally seeded with a boot snapshot (see <see cref="SettingsConfigurationSnapshot"/>).</summary>
    /// <param name="initialData">Data the provider holds from the start, before the host's refresher first loads.</param>
    public SettingsConfigurationSource(IReadOnlyDictionary<string, string?>? initialData = null) {
        if (initialData is not null) Provider.Apply(initialData);
    }

    /// <summary>The provider built by this source; shared with DI for refresh.</summary>
    public SettingsConfigurationProvider Provider { get; } = new();

    /// <inheritdoc />
    public IConfigurationProvider Build(IConfigurationBuilder builder) {
        return Provider;
    }
}
