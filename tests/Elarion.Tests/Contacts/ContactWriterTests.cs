using System.Runtime.CompilerServices;
using System.Text;
using AwesomeAssertions;
using Elarion.Contacts;
using Xunit;
using static Elarion.Tests.Contacts.VCardFiles;

namespace Elarion.Tests.Contacts;

public sealed class ContactWriterTests {
    private static readonly Contact Full = new() {
        FormattedName = "Dr. Jane Q. Doe",
        Name = new ContactName("Doe", "Jane", "Quinn, Q.", "Dr.", "PhD"),
        Nicknames = ["JD", "Janie"],
        Organization = "Example; Inc.",
        Department = "R&D",
        Title = "Chief, Research",
        Role = "Lead",
        Phones = [
            new ContactPhone("+43 660 1234567") { Types = ContactTypes.Cell | ContactTypes.Voice, Preferred = true },
            new ContactPhone("+43 1 234") { Types = ContactTypes.Work | ContactTypes.Fax, Label = "Office fax" },
        ],
        Emails = [new ContactEmail("jane@example.com") { Types = ContactTypes.Work, Label = "Club" }],
        Addresses = [
            new ContactAddress {
                Street = "Hauptstraße 1\nStiege 2", Locality = "Wien", PostalCode = "1010", Country = "Österreich", Types = ContactTypes.Home,
                Preferred = true,
            },
        ],
        Urls = [new ContactUrl("https://example.com/a,b;c") { Types = ContactTypes.Work }],
        Birthday = new ContactDate(null, 2, 29),
        Anniversary = new ContactDate(2010, 6, 12),
        Note = "Line one\nLine two: \"quoted\", semi; back\\slash",
        Categories = ["Friends", "Work, mostly"],
        Photo = ContactPhoto.FromData(ContactTestImages.JpegOf(300)),
        Uid = "urn:uuid:4fbe8971-0bc3-424c-9c26-36c3e1eff6b1",
        AdditionalProperties = [
            new ContactProperty("item1", "X-ABRELATEDNAMES", [], "Eva"),
            new ContactProperty("item1", "X-ABLabel", [], "_$!<Sister>!$_"),
            new ContactProperty(null, "X-SOCIALPROFILE", [new ContactParameter("TYPE", "twitter"), new ContactParameter("X-USER", ["a:b", "c"])], "https://twitter.com/jane"),
        ],
    };

    private static readonly Contact Organization = new() {
        Kind = ContactKind.Organization,
        Organization = "ACME GmbH",
        Phones = [new ContactPhone("+43 1 999") { Types = ContactTypes.Main }],
        Photo = ContactPhoto.FromUri(new Uri("https://example.com/logo.png")),
    };

    [Fact]
    public async Task ToFile_GivesAVCard30FileWithOneCardPerContact() {
        var file = await ContactWriter.ToFileAsync([Full, Organization, new Contact { FormattedName = "Third" }], "contacts.vcf", cancellationToken: TestCancellation);

        file.ContentType.Should().Be("text/vcard; charset=utf-8");
        ContactWriter.ContentType.Should().Be("text/vcard");
        file.FileName.Should().Be("contacts.vcf");
        var text = Encoding.UTF8.GetString(file.Bytes.Span);
        CountOf(text, "BEGIN:VCARD\r\n").Should().Be(3);
        CountOf(text, "VERSION:3.0\r\n").Should().Be(3);
        file.Bytes.Span.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]).Should().BeFalse();
    }

    [Fact]
    public async Task VersionOption_Writes40Cards() {
        var text = await WriteAsync([Full], ContactFormatVersion.V4_0);

        text.Should().Contain("VERSION:4.0\r\n");
        text.Should().NotContain("VERSION:3.0");
    }

    [Theory]
    [InlineData(ContactFormatVersion.V3_0)]
    [InlineData(ContactFormatVersion.V4_0)]
    public async Task EveryMember_ReadsBackEqual(ContactFormatVersion version) {
        var text = await WriteAsync([Full, Organization], version);

        var result = await ReadAsync(text);

        result.HasErrors.Should().BeFalse();
        result.Contacts.Should().Equal(Full, ExpectedBack(Organization));
    }

    [Fact]
    public async Task Version30_WritesTheFormsItsImportersRead() {
        var text = Unfold(await WriteAsync([Full, Organization], ContactFormatVersion.V3_0));

        text.Should().Contain("PHOTO;ENCODING=b;TYPE=JPEG:/9j/");
        text.Should().Contain("BDAY;X-APPLE-OMIT-YEAR=1604:1604-02-29\r\n");
        text.Should().Contain("X-ANNIVERSARY:2010-06-12\r\n");
        text.Should().Contain("item4.X-ABDATE:2010-06-12\r\nitem4.X-ABLabel:_$!<Anniversary>!$_\r\n");
        text.Should().Contain("item2.TEL;TYPE=WORK,FAX:+43 1 234\r\nitem2.X-ABLabel:Office fax\r\n");
        text.Should().Contain("TEL;TYPE=CELL,VOICE,PREF:+43 660 1234567\r\n");
        text.Should().Contain("X-ABShowAs:COMPANY\r\n");
        text.Should().Contain("PHOTO;VALUE=uri:https://example.com/logo.png\r\n");
        text.Should().Contain("N:;;;;\r\n");
        text.Should().Contain("X-SOCIALPROFILE;TYPE=twitter;X-USER=\"a:b\",c:https://twitter.com/jane\r\n");
    }

    [Fact]
    public async Task Version40_WritesTheFormsItsImportersRead() {
        var text = Unfold(await WriteAsync([Full, Organization], ContactFormatVersion.V4_0));

        text.Should().Contain("PHOTO:data:image/jpeg;base64,/9j/");
        text.Should().Contain("BDAY:--0229\r\n");
        text.Should().Contain("ANNIVERSARY:20100612\r\n");
        text.Should().Contain("TEL;TYPE=cell,voice;PREF=1:+43 660 1234567\r\n");
        text.Should().Contain("KIND:org\r\n");
        text.Should().Contain("PHOTO:https://example.com/logo.png\r\n");
        text.Should().NotContain("X-ABShowAs");
    }

    [Fact]
    public async Task AContactWithoutAFormattedName_GetsOneFromItsDisplayName() {
        var text = await WriteAsync([
            new Contact { Name = new ContactName("Doe", "John", null, null, null) },
            new Contact { Organization = "ACME" },
            new Contact { Emails = [new ContactEmail("a@example.com")] },
            new Contact { Phones = [new ContactPhone("+1 555")] },
        ]);

        var names = (await ReadAsync(text)).Contacts.Select(static c => c.FormattedName);

        names.Should().Equal("John Doe", "ACME", "a@example.com", "+1 555");
    }

    [Fact]
    public async Task ALongNoteWithUmlauts_FoldsAt75OctetsWithoutSplittingACharacter() {
        var note = string.Concat(Enumerable.Repeat("Grüße aus Österreich – äöü ß €\n", 17))[..500];
        var bytes = Encoding.UTF8.GetBytes(await WriteAsync([new Contact { FormattedName = "N", Note = note }]));

        var lines = Split(bytes);

        lines.Should().OnlyContain(static line => line.Length <= 75);
        lines.Should().OnlyContain(static line => line.Length == 0 || (line[0] & 0xC0) != 0x80);
        (await ReadAsync(bytes)).Contacts.Single().Note.Should().Be(note);
    }

    [Fact]
    public async Task TheSameContacts_GiveByteIdenticalFiles() {
        var first = await WriteAsync([Full, Organization]);
        var second = await WriteAsync([Full, Organization]);

        second.Should().Be(first);
    }

    [Fact]
    public async Task AdditionalPropertiesReadFromAFile_AreWrittenAsTheyWereRead() {
        var source = await ReadOneAsync(Lines(
            "BEGIN:VCARD", "VERSION:2.1", "FN:A",
            "X-CUSTOM;CHARSET=UTF-8;ENCODING=QUOTED-PRINTABLE:Gr=C3=BC=C3=9Fe=0D=0Azwei",
            "IMPP;X-SERVICE-TYPE=Skype:skype:a",
            "END:VCARD"));

        var text = await WriteAsync([source]);
        var back = (await ReadAsync(text)).Contacts.Single();

        text.Should().Contain("X-CUSTOM:Grüße\\nzwei\r\n");
        text.Should().Contain("IMPP;X-SERVICE-TYPE=Skype:skype:a\r\n");
        back.Should().Be(source);
    }

    [Fact]
    public async Task AnEntrysPropertyList_IsWrittenAsItHoldsIt() {
        var entry = Assert.Single((await ReadAsync(Card("FN:A", "TEL;TYPE=CELL,X-WEIRD:1", "item7.X-THING;Q=1:v"))).Entries);

        using var stream = new MemoryStream();
        await using (var writer = ContactWriter.Create(stream)) await writer.AddAsync(entry.Properties, TestCancellation);
        var back = Assert.Single((await ReadAsync(stream.ToArray())).Entries);

        back.Properties.Should().Equal(entry.Properties);
    }

    [Fact]
    public async Task AnInvalidAdditionalProperty_FailsTheWriteNamingTheContactAndProperty() {
        using var stream = new MemoryStream();
        await using var writer = ContactWriter.Create(stream);
        await writer.AddAsync(new Contact { FormattedName = "Fine" }, TestCancellation);
        var bad = new Contact { FormattedName = "Bad", AdditionalProperties = [new ContactProperty("BAD NAME", "v")] };

        var add = async () => await writer.AddAsync(bad, TestCancellation);

        var exception = (await add.Should().ThrowAsync<ContactWriteException>()).Which;
        exception.ItemIndex.Should().Be(2);
        exception.Property.Should().Be("BAD NAME");
        exception.Message.Should().Contain("Contact 2");
        await writer.AddAsync(new Contact { FormattedName = "After" }, TestCancellation);
        await writer.DisposeAsync();
        (await ReadAsync(stream.ToArray())).Contacts.Select(static c => c.FormattedName).Should().Equal("Fine", "After");
    }

    [Theory]
    [InlineData("X-BAD", "line\r\nbreak")]
    [InlineData("END", "VCARD")]
    [InlineData("X-OKä", "v")]
    public async Task UnrepresentableProperties_FailTheWrite(string name, string value) {
        var contact = new Contact { FormattedName = "A", AdditionalProperties = [new ContactProperty(name, value)] };

        var write = async () => await WriteAsync([contact]);

        await write.Should().ThrowAsync<ContactWriteException>();
    }

    [Fact]
    public async Task OneContactPerFile_HoldsOneCardEach() {
        foreach (var contact in new[] { Full, Organization }) {
            var file = await ContactWriter.ToFileAsync([contact], "contact.vcf", cancellationToken: TestCancellation);
            CountOf(Encoding.UTF8.GetString(file.Bytes.Span), "BEGIN:VCARD").Should().Be(1);
        }
    }

    [Fact]
    public async Task AStreamedFile_SendsTheFirstCardBeforeTheLastIsProduced() {
        var body = new ObservedStream();
        await using var file = ContactWriter.ToStreamedFile(Produce(100_000, body, TestCancellation), "contacts.vcf");

        file.ContentType.Should().Be("text/vcard; charset=utf-8");
        file.FileName.Should().Be("contacts.vcf");
        await file.WriteToAsync(body, TestCancellation);

        body.BytesBeforeLastProduced.Should().BeGreaterThan(0);
        body.LargestWrite.Should().BeLessThan(64 * 1024);
        CountOf(Encoding.UTF8.GetString(body.Tail), "END:VCARD").Should().BeGreaterThan(0);
        body.Length.Should().BeGreaterThan(100_000 * 30);
    }

    [Fact]
    public async Task AStreamedFileWithADelegate_WritesWhatTheDelegateAdds() {
        await using var file = ContactWriter.ToStreamedFile("contacts.vcf", async (writer, ct) => {
            await writer.AddAsync(new Contact { FormattedName = "One" }, ct);
            await writer.AddAsync([new ContactProperty("FN", "Two")], ct);
        }, new ContactWriteOptions { Version = ContactFormatVersion.V4_0 });

        using var body = new MemoryStream();
        await file.WriteToAsync(body, TestCancellation);

        var result = await ReadAsync(body.ToArray());
        result.Contacts.Select(static c => c.FormattedName).Should().Equal("One", "Two");
        result.Entries.Select(static e => e.Version).Should().AllBe("4.0");
    }

    [Fact]
    public async Task ToFile_FromContactsProducedOneAtATime_GivesTheSameFile() {
        var buffered = await ContactWriter.ToFileAsync([Full, Organization], "c.vcf", cancellationToken: TestCancellation);
        var produced = await ContactWriter.ToFileAsync(AsAsync([Full, Organization]), "c.vcf", cancellationToken: TestCancellation);

        produced.Bytes.ToArray().Should().Equal(buffered.Bytes.ToArray());
    }

    [Fact]
    public void ToProperties_ListsTheCardsPropertiesInWriterOrder() {
        var properties = Organization.ToProperties(ContactFormatVersion.V4_0);

        properties.Select(static p => p.Name).Should().Equal("KIND", "FN", "ORG", "TEL", "PHOTO");
        Contact.TryFromProperties(properties, out var back, out _).Should().BeTrue();
        back.Should().Be(ExpectedBack(Organization));
    }

    private static Contact ExpectedBack(Contact contact) => new() {
        FormattedName = contact.FormattedName ?? contact.DisplayName,
        Name = contact.Name, Nicknames = contact.Nicknames, Kind = contact.Kind, Organization = contact.Organization,
        Department = contact.Department, Title = contact.Title, Role = contact.Role, Phones = contact.Phones, Emails = contact.Emails,
        Addresses = contact.Addresses, Urls = contact.Urls, Birthday = contact.Birthday, Anniversary = contact.Anniversary,
        Note = contact.Note, Categories = contact.Categories, Photo = contact.Photo, Uid = contact.Uid,
        AdditionalProperties = contact.AdditionalProperties,
    };

    private static async Task<string> WriteAsync(IEnumerable<Contact> contacts, ContactFormatVersion version = ContactFormatVersion.V3_0) {
        using var stream = new MemoryStream();
        await ContactWriter.WriteAsync(stream, contacts, new ContactWriteOptions { Version = version }, TestCancellation);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string Unfold(string text) => text.Replace("\r\n ", "", StringComparison.Ordinal);

    private static int CountOf(string text, string value) {
        var count = 0;
        for (var at = text.IndexOf(value, StringComparison.Ordinal); at >= 0; at = text.IndexOf(value, at + 1, StringComparison.Ordinal)) count++;
        return count;
    }

    private static List<byte[]> Split(byte[] bytes) {
        var lines = new List<byte[]>();
        var start = 0;
        for (var i = 0; i + 1 < bytes.Length; i++) {
            if (bytes[i] != '\r' || bytes[i + 1] != '\n') continue;
            lines.Add(bytes[start..i]);
            start = i + 2;
        }

        return lines;
    }

    private static async IAsyncEnumerable<Contact> AsAsync(IEnumerable<Contact> contacts) {
        foreach (var contact in contacts) {
            await Task.Yield();
            yield return contact;
        }
    }

    private static async IAsyncEnumerable<Contact> Produce(int count, ObservedStream body, [EnumeratorCancellation] CancellationToken cancellationToken = default) {
        for (var i = 1; i <= count; i++) {
            cancellationToken.ThrowIfCancellationRequested();
            if (i == count) body.MarkLastProduced();
            if (i % 10_000 == 0) await Task.Yield();
            yield return new Contact { FormattedName = $"Person {i}", Emails = [new ContactEmail($"p{i}@example.com")] };
        }
    }

    /// <summary>A body that keeps only counts and its last bytes, so the test's memory does not grow with the file.</summary>
    private sealed class ObservedStream : Stream {
        private readonly byte[] _tail = new byte[256];
        private int _tailLength;
        private long _length;

        public long BytesBeforeLastProduced { get; private set; } = -1;

        public int LargestWrite { get; private set; }

        public byte[] Tail => _tail[.._tailLength];

        public void MarkLastProduced() => BytesBeforeLastProduced = _length;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _length;
        public override long Position { get => _length; set => throw new NotSupportedException(); }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer) {
            _length += buffer.Length;
            LargestWrite = Math.Max(LargestWrite, buffer.Length);
            var keep = Math.Min(buffer.Length, _tail.Length);
            buffer[^keep..].CopyTo(_tail);
            _tailLength = keep;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) {
            Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) {
            Write(buffer.AsSpan(offset, count));
            return Task.CompletedTask;
        }

        public override void Flush() {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData(ContactFormatVersion.V3_0)]
    [InlineData(ContactFormatVersion.V4_0)]
    public async Task AWebAddress_IsWrittenAsAUriNotEscapedAsText(ContactFormatVersion version) {
        var contact = new Contact { FormattedName = "A", Urls = [new ContactUrl("https://example.com/a;b,c?x=1")] };

        var text = await WriteAsync([contact], version);

        text.Should().Contain("URL:https://example.com/a;b,c?x=1\r\n");
        (await ReadAsync(text)).Contacts.Single().Urls.Single().Value.Should().Be("https://example.com/a;b,c?x=1");
    }

    [Theory]
    [InlineData(ContactFormatVersion.V3_0)]
    [InlineData(ContactFormatVersion.V4_0)]
    public async Task AWebAddressWithALineBreakOrTab_IsWrittenWithoutThem_AsAUrlParserReadsIt(ContactFormatVersion version) {
        var contact = new Contact { FormattedName = "A", Urls = [new ContactUrl("https://example.com/\r\nX-EVIL:1\tz")] };

        var text = await WriteAsync([contact], version);

        text.Should().Contain("URL:https://example.com/X-EVIL:1z\r\n").And.NotContain("\r\nX-EVIL");
        (await ReadAsync(text)).Contacts.Single().Urls.Single().Value.Should().Be("https://example.com/X-EVIL:1z");
    }

    [Fact]
    public async Task ATelUriWithALineBreak_IsWrittenWithoutIt() {
        var contact = new Contact { FormattedName = "A", Phones = [new ContactPhone("tel:+43-1-\n234")] };

        var text = await WriteAsync([contact], ContactFormatVersion.V4_0);

        text.Should().Contain("TEL;VALUE=uri:tel:+43-1-234\r\n");
    }

    [Fact]
    public async Task ATelUriInVersion40_IsWrittenAsAUriValue() {
        var contact = new Contact { FormattedName = "A", Phones = [new ContactPhone("tel:+43-1-234;ext=5") { Types = ContactTypes.Work }] };

        var v4 = await WriteAsync([contact], ContactFormatVersion.V4_0);
        var v3 = await WriteAsync([contact], ContactFormatVersion.V3_0);

        v4.Should().Contain("TEL;VALUE=uri;TYPE=work:tel:+43-1-234;ext=5\r\n");
        v3.Should().Contain("TEL;TYPE=WORK:tel:+43-1-234\\;ext=5\r\n");
        (await ReadAsync(v4)).Contacts.Single().Phones.Single().Number.Should().Be("tel:+43-1-234;ext=5");
    }

    [Fact]
    public async Task ControlCharacters_AreLeftOutOfEveryValue() {
        var contact = new Contact {
            FormattedName = "A\u0000B",
            Note = "a\u0000b\u0007c\td\u0085e\u007Ff\ng",
            AdditionalProperties = [new ContactProperty(null, "X-RAW", [new ContactParameter("X-P", "p\u0001q")], "r\u001Bs")],
        };

        var text = await WriteAsync([contact]);

        text.Should().Contain("FN:AB\r\n");
        text.Should().Contain("NOTE:abc\tdef\\ng\r\n");
        text.Should().Contain("X-RAW;X-P=pq:rs\r\n");
        text.Where(static c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')).Should().BeEmpty();
    }

    [Fact]
    public async Task FoldLinesOff_WritesEachPropertyOnOneLine() {
        var note = new string('x', 200);
        var contact = new Contact { FormattedName = "A", Note = note };

        var text = Encoding.UTF8.GetString((await ContactWriter.ToFileAsync([contact], "a.vcf", new ContactWriteOptions { FoldLines = false }, TestCancellation)).Bytes.Span);

        text.Should().Contain("NOTE:" + note + "\r\n");
        (await ReadAsync(text)).Contacts.Single().Note.Should().Be(note);
    }

    [Fact]
    public async Task AFoldNeverSplitsABackslashFromTheCharacterItEscapes() {
        for (var prefix = 0; prefix < 4; prefix++) {
            var note = new string('a', prefix) + string.Concat(Enumerable.Repeat(";\\", 60));
            var bytes = Encoding.UTF8.GetBytes(await WriteAsync([new Contact { FormattedName = "A", Note = note }]));

            foreach (var line in Split(bytes)) {
                var run = 0;
                for (var i = line.Length - 1; i >= 0 && line[i] == (byte)'\\'; i--) run++;
                (run % 2).Should().Be(0, "a physical line must not end inside an escape");
                line.Length.Should().BeLessThanOrEqualTo(75);
            }

            (await ReadAsync(bytes)).Contacts.Single().Note.Should().Be(note);
        }
    }

    [Fact]
    public async Task TheInternetType_IsReadAndWritten() {
        var contact = new Contact { FormattedName = "A", Emails = [new ContactEmail("a@example.com") { Types = ContactTypes.Internet | ContactTypes.Work }] };

        var text = await WriteAsync([contact]);

        text.Should().Contain("EMAIL;TYPE=INTERNET,WORK:a@example.com\r\n");
        (await ReadAsync(text)).Contacts.Single().Should().Be(contact);
    }

    [Fact]
    public async Task ToTextAndWrite_WriteSynchronouslyWhatTheAsynchronousWriterWrites() {
        var expected = await WriteAsync([Full, Organization]);
        using var stream = new MemoryStream();

        ContactWriter.Write(stream, [Full, Organization]);

        Encoding.UTF8.GetString(stream.ToArray()).Should().Be(expected);
        ContactWriter.ToText(Organization).Should().Be(await WriteAsync([Organization]));
    }

    [Fact]
    public async Task APropertyList_IsWrittenSynchronouslyInItsOwnOrderAndSpelling() {
        var card = new List<ContactProperty> {
            new("UID", "u-1"),
            new(null, "EMAIL", [new ContactParameter("type", ["INTERNET", "pref"])], "a@example.com"),
            new("FN", "Ada"),
        };
        var expected = await WriteCardsAsync([card]);
        using var stream = new MemoryStream();

        ContactWriter.Write(stream, [card, card]);
        var text = ContactWriter.ToText(card, new ContactWriteOptions { FoldLines = false });

        text.Should().Be("BEGIN:VCARD\r\nVERSION:3.0\r\nUID:u-1\r\nEMAIL;type=INTERNET,pref:a@example.com\r\nFN:Ada\r\nEND:VCARD\r\n");
        text.Should().Be(expected);
        Encoding.UTF8.GetString(stream.ToArray()).Should().Be(expected + expected);
    }

    [Fact]
    public void APropertyListTheWriterCannotRepresent_FailsTheSynchronousWrite() {
        var write = () => ContactWriter.ToText([new ContactProperty("FN", "A\r\nX-EVIL:1")]);

        write.Should().Throw<ContactWriteException>().Which.Property.Should().Be("FN");
    }

    private static async Task<string> WriteCardsAsync(IEnumerable<IReadOnlyList<ContactProperty>> cards) {
        using var stream = new MemoryStream();
        await using (var writer = ContactWriter.Create(stream)) {
            foreach (var card in cards) await writer.AddAsync(card, TestCancellation);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
