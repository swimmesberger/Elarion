namespace Elarion.Settings;

/// <summary>
/// Marks an <c>IConfigurationProvider</c> as a <i>projection</i> of the settings resolver (for example the one in
/// <c>Elarion.Settings.Configuration</c>). The resolver skips projection providers when it reads the configuration
/// layer, so the projection never feeds back into the value it is derived from.
/// </summary>
public interface ISettingsProjectionProvider;
