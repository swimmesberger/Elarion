namespace Elarion.Tests.Contacts;

/// <summary>Bytes that start like images of each format the reader sniffs; the reader checks only the magic numbers.</summary>
internal static class ContactTestImages {
    public static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52, 1, 2, 3, 4, 5, 6, 7, 8, 9];

    public static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0, 16, 0x4A, 0x46, 0x49, 0x46, 0, 1, 1, 0, 0, 1, 0, 1, 0, 0, 0xFF, 0xD9];

    public static readonly byte[] Gif = [.. "GIF89a"u8, 1, 0, 1, 0, 0, 0, 0, 0x3B];

    public static readonly byte[] WebP = [.. "RIFF"u8, 20, 0, 0, 0, .. "WEBPVP8 "u8, 1, 2, 3, 4];

    public static readonly byte[] Bmp = [.. "BM"u8, 58, 0, 0, 0, 0, 0, 0, 0, 54, 0, 0, 0, 40, 0];

    /// <summary>A JPEG of <paramref name="length"/> bytes.</summary>
    public static byte[] JpegOf(int length) {
        var bytes = new byte[length];
        Jpeg.CopyTo(bytes, 0);
        for (var i = Jpeg.Length; i < length; i++) bytes[i] = (byte)(i * 31);
        return bytes;
    }
}
