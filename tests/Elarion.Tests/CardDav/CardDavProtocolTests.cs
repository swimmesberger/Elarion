using AwesomeAssertions;
using Elarion.CardDav;
using Elarion.Contacts;
using Elarion.WebDav;
using Xunit;

namespace Elarion.Tests.CardDav;

/// <summary>
/// "Add an account in a contacts app" (spec 0007, P1) at the dispatcher: multiget, the vCard version a client asks for,
/// partial cards, read-only privileges and refused writes, an expired sync token (FR-019–FR-021, FR-025–FR-029).
/// </summary>
public sealed class CardDavProtocolTests {
    private const string Book = "/addressbooks/alice/customers.all/";
    private const string BookHref = "/dav/addressbooks/alice/customers.all/";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<CardDavTestHarness> WithContactsAsync() {
        var harness = CardDavTestHarness.Create();
        await harness.PublishAsync("customers", "all", CardDavTestData.Person("c-1", "Anna", "Berger", "anna@example.com", "+43 1 234", "Key account"));
        await harness.PublishAsync("customers", "all", CardDavTestData.Person("c-2", "Bernd", "Huber", "bernd@example.org"));
        return harness;
    }

    [Fact]
    public async Task AddressbookMultiget_AnswersTheNamedContactsInOrderAndA404ForAnUnknownHref() {
        await using var harness = await WithContactsAsync();

        var answer = await harness.SendAsync("REPORT", Book,
            CardDavTestHarness.Multiget("<CARD:address-data/>", BookHref + "c-2.vcf", BookHref + "missing.vcf", "https://example.com" + BookHref + "c-1.vcf"));

        answer.Status.Should().Be(207);
        answer.Responses.Keys.Should().Equal(BookHref + "c-2.vcf", BookHref + "missing.vcf", BookHref + "c-1.vcf");
        answer.Prop(BookHref + "c-2.vcf", DavNames.Card.AddressData)!.Text.Should().Contain("FN:Bernd Huber").And.Contain("VERSION:3.0");
        answer.Responses[BookHref + "missing.vcf"].Child(DavNames.D.Status)!.Text.Should().Contain("404");
    }

    [Fact]
    public async Task GivenAClientThatAsksForVCard40_WhenItFetchesContacts_ThenItReceivesVCard40WithTheSameETag_AndWithoutAVersionVCard30() {
        await using var harness = await WithContactsAsync();

        var v3 = await harness.SendAsync("REPORT", Book, CardDavTestHarness.Multiget("<CARD:address-data/>", BookHref + "c-1.vcf"));
        var v4 = await harness.SendAsync("REPORT", Book,
            CardDavTestHarness.Multiget("<CARD:address-data content-type=\"text/vcard\" version=\"4.0\"/>", BookHref + "c-1.vcf"));

        var card3 = v3.Prop(BookHref + "c-1.vcf", DavNames.Card.AddressData)!.Text;
        var card4 = v4.Prop(BookHref + "c-1.vcf", DavNames.Card.AddressData)!.Text;
        card3.Should().Contain("VERSION:3.0");
        card4.Should().Contain("VERSION:4.0").And.Contain("FN:Anna Berger").And.Contain("UID:c-1").And.Contain("anna@example.com");
        v4.Prop(BookHref + "c-1.vcf", DavNames.D.GetETag)!.Text.Should().Be(v3.Prop(BookHref + "c-1.vcf", DavNames.D.GetETag)!.Text);
        (await ReadAsync(card3)).Should().Be(await ReadAsync(card4));
    }

    [Fact]
    public async Task AddressDataNamingProperties_AnswersOnlyThoseWithVersionUidAndFn() {
        await using var harness = await WithContactsAsync();

        var answer = await harness.SendAsync("REPORT", Book, CardDavTestHarness.Multiget(
            "<CARD:address-data><CARD:prop name=\"EMAIL\"/><CARD:prop name=\"NOTE\" novalue=\"yes\"/></CARD:address-data>", BookHref + "c-1.vcf"));

        var card = answer.Prop(BookHref + "c-1.vcf", DavNames.Card.AddressData)!.Text;
        card.Should().Contain("VERSION:3.0").And.Contain("UID:c-1").And.Contain("FN:Anna Berger").And.Contain("anna@example.com")
            .And.Contain("NOTE:\r\n").And.NotContain("Key account").And.NotContain("TEL").And.NotContain("\r\nN:");
    }

    [Fact]
    public async Task AddressDataOfAnUnsupportedVersionOrContentType_Answers403SupportedAddressData() {
        await using var harness = await WithContactsAsync();

        var version = await harness.SendAsync("REPORT", Book, CardDavTestHarness.Multiget("<CARD:address-data version=\"2.1\"/>", BookHref + "c-1.vcf"));
        var json = await harness.SendAsync("REPORT", Book,
            CardDavTestHarness.Multiget("<CARD:address-data content-type=\"application/vcard+json\"/>", BookHref + "c-1.vcf"));

        version.Status.Should().Be(403);
        version.Xml.Child(DavNames.Card.SupportedAddressData).Should().NotBeNull();
        json.Status.Should().Be(403);
    }

    [Fact]
    public async Task Get_AnswersTheStoredCard_HonoursIfNoneMatch_AndAnswers40WhenAcceptPrefersIt() {
        await using var harness = await WithContactsAsync();

        var plain = await harness.SendAsync("GET", Book + "c-1.vcf");
        var notModified = await harness.SendAsync("GET", Book + "c-1.vcf", headers: new Dictionary<string, string> { ["If-None-Match"] = plain.Headers["ETag"] });
        var v4 = await harness.SendAsync("GET", Book + "c-1.vcf",
            headers: new Dictionary<string, string> { ["Accept"] = "text/vcard;q=0.8, text/vcard;version=4.0" });
        var v3Preferred = await harness.SendAsync("GET", Book + "c-1.vcf",
            headers: new Dictionary<string, string> { ["Accept"] = "text/vcard, text/vcard;version=4.0;q=0.5" });

        plain.ContentType.Should().Be("text/vcard; charset=utf-8");
        plain.Body.Should().Contain("VERSION:3.0");
        notModified.Status.Should().Be(304);
        v4.Body.Should().Contain("VERSION:4.0");
        v4.ContentType.Should().Contain("version=4.0");
        v4.Headers["ETag"].Should().Be(plain.Headers["ETag"]);
        v3Preferred.Body.Should().Contain("VERSION:3.0");
    }

    [Fact]
    public void AcceptNegotiation_Prefers30UnlessAVersion40RangeRanksHigher() {
        CardDavAddressData.PreferredVersion(null).Should().Be(ContactFormatVersion.V3_0);
        CardDavAddressData.PreferredVersion("*/*").Should().Be(ContactFormatVersion.V3_0);
        CardDavAddressData.PreferredVersion("text/vcard;version=4.0").Should().Be(ContactFormatVersion.V4_0);
        CardDavAddressData.PreferredVersion("text/vcard; version=\"4.0\", */*;q=0.1").Should().Be(ContactFormatVersion.V4_0);
        CardDavAddressData.PreferredVersion("text/vcard, text/vcard;version=4.0").Should().Be(ContactFormatVersion.V3_0);
    }

    [Fact]
    public async Task GivenAPublishedAddressBook_WhenAClientAsksForItsPrivileges_ThenItReceivesReadOnly() {
        await using var harness = await WithContactsAsync();

        var answer = await harness.SendAsync("PROPFIND", Book, CardDavTestHarness.PropFind("D:current-user-privilege-set"),
            headers: new Dictionary<string, string> { ["Depth"] = "0" });

        answer.Prop(BookHref, DavNames.D.CurrentUserPrivilegeSet)!.ChildrenNamed(DavNames.D.Privilege).SelectMany(static p => p.Children)
            .Select(static p => p.Name.LocalName).Should().Equal("read", "read-current-user-privilege-set");
    }

    [Theory]
    [InlineData("PUT", "c-1.vcf")]
    [InlineData("PUT", "new.vcf")]
    [InlineData("DELETE", "c-1.vcf")]
    [InlineData("PROPPATCH", "")]
    [InlineData("MKCOL", "sub/")]
    [InlineData("MOVE", "c-1.vcf")]
    [InlineData("COPY", "c-1.vcf")]
    public async Task GivenAReadOnlyAddressBook_WhenAClientWrites_ThenTheAnswerIs403NeedPrivilegesAndNothingChanges(string method, string target) {
        await using var harness = await WithContactsAsync();
        var before = (await harness.SendAsync("REPORT", Book, CardDavTestHarness.SyncCollection(null))).SyncToken;

        var answer = await harness.SendAsync(method, Book + target, "BEGIN:VCARD\r\nVERSION:3.0\r\nFN:X\r\nEND:VCARD\r\n");

        answer.Status.Should().Be(403);
        answer.Xml.Child(DavNames.D.NeedPrivileges).Should().NotBeNull();
        (await harness.SendAsync("REPORT", Book, CardDavTestHarness.SyncCollection(null))).SyncToken.Should().Be(before);
        (await harness.SendAsync("GET", Book + "c-1.vcf")).Body.Should().Contain("FN:Anna Berger");
    }

    [Fact]
    public async Task GivenATokenOlderThanTheJournal_WhenAClientSyncs_ThenItGets403ValidSyncTokenAndAFullResyncEndsWithTheSameContacts() {
        await using var harness = CardDavTestHarness.Create(webDav: o => o.MaxJournalRowsPerCollection = 1);
        await harness.PublishAsync("customers", "all", CardDavTestData.Person("c-1", "Anna", "Berger"));
        var old = (await harness.SendAsync("REPORT", Book, CardDavTestHarness.SyncCollection(null))).SyncToken;
        await harness.PublishAsync("customers", "all", CardDavTestData.Person("c-2", "Bernd", "Huber"));
        await harness.PublishAsync("customers", "all", CardDavTestData.Person("c-3", "Clara", "Maier"));

        var refused = await harness.SendAsync("REPORT", Book, CardDavTestHarness.SyncCollection(old));
        var full = await harness.SendAsync("REPORT", Book, CardDavTestHarness.SyncCollection(null));

        refused.Status.Should().Be(403);
        refused.Xml.Child(DavNames.D.ValidSyncToken).Should().NotBeNull();
        full.Responses.Keys.Should().BeEquivalentTo(BookHref + "c-1.vcf", BookHref + "c-2.vcf", BookHref + "c-3.vcf");
    }

    [Fact]
    public async Task SupportedReportSet_ListsSyncCollectionAndTheAddressBookReports() {
        await using var harness = await WithContactsAsync();

        var answer = await harness.SendAsync("PROPFIND", Book, CardDavTestHarness.PropFind("D:supported-report-set"),
            headers: new Dictionary<string, string> { ["Depth"] = "0" });

        answer.Prop(BookHref, DavNames.D.SupportedReportSet)!.Children.Select(static r => r.Child(DavNames.D.Report)!.Children[0].Name.LocalName)
            .Should().Contain(["sync-collection", "addressbook-multiget"]);
    }

    private static async Task<Contact> ReadAsync(string card) {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(card));
        var read = await new ContactReader().ReadAsync(stream, cancellationToken: Ct);
        return read.Contacts.Single();
    }
}
