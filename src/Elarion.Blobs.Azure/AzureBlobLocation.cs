namespace Elarion.Blobs.Azure;

/// <summary>
/// The Azure address behind a <see cref="BlobRef"/>: <c>{container}/{name}</c>. The container never
/// contains a slash (Azure container naming rules), so the first slash splits unambiguously even though
/// blob names may contain slashes.
/// </summary>
internal readonly record struct AzureBlobLocation(string Container, string Name) {
    public BlobRef ToBlobRef() {
        return new BlobRef { Value = $"{Container}/{Name}" };
    }

    public static AzureBlobLocation Parse(BlobRef blobRef) {
        ArgumentException.ThrowIfNullOrWhiteSpace(blobRef.Value, nameof(blobRef));

        var separator = blobRef.Value.IndexOf('/');
        if (separator <= 0 || separator == blobRef.Value.Length - 1)
            throw new ArgumentException(
                $"'{blobRef.Value}' is not an Azure blob reference (expected '{{container}}/{{name}}').",
                nameof(blobRef));

        return new AzureBlobLocation(blobRef.Value[..separator], blobRef.Value[(separator + 1)..]);
    }
}
