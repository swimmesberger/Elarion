using System.Text;
using AwesomeAssertions;
using Elarion.Contacts;
using Xunit;
using static Elarion.Tests.Contacts.VCardFiles;

namespace Elarion.Tests.Contacts;

public sealed class ContactImportTests {
    [Fact]
    public async Task AppleExport_ReadsThreeContactsWithEveryMappedValue() {
        var result = await ReadAsync(Apple);

        result.HasErrors.Should().BeFalse();
        result.Entries.Should().HaveCount(3);
        result.Entries.Select(static entry => entry.Version).Should().AllBe("3.0");
        var john = result.Entries[0].Value!;
        john.FormattedName.Should().Be("John Doe");
        john.Name.Should().Be(new ContactName("Doe", "John", null, "Mr.", null));
        john.Organization.Should().Be("Example Inc.");
        john.Department.Should().Be("Sales");
        john.Title.Should().Be("Manager");
        john.Emails.Should().Equal(new ContactEmail("john@example.com") { Types = ContactTypes.Internet | ContactTypes.Work, Preferred = true });
        john.Phones.Should().Equal(
            new ContactPhone("+43 660 1234567") { Types = ContactTypes.Cell | ContactTypes.Voice, Preferred = true },
            new ContactPhone("+43 1 234567") { Types = ContactTypes.Work | ContactTypes.Voice });
        john.Addresses.Should().Equal(new ContactAddress {
            Street = "Hauptstraße 1", Locality = "Wien", PostalCode = "1010", Country = "Österreich", Types = ContactTypes.Home, Preferred = true,
        });
        john.Birthday.Should().Be(new ContactDate(1980, 3, 22));
        john.Note.Should().Be("Met at the fair, 2019.\nCall back.");
        john.AdditionalProperties.Select(static p => p.Name).Should().Equal("PRODID", "X-ABADR");

        var anna = result.Entries[1].Value!;
        anna.Birthday.Should().Be(new ContactDate(null, 7, 1));
        anna.Addresses[0].Types.Should().Be(ContactTypes.Work);

        var acme = result.Entries[2].Value!;
        acme.Kind.Should().Be(ContactKind.Organization);
        acme.Name.Should().BeNull();
        acme.Phones[0].Types.Should().Be(ContactTypes.Main);
    }

    [Fact]
    public async Task AndroidExport_DecodesQuotedPrintableWhoseSoftBreakSplitsACharacter() {
        // "Jürgen Weiß": ü is C3 BC, split across the soft break; the next line does not start with whitespace.
        var text = Lines(
            "BEGIN:VCARD",
            "VERSION:2.1",
            "N;CHARSET=UTF-8;ENCODING=QUOTED-PRINTABLE:Wei=C3=9F;J=C3=",
            "=BCrgen;;;",
            "FN;CHARSET=UTF-8;ENCODING=QUOTED-PRINTABLE:J=C3=BCrgen=20=",
            "Wei=C3=9F",
            "TEL;CELL:+436601234567",
            "END:VCARD");

        var contact = await ReadOneAsync(text);

        contact.FormattedName.Should().Be("Jürgen Weiß");
        contact.Name!.Family.Should().Be("Weiß");
        contact.Name.Given.Should().Be("Jürgen");
        contact.Phones[0].Types.Should().Be(ContactTypes.Cell);
    }

    [Fact]
    public async Task OutlookExport_ReadsBareParametersAndAMultiLineQuotedPrintableNote() {
        var text = Lines(
            "BEGIN:VCARD",
            "VERSION:2.1",
            "N;LANGUAGE=de-at:Huber;Franz",
            "FN:Franz Huber",
            "TEL;WORK;VOICE:+43 1 5550",
            "NOTE;ENCODING=QUOTED-PRINTABLE:First line=0D=0A=",
            "Second line=0D=0AThird",
            "END:VCARD");

        var contact = await ReadOneAsync(text);

        contact.Phones.Should().Equal(new ContactPhone("+43 1 5550") { Types = ContactTypes.Work | ContactTypes.Voice });
        contact.Note.Should().Be("First line\nSecond line\nThird");
    }

    [Fact]
    public async Task GoogleExport_UnescapesTheUrlAndRemovesDirectionalMarksFromPhoneNumbers() {
        var contact = await ReadOneAsync(Card("FN:Lisa", "N:;Lisa;;;", "URL:http\\://example.com", "TEL;TYPE=CELL:‪+43 660 111‬‎"));

        contact.Urls.Should().Equal(new ContactUrl("http://example.com"));
        contact.Phones[0].Number.Should().Be("+43 660 111");
    }

    [Fact]
    public async Task MixedVersions_ReadEachContactWithItsVersion() {
        var text = Lines(
            "BEGIN:VCARD", "VERSION:3.0", "FN:Three", "END:VCARD",
            "BEGIN:VCARD", "VERSION:4.0", "FN:Four", "KIND:individual", "END:VCARD");

        var result = await ReadAsync(text);

        result.Entries.Select(static e => e.Version).Should().Equal("3.0", "4.0");
        result.Contacts.Select(static c => c.FormattedName).Should().Equal("Three", "Four");
        result.HasErrors.Should().BeFalse();
    }

    [Fact]
    public async Task AppleGroups_MapWellKnownLabelsToTypesAndKeepCustomLabels() {
        var contact = await ReadOneAsync(Card(
            "FN:Max",
            "item1.TEL:+43 1",
            "item1.X-ABLabel:_$!<Mobile>!$_",
            "item2.EMAIL;type=INTERNET:max@club.example",
            "item2.X-ABLabel:Club",
            "item3.X-ABRELATEDNAMES:Eva",
            "item3.X-ABLabel:_$!<Sister>!$_"));

        contact.Phones.Should().Equal(new ContactPhone("+43 1") { Types = ContactTypes.Cell });
        contact.Emails.Should().Equal(new ContactEmail("max@club.example") { Types = ContactTypes.Internet, Label = "Club" });
        contact.AdditionalProperties.Select(static p => $"{p.Group}.{p.Name}").Should().Equal("item3.X-ABRELATEDNAMES", "item3.X-ABLABEL");
    }

    [Fact]
    public async Task LowerCaseNames_ReadLikeUpperCaseOnes() {
        var text = Lines("BEGIN:vCard", "version:2.1", "fn:Lower", "tel;cell:+1 555", "end:vcard");

        var result = await ReadAsync(text);

        var entry = Assert.Single(result.Entries);
        entry.Version.Should().Be("2.1");
        entry.Value!.Phones.Should().Equal(new ContactPhone("+1 555") { Types = ContactTypes.Cell });
        result.HasErrors.Should().BeFalse();
    }

    [Fact]
    public async Task MixedLineEndsCommentsAndBlankLines_ProduceNoError() {
        var text = "# exported\r\nBEGIN:VCARD\nVERSION:3.0\r\r\nFN:One\rEND:VCARD\r\n\r\n# next\n\nBEGIN:VCARD\r\nVERSION:3.0\r\nFN:Two\r\nEND:VCARD";

        var result = await ReadAsync(text);

        result.HasErrors.Should().BeFalse();
        result.Contacts.Select(static c => c.FormattedName).Should().Equal("One", "Two");
        result.Entries[1].LineNumber.Should().Be(9);
    }

    [Fact]
    public async Task UnmappedProperties_AreAdditionalPropertiesWithGroupParametersAndValue() {
        var contact = await ReadOneAsync(Card(
            "FN:Social",
            "X-SOCIALPROFILE;type=twitter;x-user=social:https://twitter.com/social",
            "IMPP;X-SERVICE-TYPE=Skype:skype:social",
            "item4.X-ANDROID-CUSTOM:vnd.android.cursor.item/nickname;Soc;1"));

        contact.AdditionalProperties.Should().Equal(
            new ContactProperty(null, "X-SOCIALPROFILE", [new ContactParameter("TYPE", "twitter"), new ContactParameter("X-USER", "social")], "https://twitter.com/social"),
            new ContactProperty(null, "IMPP", [new ContactParameter("X-SERVICE-TYPE", "Skype")], "skype:social"),
            new ContactProperty("item4", "X-ANDROID-CUSTOM", [], "vnd.android.cursor.item/nickname;Soc;1"));
    }

    [Fact]
    public async Task EveryProperty_IsListedInFileOrderWithItsLine() {
        var text = Card(
            "FN:Twelve", "N:Doe;Jane;;;", "NICKNAME:JD", "ORG:Org", "TITLE:Boss", "TEL:1", "EMAIL:j@example.com",
            "URL:https://example.com", "NOTE:n", "CATEGORIES:a,b", "X-CUSTOM;X-P=\"a,b\":v");

        var result = await ReadAsync(text);

        var entry = Assert.Single(result.Entries);
        entry.Properties.Should().HaveCount(12);
        entry.Properties.Select(static p => p.Name).Should().Equal(
            "VERSION", "FN", "N", "NICKNAME", "ORG", "TITLE", "TEL", "EMAIL", "URL", "NOTE", "CATEGORIES", "X-CUSTOM");
        entry.Properties.Select(static p => p.LineNumber).Should().Equal(Enumerable.Range(2, 12).Cast<int?>());
        entry.Properties[^1].Parameters.Should().Equal(new ContactParameter("X-P", "a,b"));
        entry.Properties[2].Value.Should().Be("Doe;Jane;;;");
        entry.Value!.Nicknames.Should().Equal("JD");
        entry.Value.Categories.Should().Equal("a", "b");
    }

    [Fact]
    public async Task FoldedLines_UnfoldWhateverTheIndentation() {
        var bytes = Encoding.UTF8.GetBytes("BEGIN:VCARD\r\nVERSION:3.0\r\nFN:Ann\r\nNOTE:one \r\n  two\r\n\tthree\r\nEND:VCARD\r\n");
        // A fold inside "ä" (C3 A4).
        byte[] split = [.. Encoding.ASCII.GetBytes("BEGIN:VCARD\r\nVERSION:3.0\r\nFN:J"), 0xC3, (byte)'\r', (byte)'\n', (byte)' ', 0xA4, .. Encoding.ASCII.GetBytes("ger\r\nEND:VCARD\r\n")];

        (await ReadAsync(bytes)).Contacts.Single().Note.Should().Be("one twothree");
        (await ReadAsync(split)).Contacts.Single().FormattedName.Should().Be("Jäger");
    }

    [Fact]
    public async Task Parameters_ReadQuotedRepeatedCommaJoinedSpacedAndCaretEscaped() {
        var contact = await ReadOneAsync(Card(
            "FN:P",
            "TEL; TYPE=\"cell,voice\";TYPE=work:1",
            "X-NOTE;X-LABEL=\"Line^nTwo ^'q^' ^^\":v"));

        contact.Phones[0].Types.Should().Be(ContactTypes.Cell | ContactTypes.Voice | ContactTypes.Work);
        contact.AdditionalProperties[0].GetParameter("X-LABEL").Should().Equal("Line\nTwo \"q\" ^");
    }

    [Fact]
    public async Task TextValues_UnescapeKnownAndUnknownBackslashSequences() {
        var contact = await ReadOneAsync(Card("FN:A\\,B\\;C\\:D\\\\E\\NF\\xG"));

        contact.FormattedName.Should().Be("A,B;C:D\\E\nFxG");
    }

    [Fact]
    public async Task Base64Photo_EndsAtABlankLineOrTheNextProperty() {
        var png = ContactTestImages.Png;
        var base64 = Convert.ToBase64String(png);
        var blankLine = Lines("BEGIN:VCARD", "VERSION:2.1", "FN:Two", "PHOTO;ENCODING=BASE64;TYPE=JPEG:" + base64[..12], base64[12..24], base64[24..], "", "TEL:1", "END:VCARD");
        var nextProperty = Lines("BEGIN:VCARD", "VERSION:2.1", "FN:Two", "PHOTO;BASE64:" + base64[..20], base64[20..], "TEL:1", "END:VCARD");

        foreach (var text in new[] { blankLine, nextProperty }) {
            var result = await ReadAsync(text);
            result.HasErrors.Should().BeFalse();
            var contact = result.Contacts.Single();
            contact.Photo!.Data!.Value.ToArray().Should().Equal(png);
            contact.Phones.Should().ContainSingle();
        }
    }
}
