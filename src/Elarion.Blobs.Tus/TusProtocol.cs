using System.Text;

namespace Elarion.Blobs.Tus;

/// <summary>tus 1.0 protocol constants.</summary>
internal static class TusProtocol {
    public const string Version = "1.0.0";
    public const string OffsetContentType = "application/offset+octet-stream";
    public const string Extensions = "creation,expiration,termination";

    public const string Resumable = "Tus-Resumable";
    public const string VersionHeader = "Tus-Version";
    public const string Extension = "Tus-Extension";
    public const string MaxSize = "Tus-Max-Size";

    public const string UploadLength = "Upload-Length";
    public const string UploadOffset = "Upload-Offset";
    public const string UploadMetadata = "Upload-Metadata";
    public const string UploadExpires = "Upload-Expires";

    /// <summary>
    /// Non-standard response header carrying the produced blob reference once an upload completes, so the
    /// client can pass it when creating the owning entity. Expose it via CORS in cross-origin setups.
    /// </summary>
    public const string BlobRef = "Elarion-Blob-Ref";
}

/// <summary>
/// Parses the tus <c>Upload-Metadata</c> header — comma-separated <c>key base64(value)</c> pairs.
/// </summary>
internal static class TusMetadata {
    public static IReadOnlyDictionary<string, string> Parse(string? header) {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(header)) {
            return result;
        }

        foreach (var pair in header.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
            var space = pair.IndexOf(' ');
            if (space < 0) {
                result[pair] = string.Empty;
                continue;
            }

            var key = pair[..space];
            var encoded = pair[(space + 1)..];
            result[key] = TryDecodeBase64(encoded, out var value) ? value : string.Empty;
        }

        return result;
    }

    private static bool TryDecodeBase64(string encoded, out string value) {
        try {
            value = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            return true;
        }
        catch (FormatException) {
            value = string.Empty;
            return false;
        }
    }
}
