using Microsoft.Extensions.Hosting;

namespace Elarion.Settings;

/// <summary>
/// Fails host startup when the settings registration is inconsistent: resolving the catalog validates unique keys
/// and that an <see cref="ISettingValueProtector"/> exists whenever a secret definition is registered, so a
/// misconfiguration surfaces at start instead of at the first secret write.
/// </summary>
internal sealed class SettingsStartupValidator(ISettingDefinitionCatalog catalog) : IHostedService {
    public Task StartAsync(CancellationToken cancellationToken) {
        _ = catalog.All;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) {
        return Task.CompletedTask;
    }
}
