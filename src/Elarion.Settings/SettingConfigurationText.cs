using System.Text.Json;

namespace Elarion.Settings;

/// <summary>
/// Maps between the text form of a setting in <c>IConfiguration</c> and canonical JSON. A string setting's
/// configuration value is the string itself; every other type uses its JSON form (<c>25</c>, <c>true</c>, a JSON
/// document for a record), with bare words accepted for enum and string-like types (<c>Red</c>,
/// <c>00:00:05</c>).
/// </summary>
internal static class SettingConfigurationText {
    public static string ToJson(SettingDefinition definition, string raw) {
        var type = Nullable.GetUnderlyingType(definition.ValueType) ?? definition.ValueType;
        if (type == typeof(string)) return QuoteString(raw);

        if (type == typeof(bool) && bool.TryParse(raw, out var boolean)) return boolean ? "true" : "false";

        try {
            using var _ = JsonDocument.Parse(raw);
            return raw;
        }
        catch (JsonException) {
            return QuoteString(raw);
        }
    }

    public static string QuoteString(string value) {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) {
            writer.WriteStringValue(value);
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
}
