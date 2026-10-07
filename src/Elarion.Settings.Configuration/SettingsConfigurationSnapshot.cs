using Elarion.Abstractions.Serialization;
using Elarion.Abstractions.Settings;
using Microsoft.Extensions.Configuration;

namespace Elarion.Settings.Configuration;

/// <summary>
/// Reads the stored global settings <b>before the host is built</b>, for configuration that is consumed before the
/// DI container exists (web server endpoints, authentication scheme options, anything read while the builder is
/// still being assembled). It runs the same resolver and projection the running host uses, so the keys and values
/// are identical to what <see cref="SettingsConfigurationRefresher"/> publishes later — an application never has
/// to duplicate the decode and flatten logic.
/// </summary>
/// <example>
/// <code>
/// var snapshot = await SettingsConfigurationSnapshot.LoadAsync(
///     ElarionSettingDefinitions.All, store, serialization, builder.Configuration);
/// builder.AddElarionSettingsConfiguration(snapshot);   // the host's refresher takes over from the snapshot
/// </code>
/// </example>
public static class SettingsConfigurationSnapshot {
    /// <summary>
    /// Resolves every non-secret global definition against <paramref name="store"/> and projects the result. Secret
    /// definitions are skipped (they are never projected and need a protector the boot phase does not have).
    /// A stored row that is unreadable is reported in <see cref="SettingsProjection.Problems"/> and does not stop
    /// the rest from loading.
    /// </summary>
    /// <param name="definitions">The registered definitions, typically <c>ElarionSettingDefinitions.All</c>.</param>
    /// <param name="store">The settings store to read.</param>
    /// <param name="serialization">The canonical JSON options (<see cref="IElarionJsonSerialization"/>).</param>
    /// <param name="deploymentConfiguration">
    /// The configuration assembled so far, so a pinned definition is recognized and left out. May be
    /// <see langword="null"/> when nothing pins settings.
    /// </param>
    /// <param name="options">Resolver options (<see cref="SettingsOptions.EmptyConfigurationValuesPin"/>); defaults apply when omitted.</param>
    /// <param name="cancellationToken">Cancels the store read.</param>
    public static async ValueTask<SettingsProjection> LoadAsync(
        IEnumerable<SettingDefinition> definitions,
        ISettingsStore store,
        IElarionJsonSerialization serialization,
        IConfiguration? deploymentConfiguration = null,
        SettingsOptions? options = null,
        CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(serialization);

        var snapshotOptions = new SettingsOptions {
            EmptyConfigurationValuesPin = options?.EmptyConfigurationValuesPin ?? false
        };
        snapshotOptions.AddDefinitions(definitions.Where(static d => !d.IsSecret));

        var catalog = new SettingDefinitionCatalog(snapshotOptions);
        var pins = new SettingPins(catalog, snapshotOptions, deploymentConfiguration);
        var resolver = new SettingResolver(catalog, store, pins, serialization);
        var resolved = await resolver.ResolveAllAsync(SettingsScope.Global, null, cancellationToken)
            .ConfigureAwait(false);
        return SettingsConfigurationProjection.Project(resolved, serialization, deploymentConfiguration);
    }

    /// <summary>
    /// Adds a boot snapshot to a configuration builder as a projection provider, for a host that does not use
    /// <c>AddElarionSettingsConfiguration</c>. The data is static; to keep it live, use
    /// <c>AddElarionSettingsConfiguration(snapshot)</c> instead, which seeds the live provider with it. The
    /// provider is an <see cref="ISettingsProjectionProvider"/>, so it can never pin a definition.
    /// </summary>
    public static IConfigurationBuilder AddElarionSettingsSnapshot(
        this IConfigurationBuilder builder, SettingsProjection snapshot) {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(snapshot);
        return builder.Add(new SettingsConfigurationSource(snapshot.Data));
    }
}
