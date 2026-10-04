using System.Globalization;
using System.Text.Json;
using Elarion.Abstractions.Serialization;

namespace Elarion.Settings.Configuration;

/// <summary>
/// Projects resolved settings into flat <c>IConfiguration</c> keys. A scalar definition becomes one key; an object
/// value becomes one key per property path (<c>app:smtp:host</c>) and an array one key per index, so
/// <c>IOptionsMonitor&lt;T&gt;</c> can bind the section.
/// </summary>
/// <remarks>
/// Only what is meaningful is projected:
/// <list type="bullet">
/// <item><description>Secrets are <b>never</b> projected: <c>IConfiguration</c> is routinely bound, logged and dumped
/// by diagnostics, so a protected-at-rest secret must not be handed to it; read secrets through
/// <see cref="ISettingsManager"/>.</description></item>
/// <item><description>Values that deployment configuration pins are skipped (they are already in
/// <c>IConfiguration</c>).</description></item>
/// <item><description>A <b>stored</b> value is always projected, structured or not.</description></item>
/// <item><description>A <b>declared default</b> is projected only when it is a scalar. A structured default (a
/// record or collection) would add a key per property that nobody set, and the binder's target type already
/// carries those defaults; it appears as soon as a value is stored.</description></item>
/// <item><description>A definition without a value is skipped.</description></item>
/// </list>
/// Projection is isolated per setting: an unreadable entry (undecryptable, or not valid for its definition's type)
/// or a value that cannot be flattened is left out and reported in <see cref="SettingsProjection.Problems"/>,
/// while every other setting is still projected. A problem carries the key and a reason, never the value.
/// </remarks>
public static class SettingsConfigurationProjection {
    /// <summary>Builds the projection data for the given resolved settings.</summary>
    public static SettingsProjection Project(
        IEnumerable<ResolvedSetting> settings, IElarionJsonSerialization serialization) {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(serialization);

        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var problems = new List<SettingsProjectionProblem>();
        foreach (var setting in settings) {
            var definition = setting.Definition;
            if (definition.IsSecret || setting.Source == SettingSource.Configuration) continue;

            if (setting.IsUnreadable) {
                problems.Add(new SettingsProjectionProblem(definition.Key,
                    setting.UnreadableReason ?? "The stored value is unreadable."));
                continue;
            }

            var isDefault = setting.Source == SettingSource.Default;
            var json = isDefault ? definition.SerializeDefault(serialization) : setting.ValueJson;
            if (json is null) continue;

            try {
                using var document = JsonDocument.Parse(json);
                if (isDefault && document.RootElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    continue;

                // Flatten into a scratch map first so a failure part-way leaves no partial subtree behind.
                var flattened = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                Flatten(definition.Key, document.RootElement, flattened);
                foreach (var pair in flattened) data[pair.Key] = pair.Value;
            }
            catch (JsonException) {
                problems.Add(new SettingsProjectionProblem(definition.Key, "The value is not valid JSON."));
            }
        }

        return new SettingsProjection(data, problems);
    }

    /// <summary>
    /// Flattens canonical JSON into <c>IConfiguration</c> keys under <paramref name="path"/>: an object becomes one
    /// key per property path and an array one key per index; scalars use their invariant text. This is the same
    /// shape <see cref="Project"/> produces, exposed so a host that reads stored settings before the container
    /// exists can build the identical keys.
    /// </summary>
    /// <exception cref="JsonException"><paramref name="json"/> is not valid JSON.</exception>
    public static void Flatten(string path, string json, IDictionary<string, string?> data) {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(data);

        using var document = JsonDocument.Parse(json);
        Flatten(path, document.RootElement, data);
    }

    private static void Flatten(string path, JsonElement element, IDictionary<string, string?> data) {
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

/// <summary>The result of a projection run: the flat configuration data plus the settings that had to be skipped.</summary>
/// <param name="Data">The flat <c>IConfiguration</c> key/value map.</param>
/// <param name="Problems">The settings left out because they could not be projected.</param>
public sealed record SettingsProjection(
    IReadOnlyDictionary<string, string?> Data, IReadOnlyList<SettingsProjectionProblem> Problems);

/// <summary>A setting that was skipped during projection. Never contains the stored value.</summary>
/// <param name="Key">The setting key.</param>
/// <param name="Reason">Why the setting was skipped.</param>
public sealed record SettingsProjectionProblem(string Key, string Reason);
