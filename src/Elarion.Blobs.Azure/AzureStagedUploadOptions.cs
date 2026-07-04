namespace Elarion.Blobs.Azure;

/// <summary>
/// Configures the Azure append-blob staging store.
/// </summary>
public sealed class AzureStagedUploadOptions {
    /// <summary>
    /// The container in-progress uploads are staged in (one append blob per session). Created on demand;
    /// must satisfy Azure container naming rules. Defaults to <c>elarion-staged-uploads</c>.
    /// </summary>
    public string StagingContainer { get; set; } = "elarion-staged-uploads";
}
