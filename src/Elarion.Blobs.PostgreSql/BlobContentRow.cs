namespace Elarion.Blobs.PostgreSql;

internal sealed class BlobContentRow {
    public string BlobId { get; set; } = string.Empty;

    public byte[] Data { get; set; } = [];
}
