namespace Elarion.Contacts;

/// <summary>
/// The photo of a contact (FR-028–FR-032): either its image bytes, with the media type the bytes show, or the
/// <c>http</c> or <c>https</c> address of an image, which the package never requests.
/// </summary>
/// <remarks>
/// A photo given as an address is the application's to fetch or not; fetching an address taken from an uploaded file
/// lets the uploader make the server send requests (server-side request forgery), so check it against an allow list
/// first. Equality compares the bytes, the media type and the address.
/// </remarks>
public sealed class ContactPhoto : IEquatable<ContactPhoto> {
    private ContactPhoto(ReadOnlyMemory<byte>? data, string? mediaType, Uri? uri) {
        Data = data;
        MediaType = mediaType;
        Uri = uri;
    }

    /// <summary>The image bytes, or <see langword="null"/> for a photo given as an address.</summary>
    public ReadOnlyMemory<byte>? Data { get; }

    /// <summary>
    /// The media type the bytes show (<c>image/jpeg</c>, <c>image/png</c>, <c>image/gif</c>, <c>image/webp</c> or
    /// <c>image/bmp</c>), whatever the file declared; <see langword="null"/> for a photo given as an address.
    /// </summary>
    public string? MediaType { get; }

    /// <summary>The address of the image, or <see langword="null"/> for an inline photo.</summary>
    public Uri? Uri { get; }

    /// <summary>A photo of <paramref name="data"/>, whose media type is read from the bytes.</summary>
    /// <param name="data">The bytes of a JPEG, PNG, GIF, WebP or BMP image.</param>
    /// <exception cref="ArgumentException">The bytes are empty or of no known image format.</exception>
    public static ContactPhoto FromData(ReadOnlyMemory<byte> data) {
        var mediaType = SniffMediaType(data.Span)
            ?? throw new ArgumentException("The bytes are not a JPEG, PNG, GIF, WebP or BMP image.", nameof(data));
        return new ContactPhoto(data, mediaType, null);
    }

    /// <summary>A photo given as the address of an image.</summary>
    /// <param name="uri">An absolute <c>http</c> or <c>https</c> address.</param>
    /// <exception cref="ArgumentException">The address is not an absolute <c>http</c> or <c>https</c> address.</exception>
    public static ContactPhoto FromUri(Uri uri) {
        ArgumentNullException.ThrowIfNull(uri);
        if (!IsWebUri(uri)) throw new ArgumentException("A photo address must be an absolute http or https address.", nameof(uri));
        return new ContactPhoto(null, null, uri);
    }

    /// <inheritdoc/>
    public bool Equals(ContactPhoto? other) {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        if (Data.HasValue != other.Data.HasValue) return false;
        if (Data is { } data && !data.Span.SequenceEqual(other.Data!.Value.Span)) return false;
        return MediaType == other.MediaType && Uri == other.Uri;
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as ContactPhoto);

    /// <inheritdoc/>
    public override int GetHashCode() {
        var hash = new HashCode();
        hash.Add(MediaType);
        hash.Add(Uri);
        if (Data is { } data) {
            hash.Add(data.Length);
            hash.AddBytes(data.Span[..Math.Min(data.Length, 64)]);
        }

        return hash.ToHashCode();
    }

    internal static bool IsWebUri(Uri uri) =>
        uri.IsAbsoluteUri && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    // Magic numbers (FR-028): the bytes decide, not the declared type.
    internal static string? SniffMediaType(ReadOnlySpan<byte> bytes) {
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF])) return "image/jpeg";
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47])) return "image/png";
        if (bytes.StartsWith("GIF8"u8)) return "image/gif";
        if (bytes.Length >= 12 && bytes.StartsWith("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8)) return "image/webp";
        if (bytes.Length >= 14 && bytes.StartsWith("BM"u8)) return "image/bmp";
        return null;
    }
}
