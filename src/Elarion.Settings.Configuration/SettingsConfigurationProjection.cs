using System.Globalization;
using System.Text.Json;
using Elarion.Abstractions.Serialization;

namespace Elarion.Settings.Configuration;

/// <summary>
/// Projects resolved settings into flat <c>IConfiguration</c> keys. A scalar definition becomes one key; an object
/// value becomes one key per property path (<c>app:smtp:host</c>) and an array one key per index, so
/// <c>IOptionsMonitor&lt;T&gt;</c> can bind the section. Secrets are <b>never</b> projected: <c>IConfiguration</c>
/// is routinely bound, logged and dumped by diagnostics, so a protected-at-rest secret must not be handed to it;
/// read secrets through <see cref="ISettingsManager"/>. Values that deployment configuration pins are skipped (they
/// are already in <c>IConfiguration</c>), as are unreadable entries and definitions without a value.
/// </summary>
public static class SettingsConfigurationProjection {
    /// <summary>Builds the projection data for the given resolved settings.</summary>
    public static IReadOnlyDictionary<string, string?> Project(
        IEnumerable<ResolvedSetting> settings, IElarionJsonSerialization serialization) {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(serialization);

        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var setting in settings) {
            var definition = setting.Definition;
            if (definition.IsSecret || setting.IsUnreadable || setting.Source == SettingSource.Configuration) continue;

            var json = setting.Source == SettingSource.Default ? definition.SerializeDefault(serialization)
                : setting.ValueJson;
            if (json is null) continue;

            using var document = JsonDocument.Parse(json);
            Flatten(definition.Key, document.RootElement, data);
        }

        return data;
    }

    private static void Flatten(string path, JsonElement element, Dictionary<string, string?> data) {
        switch (element.ValueKind) {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                    Flatten(path + SettingsPath.Separator + property.Name, property.Value, data);
                break;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                    Flatten(path + SettingsPath.Separator + index++.ToString(CultureInfo.InvariantCulture), item, data);
                break;
            case JsonValueKind.String:
                data[path] = element.GetString();
                break;
            case JsonValueKind.Number:
                data[path] = element.GetRawText();
                break;
            case JsonValueKind.True:
                data[path] = "true";
                break;
            case JsonValueKind.False:
                data[path] = "false";
                break;
        }
    }
}
