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
        var version21 = await ReadOneAsync(Lines(
            "BEGIN:VCARD", "VERSION:2.1", "FN:A", "NOTE;ENCODING=QUOTED-PRINTABLE:a=0D=0Ab=0Dc=0Ad", "END:VCARD"));
        var version30 = await ReadOneAsync(Card("FN:A", "TITLE:x\\ny\\Nz"));

        version21.Note.Should().Be("a\nb\nc\nd");
        version30.Title.Should().Be("x\ny\nz");
    }

    [Fact]
    public async Task AFileConcatenatedWithAByteOrderMarkBeforeEachCard_KeepsEveryCard() {
        byte[] bom = [0xEF, 0xBB, 0xBF];
        byte[] file = [
            .. bom, .. Encoding.UTF8.GetBytes(Card("FN:One")),
            .. bom, .. Encoding.UTF8.GetBytes(Card("FN:Two")),
            .. bom, .. bom, .. Encoding.UTF8.GetBytes(Card("FN:Three")),
        ];

        var result = await ReadAsync(file);

        result.Errors.Should().BeEmpty();
        result.Contacts.Select(static c => c.FormattedName).Should().Equal("One", "Two", "Three");
        result.ByteOrderMarkEncoding.Should().Be("utf-8");
    }

    [Fact]
    public async Task AUtf16FileConcatenatedWithAByteOrderMarkBeforeEachCard_KeepsEveryCard() {
        byte[] bom = [0xFF, 0xFE];
        byte[] file = [
            .. bom, .. Encoding.Unicode.GetBytes(Card("FN:Één")),
            .. bom, .. Encoding.Unicode.GetBytes(Card("FN:Twee")),
        ];

        var result = await ReadAsync(file);

        result.Errors.Should().BeEmpty();
        result.Contacts.Select(static c => c.FormattedName).Should().Equal("Één", "Twee");
        result.ByteOrderMarkEncoding.Should().Be("utf-16le");
    }

    [Fact]
    public async Task AByteOrderMarkInsideAValue_IsKept() {
        var contact = await ReadOneAsync(Card("FN:A", "NOTE:x\uFEFFy"));

        contact.Note.Should().Be("x\uFEFFy");
    }

    [Fact]
    public async Task Version21Backslashes_AreCharactersExceptBeforeASemicolon() {
        var entry = Assert.Single((await ReadAsync(Lines(
            "BEGIN:VCARD", "VERSION:2.1", @"N:C:\Daten;Anna\;Maria", @"NOTE:Pfad C:\neu\temp", @"TITLE:a\,b", "END:VCARD"))).Entries);

        entry.Value!.Name!.Family.Should().Be(@"C:\Daten");
        entry.Value.Name.Given.Should().Be("Anna;Maria");
        entry.Value.Note.Should().Be(@"Pfad C:\neu\temp");
        entry.Value.Title.Should().Be(@"a\,b");
        entry.Properties.Single(static p => p.Name == "N").Value.Should().Be(@"C:\\Daten;Anna\;Maria");
    }

    [Fact]
    public async Task AVersion21QuotedPrintableLineBreak_IsALineBreakAndALiteralBackslashNIsNot() {
        var contact = await ReadOneAsync(Lines(
            "BEGIN:VCARD", "VERSION:2.1", "FN:A", @"NOTE;ENCODING=QUOTED-PRINTABLE:C:\neu=0D=0Azweite Zeile", "END:VCARD"));

        contact.Note.Should().Be("C:\\neu\nzweite Zeile");
    }

    [Fact]
    public async Task PropertiesBeforeAVersion21Line_TakeItsEscaping() {
        var contact = await ReadOneAsync(Lines("BEGIN:VCARD", @"N:C:\Daten;Anna", "VERSION:2.1", "FN:A", "END:VCARD"));

        contact.Name!.Family.Should().Be(@"C:\Daten");
    }

    [Fact]
    public async Task AVersion21CardWrittenAs30_KeepsItsBackslashes() {
        var source = await ReadAsync(Lines("BEGIN:VCARD", "VERSION:2.1", @"FN:C:\Daten", @"X-PATH:\\server\share", "END:VCARD"));

        var text = ContactWriter.ToText(source.Contacts.Single());
        var back = (await ReadAsync(text)).Contacts.Single();

        text.Should().Contain(@"FN:C:\\Daten" + "\r\n");
        back.FormattedName.Should().Be(@"C:\Daten");
        back.AdditionalProperties.Single().GetText().Should().Be(@"\\server\share");
    }
}
