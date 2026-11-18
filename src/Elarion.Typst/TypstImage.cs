using System.Buffers.Binary;

namespace Elarion.Typst;

/// <summary>A raster image format Typst decodes.</summary>
public enum TypstImageFormat {
    /// <summary>PNG.</summary>
    Png,

    /// <summary>JPEG.</summary>
    Jpeg,

    /// <summary>GIF.</summary>
    Gif,

    /// <summary>WebP.</summary>
    WebP
}

/// <summary>
/// Recognizes pictures Typst can embed, from their bytes - never from a declared media type, which is only a claim.
/// </summary>
/// <remarks>
/// <para>
/// Typst aborts the whole compilation on an image it cannot decode, so a single broken upload would make a document
/// impossible to produce. <see cref="TryCreateAsset"/> hands over only a picture whose format Typst reads and whose
/// file is structurally complete; for anything else the template prints a placeholder.
/// </para>
/// <para>
/// The checks are cheap and catch the cut-short upload, which is the realistic way a picture breaks: every PNG chunk
/// within the file and an <c>IEND</c>; JPEG segments within the file and an end-of-image marker behind the scan data
/// (anything a phone appends after it, such as a motion-photo video, is ignored, as Typst ignores it); a GIF trailer;
/// a WebP RIFF size that matches the file. A picture that passes them and is still corrupt inside its compressed data
/// is beyond what they can see. SVG and PDF assets are not covered: pass them as plain <see cref="TypstAsset"/>s.
/// </para>
/// </remarks>
public static class TypstImage {
    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static ReadOnlySpan<byte> JpegSignature => [0xFF, 0xD8, 0xFF];

    /// <summary>
    /// The format of a complete picture Typst can decode, or <see langword="null"/> for an unsupported format (BMP,
    /// TIFF, HEIC, anything unrecognized) or a truncated file.
    /// </summary>
    public static TypstImageFormat? DetectFormat(ReadOnlySpan<byte> content) {
        if (content.StartsWith(PngSignature)) return IsCompletePng(content) ? TypstImageFormat.Png : null;
        if (content.StartsWith(JpegSignature)) return IsCompleteJpeg(content) ? TypstImageFormat.Jpeg : null;
        if (content.StartsWith("GIF87a"u8) || content.StartsWith("GIF89a"u8)) return content[^1] == 0x3B ? TypstImageFormat.Gif : null;
        if (content.Length >= 12 && content[..4].SequenceEqual("RIFF"u8) && content[8..12].SequenceEqual("WEBP"u8))
            return IsCompleteRiff(content) ? TypstImageFormat.WebP : null;
        return null;
    }

    /// <summary>The conventional file extension of <paramref name="format"/>, without the dot.</summary>
    public static string GetExtension(TypstImageFormat format) => format switch {
        TypstImageFormat.Png => "png",
        TypstImageFormat.Jpeg => "jpg",
        TypstImageFormat.Gif => "gif",
        TypstImageFormat.WebP => "webp",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown image format.")
    };

    /// <summary>
    /// Creates an asset named <c>&lt;baseName&gt;.&lt;extension&gt;</c> when <paramref name="content"/> is a complete
    /// picture Typst can decode; returns <see langword="false"/> otherwise.
    /// </summary>
    /// <param name="baseName">The asset name without extension, such as <c>photo-0001</c>.</param>
    /// <param name="content">The picture's bytes.</param>
    /// <param name="asset">The asset, when the method returns <see langword="true"/>.</param>
    /// <example>
    /// <code>
    /// var assets = new List&lt;TypstAsset&gt;();
    /// var photos = new List&lt;string?&gt;();   // the template model: asset name, or null for "not printable"
    /// foreach (var (photo, index) in inspection.Photos.Select((p, i) =&gt; (p, i))) {
    ///     photos.Add(TypstImage.TryCreateAsset($"photo-{index:D4}", photo.Content, out var asset) ? asset.Name : null);
    ///     if (asset is not null) assets.Add(asset);
    /// }
    /// </code>
    /// </example>
    public static bool TryCreateAsset(string baseName, ReadOnlyMemory<byte> content, out TypstAsset? asset) {
        asset = null;
        if (DetectFormat(content.Span) is not { } format) return false;
        asset = new TypstAsset($"{baseName}.{GetExtension(format)}", content);
        return true;
    }

    /// <summary>An <c>IHDR</c> first, every chunk inside the file, and an <c>IEND</c> chunk.</summary>
    private static bool IsCompletePng(ReadOnlySpan<byte> content) {
        long offset = PngSignature.Length;
        var first = true;
        while (offset + 12 <= content.Length) {
            var length = BinaryPrimitives.ReadUInt32BigEndian(content.Slice((int)offset, 4));
            var type = content.Slice((int)offset + 4, 4);
            var end = offset + 12 + length; // length, type, data, CRC
            if (end > content.Length) return false;
            if (first && !type.SequenceEqual("IHDR"u8)) return false;
            if (type.SequenceEqual("IEND"u8)) return true;
            first = false;
            offset = end;
        }

        return false;
    }

    /// <summary>
    /// Walks the segments by their lengths - an Exif segment may carry a complete thumbnail JPEG of its own, with its
    /// own end-of-image marker, in front of the main picture - and requires the end-of-image marker behind the scan
    /// data. Inside scan data every <c>FF</c> is stuffed (<c>FF 00</c>) and restart markers (<c>FF D0</c>-<c>FF D7</c>)
    /// belong to it, so the first other marker ends the scan: the end of image, or the next segment of a progressive
    /// picture.
    /// </summary>
    private static bool IsCompleteJpeg(ReadOnlySpan<byte> content) {
        var offset = 2; // behind the start-of-image marker FF D8
        while (offset + 1 < content.Length) {
            if (content[offset] != 0xFF) return false;
            var marker = content[offset + 1];
            if (marker == 0xFF) {
                offset++; // a fill byte in front of a marker
                continue;
            }

            offset += 2;
            if (marker == 0xD9) return true;
            if (marker is 0x01 or >= 0xD0 and <= 0xD7) continue; // standalone markers carry no length

            if (offset + 2 > content.Length) return false;
            int length = BinaryPrimitives.ReadUInt16BigEndian(content.Slice(offset, 2));
            if (length < 2 || offset + length > content.Length) return false;
            offset += length;
            if (marker != 0xDA) continue;

            while (true) {
                var next = content[offset..].IndexOf((byte)0xFF);
                if (next < 0 || offset + next + 1 >= content.Length) return false;
                offset += next;
                var following = content[offset + 1];
                if (following is 0x00 or >= 0xD0 and <= 0xD7) {
                    offset += 2;
                    continue;
                }

                break;
            }
        }

        return false;
    }

    /// <summary>The RIFF size field covers the rest of the file (one pad byte for an odd size is allowed).</summary>
    private static bool IsCompleteRiff(ReadOnlySpan<byte> content) {
        var size = BinaryPrimitives.ReadUInt32LittleEndian(content.Slice(4, 4));
        long payload = content.Length - 8;
        return size == payload || (size % 2 == 1 && size + 1 == payload);
    }
}
