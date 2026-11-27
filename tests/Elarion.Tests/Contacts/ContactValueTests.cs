using AwesomeAssertions;
using Elarion.Contacts;
using Xunit;
using static Elarion.Tests.Contacts.VCardFiles;

namespace Elarion.Tests.Contacts;

public sealed class ContactValueTests {
    [Theory]
    [InlineData("BDAY:19800322")]
    [InlineData("BDAY:1980-03-22")]
    [InlineData("BDAY:1980-03-22T00:00:00Z")]
    [InlineData("BDAY;value=date:1980-03-22")]
    public async Task Birthdays_ReadInEveryProducersForm(string line) {
        (await ReadOneAsync(Card("FN:A", line))).Birthday.Should().Be(new ContactDate(1980, 3, 22));
    }

    [Theory]
    [InlineData("BDAY:--0322")]
    [InlineData("BDAY:--03-22")]
    [InlineData("BDAY;X-APPLE-OMIT-YEAR=1604:1604-03-22")]
    [InlineData("BDAY:1604-03-22")]
    public async Task BirthdaysWithoutAYear_ReadWithoutAYear(string line) {
        (await ReadOneAsync(Card("FN:A", line))).Birthday.Should().Be(new ContactDate(null, 3, 22));
    }

    [Theory]
    [InlineData("ANNIVERSARY:20100612")]
    [InlineData("X-ANNIVERSARY:2010-06-12")]
    [InlineData("X-MS-ANNIVERSARY:2010-06-12")]
    public async Task Anniversaries_ReadFromEveryProducersProperty(string line) {
        var contact = await ReadOneAsync(Card("FN:A", line));

        contact.Anniversary.Should().Be(new ContactDate(2010, 6, 12));
        contact.AdditionalProperties.Should().BeEmpty();
    }

    [Fact]
    public async Task AppleAnniversary_ReadsFromALabelledDate() {
        var contact = await ReadOneAsync(Card("FN:A", "item3.X-ABDATE;type=pref:2010-06-12", "item3.X-ABLabel:_$!<Anniversary>!$_"));

        contact.Anniversary.Should().Be(new ContactDate(2010, 6, 12));
        contact.AdditionalProperties.Should().BeEmpty();
    }

    [Fact]
    public async Task Anniversary_TakesTheSourcesInTheirOrderOfPrecedence() {
        var contact = await ReadOneAsync(Card(
            "FN:A", "item1.X-ABDATE:2001-01-01", "item1.X-ABLabel:_$!<Anniversary>!$_", "X-MS-ANNIVERSARY:2002-02-02",
            "X-ANNIVERSARY:2003-03-03", "ANNIVERSARY:20040404"));

        contact.Anniversary.Should().Be(new ContactDate(2004, 4, 4));
    }

    [Fact]
    public async Task AnUnlabelledAppleDate_StaysAnAdditionalProperty() {
        var contact = await ReadOneAsync(Card("FN:A", "item1.X-ABDATE:2001-01-01", "item1.X-ABLabel:_$!<Other>!$_"));

        contact.Anniversary.Should().BeNull();
        contact.AdditionalProperties.Select(static p => p.Name).Should().Equal("X-ABDATE", "X-ABLABEL");
    }

    [Theory]
    [InlineData("KIND:org")]
    [InlineData("X-ABSHOWAS:COMPANY")]
    public async Task Organizations_AreMarked(string line) {
        (await ReadOneAsync(Card("FN:ACME", line))).Kind.Should().Be(ContactKind.Organization);
    }

    [Fact]
    public async Task OtherContacts_AreIndividuals() {
        (await ReadOneAsync(Card("FN:A", "X-ABSHOWAS:PERSON"))).Kind.Should().Be(ContactKind.Individual);
    }

    [Fact]
    public async Task StructuredName_ReadsEveryPartAndJoinsSeveralValues() {
        var contact = await ReadOneAsync(Card("N:Doe;John;Richter,James;Mr.;Sr."));

        contact.Name.Should().Be(new ContactName("Doe", "John", "Richter, James", "Mr.", "Sr."));
        contact.DisplayName.Should().Be("Mr. John Richter, James Doe Sr.");
    }

    [Fact]
    public async Task AnAddressWithAnEighthComponent_ReadsTheFirstSeven() {
        var contact = await ReadOneAsync(Card("FN:A", "ADR;TYPE=home:Box 1;Apt 2;Street 3;City;Region;1234;Country;Extra"));

        contact.Addresses.Should().Equal(new ContactAddress {
            PostOfficeBox = "Box 1", Extended = "Apt 2", Street = "Street 3", Locality = "City", Region = "Region", PostalCode = "1234",
            Country = "Country", Types = ContactTypes.Home,
        });
    }

    [Fact]
    public async Task PreferredMarks_ReadInEveryForm() {
        var contact = await ReadOneAsync(Lines(
            "BEGIN:VCARD", "VERSION:3.0", "FN:A", "TEL;TYPE=CELL,WORK;TYPE=pref:1", "TEL;PREF=1:2", "TEL;CELL;PREF:3", "END:VCARD"));

        contact.Phones.Should().OnlyContain(static phone => phone.Preferred);
        contact.Phones[0].Types.Should().Be(ContactTypes.Cell | ContactTypes.Work);
        contact.Phones[2].Types.Should().Be(ContactTypes.Cell);
    }

    [Fact]
    public async Task AppleLabels_MapToFlags() {
        var contact = await ReadOneAsync(Card(
            "FN:A",
            "item1.TEL:1", "item1.X-ABLabel:_$!<iPhone>!$_",
            "item2.TEL:2", "item2.X-ABLabel:_$!<HomeFAX>!$_",
            "item3.TEL:3", "item3.X-ABLabel:_$!<WorkFAX>!$_",
            "item4.TEL:4", "item4.X-ABLabel:_$!<Pager>!$_",
            "item5.TEL:5", "item5.X-ABLabel:_$!<Main>!$_",
            "item6.URL:https://example.com", "item6.X-ABLabel:_$!<HomePage>!$_",
            "item7.EMAIL:w@example.com", "item7.X-ABLabel:_$!<Work>!$_",
            "item8.EMAIL:h@example.com", "item8.X-ABLabel:_$!<Home>!$_",
            "item9.TEL:9", "item9.X-ABLabel:_$!<Other>!$_"));

        contact.Phones.Select(static p => p.Types).Should().Equal(
            ContactTypes.Cell, ContactTypes.Home | ContactTypes.Fax, ContactTypes.Work | ContactTypes.Fax, ContactTypes.Pager,
            ContactTypes.Main, ContactTypes.None);
        contact.Phones[^1].Label.Should().Be("Other");
        contact.Urls[0].Types.Should().Be(ContactTypes.Home);
        contact.Emails.Select(static e => e.Types).Should().Equal(ContactTypes.Work, ContactTypes.Home);
        contact.AdditionalProperties.Should().BeEmpty();
    }

    [Fact]
    public async Task SingleValuedProperties_TakeTheFirstAndKeepTheRest() {
        var contact = await ReadOneAsync(Card("FN:First", "FN:Second", "NOTE:one", "NOTE:two"));

        contact.FormattedName.Should().Be("First");
        contact.Note.Should().Be("one");
        contact.AdditionalProperties.Select(static p => p.Value).Should().Equal("Second", "two");
    }

    [Fact]
    public async Task AgentValues_AreNeverParsedAsVCards() {
        var contact = await ReadOneAsync(Card("FN:Boss", "AGENT:BEGIN:VCARD\\nFN:Assistant\\nEND:VCARD"));

        contact.FormattedName.Should().Be("Boss");
        contact.AdditionalProperties.Should().Equal(new ContactProperty("AGENT", "BEGIN:VCARD\\nFN:Assistant\\nEND:VCARD"));
    }

    [Fact]
    public async Task AVCard21AgentOnItsOwnLines_StaysTextOfTheAgentProperty() {
        var result = await ReadAsync(Lines(
            "BEGIN:VCARD", "VERSION:2.1", "FN:Boss", "AGENT:", "BEGIN:VCARD", "VERSION:2.1", "FN:Assistant", "END:VCARD", "TEL:1", "END:VCARD"));

        var entry = Assert.Single(result.Entries);
        entry.Errors.Should().BeEmpty();
        entry.Value!.FormattedName.Should().Be("Boss");
        entry.Value.Phones.Should().ContainSingle();
        entry.Value.AdditionalProperties.Should().Equal(new ContactProperty("AGENT", "BEGIN:VCARD\\nVERSION:2.1\\nFN:Assistant\\nEND:VCARD"));
    }

    [Fact]
    public void TryFromProperties_MapsAListBuiltInCode() {
        var ok = Contact.TryFromProperties(
            [new ContactProperty("FN", "Jane"), new ContactProperty(null, "TEL", [new ContactParameter("TYPE", "cell")], "1"), new ContactProperty("BDAY", "x")],
            out var contact, out var errors);

        ok.Should().BeTrue();
        contact!.Phones.Should().Equal(new ContactPhone("1") { Types = ContactTypes.Cell });
        Assert.Single(errors).Code.Should().Be(ContactErrorCodes.InvalidValue);
        Contact.TryFromProperties([new ContactProperty("NOTE", "only")], out var none, out var emptyErrors).Should().BeFalse();
        none.Should().BeNull();
        Assert.Single(emptyErrors).Code.Should().Be(ContactErrorCodes.Empty);
    }

    [Fact]
    public void ContactDate_RejectsAnInvalidDateAndAcceptsLeapDayWithoutAYear() {
        var invalid = () => new ContactDate(1980, 2, 31);
        var leapless = () => new ContactDate(2001, 2, 29);

        invalid.Should().Throw<ArgumentOutOfRangeException>();
        leapless.Should().Throw<ArgumentOutOfRangeException>();
        new ContactDate(null, 2, 29).ToString().Should().Be("--02-29");
        new ContactDate(1980, 3, 22).ToDateOnly().Should().Be(new DateOnly(1980, 3, 22));
    }

    [Fact]
    public void Equality_CoversEveryMemberOfTheModel() {
        // A new member must join Contact.Equals; this count fails until the test below lists it too.
        typeof(Contact).GetProperties().Select(static p => p.Name).Should().HaveCount(21);
        var baseline = new Contact { FormattedName = "A" };
        Contact[] variants = [
            new() { FormattedName = "B" },
            new() { FormattedName = "A", Name = new ContactName("x", null, null, null, null) },
            new() { FormattedName = "A", Nicknames = ["n"] },
            new() { FormattedName = "A", Kind = ContactKind.Organization },
            new() { FormattedName = "A", Kind = ContactKind.Group },
            new() { FormattedName = "A", Kind = ContactKind.Group, Members = ["urn:uuid:a"] },
            new() { FormattedName = "A", Organization = "o" },
            new() { FormattedName = "A", Department = "d" },
            new() { FormattedName = "A", Title = "t" },
            new() { FormattedName = "A", Role = "r" },
            new() { FormattedName = "A", Phones = [new ContactPhone("1")] },
            new() { FormattedName = "A", Emails = [new ContactEmail("e")] },
            new() { FormattedName = "A", Addresses = [new ContactAddress { Street = "s" }] },
            new() { FormattedName = "A", Urls = [new ContactUrl("u")] },
            new() { FormattedName = "A", Birthday = new ContactDate(null, 1, 1) },
            new() { FormattedName = "A", Anniversary = new ContactDate(null, 1, 1) },
            new() { FormattedName = "A", Note = "n" },
            new() { FormattedName = "A", Categories = ["c"] },
            new() { FormattedName = "A", Photo = ContactPhoto.FromData(ContactTestImages.Png) },
            new() { FormattedName = "A", Uid = "u" },
            new() { FormattedName = "A", AdditionalProperties = [new ContactProperty("X-A", "1")] },
        ];

        variants.Should().OnlyContain(variant => !variant.Equals(baseline));
        new Contact { FormattedName = "A", Phones = [new ContactPhone("1")] }.Should().Be(new Contact { FormattedName = "A", Phones = [new ContactPhone("1")] });
        ContactPhoto.FromData(ContactTestImages.Png).Should().Be(ContactPhoto.FromData(ContactTestImages.Png.ToArray()));
    }
}
