namespace Elarion.Blobs;

/// <summary>
/// Configures the <see cref="Elarion.Abstractions.IUploadedFiles"/> reader registered by
/// <see cref="UploadedFilesServiceCollectionExtensions.AddElarionUploadedFiles"/>.
/// </summary>
public sealed class UploadedFilesOptions {
    /// <summary>
    /// The blob containers uploads are staged in; a blob in any other container is never an upload. Defaults to
    /// <c>uploads</c>, the container of both the direct upload endpoint and the tus endpoint. A host that moved an
    /// endpoint's <c>Container</c> lists the new name here. An empty list matches nothing.
    /// </summary>
    public IList<string> Containers { get; set; } = ["uploads"];
}
