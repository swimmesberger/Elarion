using AwesomeAssertions;
using Elarion.CardDav;
using Elarion.Contacts;
using Elarion.Tests.WebDav;
using Elarion.WebDav;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elarion.Tests.CardDav;

/// <summary>
/// "Publish a group inside an address book" (spec 0007, P2; FR-032–FR-034, ADR-0114), from sabre/vobject's
/// testConvertGroupCard (V05) and Cyrus's filter_x_props (C12) and huge_group (C31).
/// </summary>
public sealed class CardDavGroupTests {
    private const string Book = "/addressbooks/alice/customers.all/";
    private const string BookHref = "/dav/addressbooks/alice/customers.all/";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Contact Group(string uid, string name, IEnumerable<string> members) => new() {
        Uid = uid, FormattedName = name, Kind = ContactKind.Group, Members = [.. members],
    };

    private static async Task<CardDavTestHarness> WithTeamAsync() {
        var harness = CardDavTestHarness.Create();
        await harness.PublishAsync("customers", "all", CardDavTestData.Person("anna", "Anna", "Berger"));
        await harness.PublishAsync("customers", "all", CardDavTestData.Person("bernd", "Bernd", "Huber"));
        await harness.PublishAsync("customers", "all", Group("team-red", "Team red", [ContactMember.FromUid("anna"), ContactMember.FromUid("bernd")]));
        return harness;
    }

    private static IEnumerable<string> Members(DavTestResponse answer) =>
        answer.Responses.Where(static r => r.Value.Child(DavNames.D.Status) is null).Select(static r => r.Key);

    [Fact]
    public async Task GivenAPublishedGroup_WhenFetchedAs30And40_ThenEachHoldsTheGroupInItsVersionsFormUnderOneETag() {
        await using var harness = await WithTeamAsync();

        var v3 = await harness.SendAsync("REPORT", Book, CardDavTestHarness.Multiget("<CARD:address-data/>", BookHref + "team-red.vcf"));
        var v4 = await harness.SendAsync("REPORT", Book,
            CardDavTestHarness.Multiget("<CARD:address-data content-type=\"text/vcard\" version=\"4.0\"/>", BookHref + "team-red.vcf"));

        v3.Prop(BookHref + "team-red.vcf", DavNames.Card.AddressData)!.Text.Should().Contain("X-ADDRESSBOOKSERVER-KIND:group")
            .And.Contain("X-ADDRESSBOOKSERVER-MEMBER:urn:uuid:anna").And.Contain("X-ADDRESSBOOKSERVER-MEMBER:urn:uuid:bernd");
        v4.Prop(BookHref + "team-red.vcf", DavNames.Card.AddressData)!.Text.Should().Contain("KIND:group")
            .And.Contain("MEMBER:urn:uuid:anna").And.Contain("MEMBER:urn:uuid:bernd").And.NotContain("X-ADDRESSBOOKSERVER");
        v4.Prop(BookHref + "team-red.vcf", DavNames.D.GetETag)!.Text.Should().Be(v3.Prop(BookHref + "team-red.vcf", DavNames.D.GetETag)!.Text);
    }

    [Theory]
    [InlineData("KIND", "group")]
    [InlineData("X-ADDRESSBOOKSERVER-KIND", "group")]
    public async Task GivenAPublishedGroup_WhenQueriedForItsKind_ThenTheGroupIsAnsweredAndTheMembersAreNot(string property, string value) {
        await using var harness = await WithTeamAsync();

        var answer = await harness.SendAsync("REPORT", Book, CardDavTestHarness.Query(
            $"<CARD:prop-filter name=\"{property}\"><CARD:text-match match-type=\"equals\">{value}</CARD:text-match></CARD:prop-filter>"));

        Members(answer).Should().Equal(BookHref + "team-red.vcf");
    }

    // Cyrus C12: a member is searchable as Apple stores it, and under the 4.0 name.
    [Theory]
    [InlineData("MEMBER")]
    [InlineData("X-ADDRESSBOOKSERVER-MEMBER")]
    public async Task AGroupIsFoundByOneOfItsMembers(string property) {
        await using var harness = await WithTeamAsync();

        var answer = await harness.SendAsync("REPORT", Book, CardDavTestHarness.Query(
            $"<CARD:prop-filter name=\"{property}\"><CARD:text-match match-type=\"equals\">urn:uuid:bernd</CARD:text-match></CARD:prop-filter>"));

        Members(answer).Should().Equal(BookHref + "team-red.vcf");
    }

    // Cyrus C31: 2,500 members within a raised limit; the default limit refuses the publish.
    [Fact]
    public async Task GivenAGroupWith2500Members_WhenTheLimitIsRaised_ThenItIsPublishedAndFetchedWhole_AndWithTheDefaultItThrows() {
        var members = Enumerable.Range(0, 2_500).Select(static i => ContactMember.FromUid("m" + i)).ToList();
        await using (var defaults = CardDavTestHarness.Create()) {
            await ((Func<Task>)(() => defaults.PublishAsync("customers", "all", Group("huge", "Huge", members))))
                .Should().ThrowAsync<ArgumentException>().WithMessage("*MaxPropertiesPerContact*");
        }

        await using var harness = CardDavTestHarness.Create(services =>
            services.Configure<CardDavOptions>(o => o.ContactRead = o.ContactRead with { MaxPropertiesPerContact = 3_000 }));
        await harness.PublishAsync("customers", "all", Group("huge", "Huge", members));

        var v4 = await harness.SendAsync("REPORT", Book,
            CardDavTestHarness.Multiget("<CARD:address-data content-type=\"text/vcard\" version=\"4.0\"/>", BookHref + "huge.vcf"));

        var card = v4.Prop(BookHref + "huge.vcf", DavNames.Card.AddressData)!.Text;
        card.Split("\nMEMBER:").Length.Should().Be(2_501);
    }

    [Fact]
    public async Task AGroupsMembers_AreNotCheckedAgainstTheAddressBook() {
        await using var harness = CardDavTestHarness.Create();

        (await harness.PublishAsync("customers", "all", Group("ghosts", "Ghosts", [ContactMember.FromUid("nobody")]))).Should().BeTrue();
    }

    [Fact]
    public async Task ReplacingAnAddressBook_PublishesItsGroups() {
        await using var harness = CardDavTestHarness.Create();

        var outcome = await harness.PublisherAsync(p => p.ReplaceAsync(new CardDavAddress("customers", "all"),
            [CardDavTestData.Person("anna", "Anna", "Berger"), Group("team", "Team", [ContactMember.FromUid("anna")])], Ct).AsTask());

        outcome.Should().Be(new DavReplaceOutcome(2, 0, 0));
    }
}
