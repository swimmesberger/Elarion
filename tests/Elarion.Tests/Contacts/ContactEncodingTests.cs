using System.Text;
using AwesomeAssertions;
using Elarion.Contacts;
using Xunit;
using static Elarion.Tests.Contacts.VCardFiles;

namespace Elarion.Tests.Contacts;

public sealed class ContactEncodingTests {
    private static readonly Encoding Windows1252 = CodePagesEncodingProvider.Instance.GetEncoding(1252)!;

    [Fact]
    public async Task TheSameContacts_ReadTheSameInUtf8AndUtf16WithAndWithoutAByteOrderMark() {
        var expected = (await ReadAsync(Apple)).Contacts.ToList();
        byte[][] files = [
            Encoding.UTF8.GetBytes(Apple),
            [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(Apple)],
            [0xFF, 0xFE, .. Encoding.Unicode.GetBytes(Apple)],
            [0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes(Apple)],
            Encoding.Unicode.GetBytes(Apple),
            Encoding.BigEndianUnicode.GetBytes(Apple),
        ];
        string?[] marks = [null, "utf-8", "utf-16le", "utf-16be", null, null];

        for (var i = 0; i < files.Length; i++) {
            var result = await ReadAsync(files[i]);
            result.Errors.Select(static e => e.ToString()).Should().BeEmpty($"file {i} reads without errors");
            result.Contacts.Should().Equal(expected);
            result.ByteOrderMarkEncoding.Should().Be(marks[i]);
            result.UsedFallbackEncoding.Should().BeFalse();
        }
    }

    [Fact]
    public async Task Windows1252WithoutCharset_ReadsWithTheFallbackAndSaysSo() {
        var bytes = Windows1252.GetBytes(Lines("BEGIN:VCARD", "VERSION:2.1", "N:Müller;Jörg", "FN:Jörg Müller", "END:VCARD"));

        var result = await ReadAsync(bytes);

        result.Contacts.Single().FormattedName.Should().Be("Jörg Müller");
        result.UsedFallbackEncoding.Should().BeTrue();
    }

    [Fact]
    public async Task MixedEncodings_DecodeEachValueInItsOwn() {
        byte[] bytes = [
            .. Encoding.UTF8.GetBytes(Lines("BEGIN:VCARD", "VERSION:3.0", "FN:Zoë Weiß")),
            .. Windows1252.GetBytes(Lines("NOTE:Grüße")),
            .. Encoding.UTF8.GetBytes(Lines("TITLE:Geschäftsführerin", "END:VCARD")),
        ];

        var contact = (await ReadAsync(bytes)).Contacts.Single();

        contact.FormattedName.Should().Be("Zoë Weiß");
        contact.Note.Should().Be("Grüße");
        contact.Title.Should().Be("Geschäftsführerin");
    }

    [Fact]
    public async Task ConfiguredFallback_ReadsWindows1250Names() {
        var windows1250 = CodePagesEncodingProvider.Instance.GetEncoding(1250)!;
        var bytes = windows1250.GetBytes(Lines("BEGIN:VCARD", "VERSION:2.1", "FN:Jiří Dvořák", "END:VCARD"));

        var result = await ReadAsync(bytes, new ContactReadOptions { FallbackEncoding = "windows-1250" });

        result.Contacts.Single().FormattedName.Should().Be("Jiří Dvořák");
    }

    [Fact]
    public async Task ShiftJisCharset_DecodesTheQuotedPrintableValue() {
        var sjis = CodePagesEncodingProvider.Instance.GetEncoding("shift_jis")!;
        var name = "山田太郎";
        var qp = string.Concat(sjis.GetBytes(name).Select(static b => $"={b:X2}"));

        var contact = await ReadOneAsync(Lines("BEGIN:VCARD", "VERSION:2.1", $"FN;CHARSET=Shift_JIS;ENCODING=QUOTED-PRINTABLE:{qp}", "END:VCARD"));

        contact.FormattedName.Should().Be(name);
    }

    [Fact]
    public async Task UnknownCharset_DecodesByTheLadderWithAnError() {
        var result = await ReadAsync(Lines("BEGIN:VCARD", "VERSION:2.1", "FN;CHARSET=x-unknown:Zoë", "END:VCARD"));

        var entry = Assert.Single(result.Entries);
        entry.Value!.FormattedName.Should().Be("Zoë");
        var error = Assert.Single(entry.Errors);
        error.Code.Should().Be(ContactErrorCodes.UnknownCharset);
        error.Charset.Should().Be("x-unknown");
        error.Property.Should().Be("FN");
        error.LineNumber.Should().Be(3);
    }

    [Fact]
    public async Task UnknownFallbackEncoding_IsAMisconfiguration() {
        var read = async () => await ReadAsync(Card("FN:A"), new ContactReadOptions { FallbackEncoding = "x-no-such-encoding" });

        await read.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task LineBreaks_ReadAsNewlinesOnEveryPlatform() {
        var contact = await ReadOneAsync(Lines(
            "BEGIN:VCARD", "VERSION:2.1", "FN:A", "NOTE;ENCODING=QUOTED-PRINTABLE:a=0D=0Ab=0Dc=0Ad", "TITLE:x\\ny\\Nz", "END:VCARD"));

        contact.Note.Should().Be("a\nb\nc\nd");
        contact.Title.Should().Be("x\ny\nz");
    }
}
