using System.Text.Json;
using System.Text.Json.Serialization;

namespace Elarion.Settings.PostgreSql;

/// <summary>
/// The JSON payload carried on the notification channel: the changed setting's scope and key. Small by
/// construction (scope kinds, owners, and keys are length-capped by the store schema), so it always fits
/// PostgreSQL's ~8000-byte notification payload limit.
/// </summary>
internal sealed record PostgreSqlSettingsChangePayload {
    public required string Kind { get; init; }

    public string? Owner { get; init; }

    public required string Key { get; init; }

    public static string Serialize(SettingsScope scope, string key) =>
        JsonSerializer.Serialize(
            new PostgreSqlSettingsChangePayload { Kind = scope.Kind, Owner = scope.Owner, Key = key },
            PostgreSqlSettingsJsonContext.Default.PostgreSqlSettingsChangePayload);

    public static bool TryDeserialize(string payload, out SettingsScope scope, out string key) {
        try {
            var parsed = JsonSerializer.Deserialize(
                payload, PostgreSqlSettingsJsonContext.Default.PostgreSqlSettingsChangePayload);
            if (parsed is { Kind.Length: > 0, Key: not null }) {
                scope = new SettingsScope(parsed.Kind, parsed.Owner);
                key = parsed.Key;
                return true;
            }
        }
        catch (JsonException) {
            // Fall through to the failure result; the caller logs the malformed payload.
        }

        scope = default;
        key = string.Empty;
        return false;
    }
}

[JsonSerializable(typeof(PostgreSqlSettingsChangePayload))]
internal sealed partial class PostgreSqlSettingsJsonContext : JsonSerializerContext;
