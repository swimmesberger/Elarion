using AwesomeAssertions;
using Elarion.CardDav;
using Elarion.Contacts;
using Elarion.WebDav;
using Xunit;

namespace Elarion.Tests.CardDav;

/// <summary>"Search an address book from a client" (spec 0007, P2) and FR-022–FR-024.</summary>
public sealed class CardDavReportTests {
    private const string Book = "/addressbooks/alice/customers.all/";
    private const string BookHref = "/dav/addressbooks/alice/customers.all/";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IEnumerable<string> Members(Tests.WebDav.DavTestResponse answer) =>
        answer.Responses.Where(static r => r.Value.Child(DavNames.D.Status) is null).Select(static r => r.Key);

    [Fact]
    public async Task GivenAnnaBergerAndBerndHuber_WhenFnContainsBer_ThenBothAnswer_AndWhenFnStartsWithBer_ThenOnlyBernd() {
        await using var harness = CardDavTestHarness.Create();
        await harness.PublishAsync("customers", "all", CardDavTestData.Person("anna", "Anna", "Berger"));
        await harness.PublishAsync("customers", "all", CardDavTestData.Person("bernd", "Bernd", "Huber"));

        var contains = await harness.SendAsync("REPORT", Book,
            CardDavTestHarness.Query("<CARD:prop-filter name=\"FN\"><CARD:text-match>ber</CARD:text-match></CARD:prop-filter>"));
        var startsWith = await harness.SendAsync("REPORT", Book,
            CardDavTestHarness.Query("<CARD:prop-filter name=\"FN\"><CARD:text-match match-type=\"starts-with\">ber</CARD:text-match></CARD:prop-filter>"));

        contains.Status.Should().Be(207);
        Members(contains).Should().BeEquivalentTo(BookHref + "anna.vcf", BookHref + "bernd.vcf");
        Members(startsWith).Should().Equal(BookHref + "bernd.vcf");
        startsWith.Prop(BookHref + "bernd.vcf", DavNames.Card.AddressData)!.Text.Should().Contain("FN:Bernd Huber");
    }

    [Fact]
    public async Task GivenAContactWithWorkAndHomeEmail_WhenQueriedForAWorkEmailEndingWithExampleCom_ThenItMatchesOnlyIfItsWorkAddressDoes() {
        await using var harness = CardDavTestHarness.Create();
        await harness.PublishAsync("customers", "all", new Contact {
            Uid = "match", FormattedName = "Work Match",
            Emails = [new ContactEmail("w@example.com") { Types = ContactTypes.Work }, new ContactEmail("h@private.org") { Types = ContactTypes.Home }],
        });
        await harness.PublishAsync("customers", "all", new Contact {
            Uid = "nomatch", FormattedName = "Home Only",
            Emails = [new ContactEmail("w@private.org") { Types = ContactTypes.Work }, new ContactEmail("h@example.com") { Types = ContactTypes.Home }],
        });

        var answer = await harness.SendAsync("REPORT", Book, CardDavTestHarness.Query(
            "<CARD:prop-filter name=\"EMAIL\" test=\"allof\">"
            + "<CARD:param-filter name=\"TYPE\"><CARD:text-match match-type=\"equals\">work</CARD:text-match></CARD:param-filter>"
            + "<CARD:text-match match-type=\"ends-with\">@example.com</CARD:text-match></CARD:prop-filter>"));

        Members(answer).Should().Equal(BookHref + "match.vcf");
    }

    [Fact]
    public async Task GivenAQueryWithLimit10Matching25_ThenTheAnswerHolds10AndA507ForTheRequestUrl() {
        await using var harness = CardDavTestHarness.Create();
        await harness.PublisherAsync(p => p.ReplaceAsync(new CardDavAddress("customers", "all"),
            Enumerable.Range(0, 25).Select(static i => CardDavTestData.Person($"c{i:00}", "Given", "Family" + i)), Ct).AsTask());

        var answer = await harness.SendAsync("REPORT", Book,
            CardDavTestHarness.Query("<CARD:prop-filter name=\"FN\"><CARD:text-match>family</CARD:text-match></CARD:prop-filter>", limit: 10));

        Members(answer).Should().HaveCount(10);
        answer.Responses[BookHref].Child(DavNames.D.Status)!.Text.Should().Contain("507");
    }

    [Fact]
    public async Task GivenAQueryWithAnUnsupportedCollation_ThenTheAnswerIs403SupportedCollation() {
        await using var harness = CardDavTestHarness.Create();

        var answer = await harness.SendAsync("REPORT", Book, CardDavTestHarness.Query(
            "<CARD:prop-filter name=\"FN\"><CARD:text-match collation=\"i;klingon\">x</CARD:text-match></CARD:prop-filter>"));

        answer.Status.Should().Be(403);
        answer.Xml.Child(DavNames.Card.SupportedCollation).Should().NotBeNull();
    }

    [Fact]
    public async Task GivenAQueryWithoutAFilter_ThenEveryContactIsInTheAnswer_EvenWithDepth0AsEvolutionSendsIt() {
        await using var harness = CardDavTestHarness.Create();
        await harness.PublishAsync("customers", "all", CardDavTestData.Person("anna", "Anna", "Berger"));
        await harness.PublishAsync("customers", "all", CardDavTestData.Person("bernd", "Bernd", "Huber"));

        var answer = await harness.SendAsync("REPORT", Book,
            CardDavTestHarness.Query(null, "<CARD:address-data><CARD:prop name=\"VERSION\"/><CARD:prop name=\"UID\"/></CARD:address-data>"),
            headers: new Dictionary<string, string> { ["Depth"] = "0" });

        Members(answer).Should().BeEquivalentTo(BookHref + "anna.vcf", BookHref + "bernd.vcf");
        answer.Prop(BookHref + "anna.vcf", DavNames.Card.AddressData)!.Text.Should().Contain("UID:anna").And.NotContain("EMAIL");
    }

    [Fact]
    public async Task AnUnknownMatchTypeOrAFilterWithoutName_Answers403SupportedFilter() {
        await using var harness = CardDavTestHarness.Create();

        var matchType = await harness.SendAsync("REPORT", Book, CardDavTestHarness.Query(
            "<CARD:prop-filter name=\"FN\"><CARD:text-match match-type=\"sounds-like\">x</CARD:text-match></CARD:prop-filter>"));
        var nameless = await harness.SendAsync("REPORT", Book, CardDavTestHarness.Query("<CARD:prop-filter/>"));

        matchType.Status.Should().Be(403);
        matchType.Xml.Child(DavNames.Card.SupportedFilter).Should().NotBeNull();
        nameless.Status.Should().Be(403);
    }

    [Fact]
    public async Task SupportedReportSet_ListsAddressbookQuery() {
        await using var harness = CardDavTestHarness.Create();

        var answer = await harness.SendAsync("PROPFIND", Book, CardDavTestHarness.PropFind("D:supported-report-set", "CARD:supported-collation-set"),
            headers: new Dictionary<string, string> { ["Depth"] = "0" });

        answer.Prop(BookHref, DavNames.D.SupportedReportSet)!.Children.Select(static r => r.Child(DavNames.D.Report)!.Children[0].Name.LocalName)
            .Should().Equal("sync-collection", "addressbook-multiget", "addressbook-query");
        answer.Prop(BookHref, DavNames.Card.SupportedCollationSet)!.Children.Select(static c => c.Text)
            .Should().Contain(["i;unicode-casemap", "i;ascii-casemap", "i;octet"]);
    }
}
