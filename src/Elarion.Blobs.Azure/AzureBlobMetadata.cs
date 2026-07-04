using System.Globalization;
using System.Text;

namespace Elarion.Blobs.Azure;

/// <summary>
/// The blob-metadata vocabulary shared by <see cref="AzureBlobStore"/> and
/// <see cref="AzureStagedUploadStore"/> — lifecycle state and expiry on stored blobs, session state on
/// staging append blobs. Free-form values (names, owner ids, transport metadata) are Base64-encoded
/// because Azure metadata values must be ASCII header values.
/// </summary>
internal static class AzureBlobMetadata {
    // Stored-blob lifecycle keys.
    public const string StateKey = "elarion_state";
    public const string PendingState = "pending";
    public const string CommittedState = "committed";
    public const string ExpiresAtKey = "elarion_expires_at";
    public const string OwnerKey = "elarion_owner";

    // Staging-session keys.
    public const string ContainerKey = "elarion_container";
    public const string NameKey = "elarion_name";
    public const string LengthKey = "elarion_length";
    public const string TransportMetadataKey = "elarion_metadata";
    public const string BlobRefKey = "elarion_blob_ref";
    public const string FinalOffsetKey = "elarion_final_offset";

    public static string Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    public static string? Decode(IDictionary<string, string> metadata, string key) =>
        metadata.TryGetValue(key, out var encoded)
            ? Encoding.UTF8.GetString(Convert.FromBase64String(encoded))
            : null;

    public static string FormatInstant(DateTimeOffset instant) =>
        instant.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    public static DateTimeOffset? ParseInstant(IDictionary<string, string> metadata, string key) =>
        metadata.TryGetValue(key, out var value)
            && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var instant)
            ? instant
            : null;

    public static long? ParseLong(IDictionary<string, string> metadata, string key) =>
        metadata.TryGetValue(key, out var value)
            && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    public static string FormatLong(long value) => value.ToString(CultureInfo.InvariantCulture);

    public static bool IsPending(IDictionary<string, string> metadata) =>
        metadata.TryGetValue(StateKey, out var state)
        && string.Equals(state, PendingState, StringComparison.Ordinal);
}
