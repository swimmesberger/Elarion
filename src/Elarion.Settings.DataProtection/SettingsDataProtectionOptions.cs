namespace Elarion.Settings.DataProtection;

/// <summary>Options for <see cref="SettingsDataProtectionServiceCollectionExtensions.AddElarionSettingsDataProtection"/>.</summary>
public sealed class SettingsDataProtectionOptions {
    /// <summary>
    /// Whether a hosted service re-protects the global secret settings once at host start: legacy plaintext
    /// becomes protected and payloads under a rotated key move to the current key. The step is idempotent,
    /// version-guarded, and never fails startup (a failure is logged). The default is <see langword="true"/>;
    /// turn it off to run <see cref="ISettingReprotector"/> yourself, for example from a maintenance command.
    /// </summary>
    public bool ReprotectOnStartup { get; set; } = true;

    /// <summary>The bound on the startup re-protection so a slow store cannot hang host start. Defaults to 30 seconds.</summary>
    public TimeSpan ReprotectTimeout { get; set; } = TimeSpan.FromSeconds(30);
}
