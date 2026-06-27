using Microsoft.Extensions.Configuration;

namespace Elarion.Settings.Configuration;

/// <summary>
/// The <see cref="IConfigurationSource"/> for settings-backed configuration. It owns a single
/// <see cref="SettingsConfigurationProvider"/> instance, which is also registered in DI so the
/// <see cref="SettingsConfigurationRefresher"/> can push data into the very provider the configuration
/// system uses.
/// </summary>
public sealed class SettingsConfigurationSource : IConfigurationSource {
    /// <summary>The provider built by this source; shared with DI for refresh.</summary>
    public SettingsConfigurationProvider Provider { get; } = new();

    /// <inheritdoc />
    public IConfigurationProvider Build(IConfigurationBuilder builder) => Provider;
}
