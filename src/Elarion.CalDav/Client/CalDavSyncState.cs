using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Elarion.CalDav;

/// <summary>What the client knows of a calendar between steps: the server's sync token or CTag, and the ETag of every object.</summary>
internal sealed record CalDavSyncState {
    [JsonPropertyName("t")]
    public string? Token { get; init; }

    [JsonPropertyName("c")]
    public string? CTag { get; init; }

    [JsonPropertyName("e")]
    public Dictionary<string, string> ETags { get; init; } = new(StringComparer.Ordinal);

    public static CalDavSyncState Empty { get; } = new();

    /// <summary>Compact JSON through the source-generated context, as base64url: no reflection, so NativeAOT-safe.</summary>
    public string Serialize() {
        var json = JsonSerializer.SerializeToUtf8Bytes(this, CalDavClientJsonContext.Default.CalDavSyncState);
        return Convert.ToBase64String(json).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <exception cref="ArgumentException">The text is not a state this client wrote.</exception>
    public static CalDavSyncState Parse(string? state) {
        if (string.IsNullOrEmpty(state)) return Empty;
        try {
            var base64 = state.Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
            var parsed = JsonSerializer.Deserialize(Convert.FromBase64String(base64), CalDavClientJsonContext.Default.CalDavSyncState);
            if (parsed is null) throw new ArgumentException("The sync state is empty.", nameof(state));
            return parsed with { ETags = new Dictionary<string, string>(parsed.ETags, StringComparer.Ordinal) };
        }
        catch (Exception ex) when (ex is FormatException or JsonException) {
            throw new ArgumentException("The sync state is not one a CalDavClient wrote: " + Encoding.ASCII.GetByteCount(state) + " characters.", nameof(state), ex);
        }
    }
}

[JsonSerializable(typeof(CalDavSyncState))]
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class CalDavClientJsonContext : JsonSerializerContext;
