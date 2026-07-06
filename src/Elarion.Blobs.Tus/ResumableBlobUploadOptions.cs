namespace Elarion.Blobs.Tus;

/// <summary>
/// Configures the resumable blob-upload endpoints mapped by <c>MapElarionResumableBlobUploads</c> (the
/// tus 1.0 adapter). All upload policy lives here — the staging store receives the resulting instants as data.
/// </summary>
public sealed class ResumableBlobUploadOptions {
    /// <summary>The route prefix the tus endpoints are mapped under. Defaults to <c>/_elarion/blobs/tus</c>.</summary>
    public string RoutePrefix { get; set; } = "/_elarion/blobs/tus";

    /// <summary>The blob container completed uploads are stored in. Defaults to <c>uploads</c>.</summary>
    public string Container { get; set; } = "uploads";

    /// <summary>
    /// How long the completed blob stays pending before it is eligible for garbage collection. Defaults to
    /// 30 minutes. The blob is kept once an application commits it via <see cref="IBlobLifecycle"/>.
    /// </summary>
    public TimeSpan Ttl { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How long an incomplete upload session is retained before it is reclaimed (the tus
    /// <c>Upload-Expires</c> window). Defaults to 24 hours.
    /// </summary>
    public TimeSpan UploadExpiry { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// How long a completed upload session remains queryable after completion, so a client's
    /// <c>HEAD</c> can still fetch the <c>Elarion-Blob-Ref</c> header before the session record is
    /// reclaimed. Defaults to 1 hour.
    /// </summary>
    public TimeSpan CompletedSessionRetention { get; set; } = TimeSpan.FromHours(1);

    /// <summary>The maximum accepted upload size in bytes (the tus <c>Tus-Max-Size</c>), or <c>null</c> for no limit. Defaults to 100 MiB.</summary>
    public long? MaxSize { get; set; } = 100L * 1024 * 1024;
}
