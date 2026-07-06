namespace Elarion.Blobs.AspNetCore;

/// <summary>
/// Configures the direct blob-transfer endpoints: the upload/cancel surface mapped by
/// <c>MapElarionBlobUploads</c> and the owner-scoped download mapped by <c>MapElarionBlobDownloads</c>
/// (which shares the prefix and container; the upload-specific limits do not apply to downloads).
/// </summary>
public sealed class BlobUploadEndpointOptions {
    /// <summary>The route prefix the transfer endpoints are mapped under. Defaults to <c>/_elarion/blobs</c>.</summary>
    public string RoutePrefix { get; set; } = "/_elarion/blobs";

    /// <summary>The blob container uploads are stored in. Defaults to <c>uploads</c>.</summary>
    public string Container { get; set; } = "uploads";

    /// <summary>
    /// How long an uploaded blob stays pending before it is eligible for garbage collection. Defaults to
    /// 30 minutes. The blob is kept once an application commits it via <see cref="IBlobLifecycle"/>.
    /// </summary>
    public TimeSpan Ttl { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// The maximum accepted upload size in bytes, or <c>null</c> for no limit. Defaults to 25 MiB.
    /// </summary>
    public long? MaxContentLength { get; set; } = 25L * 1024 * 1024;

    /// <summary>
    /// The set of accepted content types (case-insensitive), or <c>null</c>/empty to accept any. Compared
    /// against the upload's media type with parameters such as <c>; charset</c> stripped.
    /// </summary>
    public IReadOnlyCollection<string>? AllowedContentTypes { get; set; }
}
