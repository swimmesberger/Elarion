using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;

namespace Elarion.Abstractions.Substitution;

/// <summary>
/// An <see cref="IVariableSource"/> backed by <see cref="IConfiguration"/>. Because the settings
/// <c>IConfiguration</c> provider (and any other provider) feed the same configuration, pointing variable
/// substitution at configuration transparently picks up settings, environment variables, and appsettings —
/// with runtime changes when the underlying provider reloads. It is observable: <see cref="Watch"/> bridges
/// to the configuration reload token, so a provider reload propagates to consumers.
/// </summary>
public sealed class ConfigurationVariableSource(IConfiguration configuration) : IObservableVariableSource {
    /// <inheritdoc />
    public bool TryGetValue(string key, out string? value) {
        value = configuration[key];
        return value is not null;
    }

    /// <inheritdoc />
    public IChangeToken Watch() => configuration.GetReloadToken();
}
