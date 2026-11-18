using AwesomeAssertions;
using Elarion.Typst;
using Xunit;

namespace Elarion.Tests.Typst;

public sealed class TypstImageTests {
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    // Structurally complete: SOI, an APP0 segment, a scan with stuffed and restart bytes, EOI.
    private static readonly byte[] Jpeg = [
        0xFF, 0xD8,
        0xFF, 0xE0, 0x00, 0x06, 0x4A, 0x46, 0x49, 0x46,
        0xFF, 0xDA, 0x00, 0x04, 0x01, 0x02,
        0x12, 0xFF, 0x00, 0x34, 0xFF, 0xD0, 0x56,
        0xFF, 0xD9
    ];

    [Fact]
    public void Complete_png_is_recognized() =>
        TypstImage.DetectFormat(Png).Should().Be(TypstImageFormat.Png);

    [Fact]
    public void Truncated_png_is_rejected() =>
        TypstImage.DetectFormat(Png.AsSpan(0, Png.Length - 4)).Should().BeNull();

    [Fact]
    public void Complete_jpeg_is_recognized_even_with_trailing_data() {
        TypstImage.DetectFormat(Jpeg).Should().Be(TypstImageFormat.Jpeg);
        TypstImage.DetectFormat([.. Jpeg, 0x00, 0x01, 0x02]).Should().Be(TypstImageFormat.Jpeg);
    }

    [Fact]
    public void Jpeg_cut_off_inside_the_scan_is_rejected() =>
        TypstImage.DetectFormat(Jpeg.AsSpan(0, Jpeg.Length - 2)).Should().BeNull();

    [Fact]
    public void Gif_requires_its_trailer() {
        byte[] gif = [.. "GIF89a"u8, 0x01, 0x00, 0x3B];

        TypstImage.DetectFormat(gif).Should().Be(TypstImageFormat.Gif);
        TypstImage.DetectFormat(gif.AsSpan(0, gif.Length - 1)).Should().BeNull();
    }

    [Fact]
    public void Webp_requires_a_matching_riff_size() {
        byte[] webp = [.. "RIFF"u8, 0x08, 0x00, 0x00, 0x00, .. "WEBP"u8, 0x01, 0x02, 0x03, 0x04];

        TypstImage.DetectFormat(webp).Should().Be(TypstImageFormat.WebP);
        TypstImage.DetectFormat(webp.AsSpan(0, webp.Length - 1)).Should().BeNull();
    }

    [Fact]
    public void Unsupported_format_is_rejected() =>
        TypstImage.DetectFormat("BM\0\0\0\0"u8).Should().BeNull();

    [Fact]
    public void Try_create_asset_names_the_file_by_its_detected_format() {
        TypstImage.TryCreateAsset("photo-0001", Jpeg, out var asset).Should().BeTrue();
        asset!.Name.Should().Be("photo-0001.jpg");

        TypstImage.TryCreateAsset("photo-0002", new byte[] { 1, 2, 3 }, out var rejected).Should().BeFalse();
        rejected.Should().BeNull();
    }
}
