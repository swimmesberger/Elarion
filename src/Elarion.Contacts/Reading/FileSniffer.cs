namespace Elarion.Contacts.Reading;

/// <summary>
/// Names what bytes are when they are not what a read expected (FR-075, FR-076): a PDF, an archive or an image of a
/// format the package does not read, so an application words "the file is a PDF" rather than "the file is unreadable".
/// Magic numbers only; the bytes are never parsed.
/// </summary>
internal static class FileSniffer {
    /// <summary>The bytes that count when deciding whether a file is text.</summary>
    private const int TextProbeLength = 8 * 1024;

    /// <summary>The media type the magic number of <paramref name="bytes"/> shows, or <see langword="null"/> for none known.</summary>
    public static string? MediaType(ReadOnlySpan<byte> bytes) {
        if (ContactPhoto.SniffMediaType(bytes) is { } image) return image;
        if (bytes.StartsWith("%PDF-"u8)) return "application/pdf";
        if (bytes.StartsWith("PK\u0003\u0004"u8)) return "application/zip";
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0x1F, 0x8B])) return "application/gzip";
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1])) return "application/x-ole-storage";
        if (bytes.StartsWith("II*\0"u8) || bytes.StartsWith("MM\0*"u8)) return "image/tiff";
        if (bytes.Length >= 12 && bytes.Slice(4, 4).SequenceEqual("ftyp"u8)) {
            var brand = bytes.Slice(8, 4);
            if (brand.SequenceEqual("avif"u8) || brand.SequenceEqual("avis"u8)) return "image/avif";
            if (brand.SequenceEqual("heic"u8) || brand.SequenceEqual("heix"u8) || brand.SequenceEqual("hevc"u8)
                || brand.SequenceEqual("mif1"u8) || brand.SequenceEqual("msf1"u8))
                return "image/heic";
        }

        return null;
    }

    /// <summary>
    /// The media type of a file that is not text: a known magic number, else <c>application/octet-stream</c> when its
    /// first bytes hold a NUL that UTF-16 text would not explain; <see langword="null"/> for text.
    /// </summary>
    public static string? BinaryMediaType(ReadOnlySpan<byte> bytes) {
        if (MediaType(bytes) is { } known) return known;
        var probe = bytes[..Math.Min(bytes.Length, TextProbeLength)];
        if (probe.IndexOf((byte)0) < 0) return null;

        // UTF-16 text without a byte-order mark has its NULs on one side of each code unit only.
        int even = 0, odd = 0;
        for (var i = 0; i < probe.Length; i++) {
            if (probe[i] != 0) continue;
            if (i % 2 == 0) even++;
            else odd++;
        }

        var units = probe.Length / 2;
        var utf16 = units > 0 && (Math.Min(even, odd) == 0 && Math.Max(even, odd) * 4 >= units);
        return utf16 ? null : "application/octet-stream";
    }
}
