using AwesomeAssertions;
using Elarion.CardDav;
using Elarion.Tests.WebDav;
using Elarion.WebDav;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Elarion.Tests.CardDav;

/// <summary>"Decide who sees which address book in a handler" (spec 0007, P1) and FR-011–FR-015.</summary>
public sealed class CardDavSourceTests {
    private static readonly string HomeQuery = CardDavTestHarness.PropFind("D:displayname", "D:resourcetype", "CARD:addressbook-description",
        "CARD:supported-address-data", "D:current-user-privilege-set", "D:sync-token", "CS:getctag");

    private static Dictionary<string, string> Depth(string depth) => new() { ["Depth"] = depth };

    private static IEnumerable<string> AddressBooks(DavTestResponse home, string user) =>
        home.Responses.Keys.Where(href => href != $"/dav/addressbooks/{user}/");

    [Fact]
    public async Task GivenAUserWithoutThePermission_ThenTheHomeListsNoCustomerAddressBookAndItsUrlAnswers404() {
        await using var harness = CardDavTestHarness.Create();
        harness.Directory.CustomerReaders.Remove("carol");

        var home = await harness.SendAsync("PROPFIND", "/addressbooks/carol/", HomeQuery, user: "carol", headers: Depth("1"));
        var direct = await harness.SendAsync("PROPFIND", "/addressbooks/carol/customers.all/", HomeQuery, user: "carol", headers: Depth("0"));

        AddressBooks(home, "carol").Should().NotContain(href => href.Contains("customers.", StringComparison.Ordinal));
        direct.Status.Should().Be(404);
    }

    [Fact]
    public async Task GivenAUserWithThePermission_ThenTheHomeListsExactlyTheReturnedAddressBooksWithNameAndDescription() {
        await using var harness = CardDavTestHarness.Create();
        harness.Directory.Teams.Clear();
        harness.Directory.GateOpen = false;

        var home = await harness.SendAsync("PROPFIND", "/addressbooks/alice/", HomeQuery, user: "alice", headers: Depth("1"));

        const string href = "/dav/addressbooks/alice/customers.all/";
        AddressBooks(home, "alice").Should().Equal(href);
        home.Prop(href, DavNames.D.DisplayName)!.Text.Should().Be("Customers");
        home.Prop(href, DavNames.Card.AddressBookDescription)!.Text.Should().Be("Every active customer");
        home.Prop(href, DavNames.D.ResourceType)!.Child(DavNames.Card.AddressBook).Should().NotBeNull();
        home.Prop(href, DavNames.D.ResourceType)!.Child(DavNames.D.Collection).Should().NotBeNull();
        home.Prop(href, DavNames.Card.SupportedAddressData)!.Children
            .Select(static c => (c.Attribute("content-type"), c.Attribute("version"))).Should().Equal(("text/vcard", "3.0"), ("text/vcard", "4.0"));
        home.Prop(href, DavNames.D.SyncToken)!.Text.Should().StartWith("urn:elarion:sync:");
        home.Prop(href, new DavName(DavNames.CalendarServer, "getctag"))!.Text.Should().Be(home.Prop(href, DavNames.D.SyncToken)!.Text);
    }

    [Fact]
    public async Task GivenATeamAddressBook_WhenAMemberLeavesTheTeam_ThenOnTheNextSyncTheAddressBookIsGoneFromTheirHome() {
        await using var harness = CardDavTestHarness.Create();

        var before = await harness.SendAsync("PROPFIND", "/addressbooks/bob/", HomeQuery, user: "bob", headers: Depth("1"));
        harness.Directory.Teams["red"].Remove("bob");
        var after = await harness.SendAsync("PROPFIND", "/addressbooks/bob/", HomeQuery, user: "bob", headers: Depth("1"));

        AddressBooks(before, "bob").Should().Contain("/dav/addressbooks/bob/teams.red/");
        AddressBooks(after, "bob").Should().NotContain("/dav/addressbooks/bob/teams.red/");
        (await harness.SendAsync("REPORT", "/addressbooks/bob/teams.red/", CardDavTestHarness.SyncCollection(null), user: "bob")).Status.Should().Be(404);
    }

    [Fact]
    public async Task GivenTheSourcesModuleDisabled_ThenNoAddressBookOfTheSourceIsListedOrServed() {
        await using var harness = CardDavTestHarness.Create(registerGated: false);

        var home = await harness.SendAsync("PROPFIND", "/addressbooks/alice/", HomeQuery, headers: Depth("1"));

        AddressBooks(home, "alice").Should().NotContain(href => href.Contains("gated.", StringComparison.Ordinal));
        (await harness.SendAsync("PROPFIND", "/addressbooks/alice/gated.all/", HomeQuery, headers: Depth("0"))).Status.Should().Be(404);
    }

    [Fact]
    public async Task GivenTheSourcesFeatureGateClosed_ThenNoAddressBookOfTheSourceIsListedOrServed() {
        await using var harness = CardDavTestHarness.Create();
        (await harness.SendAsync("PROPFIND", "/addressbooks/alice/gated.all/", HomeQuery, headers: Depth("0"))).Status.Should().Be(207);

        harness.Directory.GateOpen = false;

        var home = await harness.SendAsync("PROPFIND", "/addressbooks/alice/", HomeQuery, headers: Depth("1"));
        AddressBooks(home, "alice").Should().NotContain(href => href.Contains("gated.", StringComparison.Ordinal));
        (await harness.SendAsync("PROPFIND", "/addressbooks/alice/gated.all/", HomeQuery, headers: Depth("0"))).Status.Should().Be(404);
    }

    [Fact]
    public async Task GivenAnAddressBookHomeUrlWithAnotherUsersId_ThenTheAnswerIs404() {
        await using var harness = CardDavTestHarness.Create();

        (await harness.SendAsync("PROPFIND", "/addressbooks/bob/", HomeQuery, user: "alice", headers: Depth("1"))).Status.Should().Be(404);
        (await harness.SendAsync("PROPFIND", "/addressbooks/bob/customers.all/", HomeQuery, user: "alice", headers: Depth("0"))).Status.Should().Be(404);
        (await harness.SendAsync("GET", "/addressbooks/bob/customers.all/x.vcf", user: "alice")).Status.Should().Be(404);
    }

    [Fact]
    public async Task ThePrincipal_AnswersTheAddressBookHomeAndOptionsNamesAddressbook() {
        await using var harness = CardDavTestHarness.Create();

        var principal = await harness.SendAsync("PROPFIND", "/principals/alice/",
            CardDavTestHarness.PropFind("CARD:addressbook-home-set", "D:current-user-principal", "D:principal-URL"), headers: Depth("0"));

        principal.Prop("/dav/principals/alice/", DavNames.Card.AddressBookHomeSet)!.Child(DavNames.D.Href)!.Text.Should().Be("/dav/addressbooks/alice/");
        (await harness.SendAsync("OPTIONS", "/principals/alice/")).Headers["DAV"].Should().Be("1, 3, addressbook");
    }

    [Fact]
    public async Task ASourceReturningAnInvalidOrDuplicateKey_FailsNamingTheSource() {
        await using var harness = CardDavTestHarness.Create();
        harness.Directory.Override = [new PublishedAddressBookInfo("bad key", "Bad")];

        await FluentActions.Awaiting(() => harness.SendAsync("PROPFIND", "/addressbooks/alice/", HomeQuery, headers: Depth("1")))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*address-book source 'customers'*");
        harness.Directory.Override = [new PublishedAddressBookInfo("k", "A"), new PublishedAddressBookInfo("k", "B")];
        await FluentActions.Awaiting(() => harness.SendAsync("PROPFIND", "/addressbooks/alice/", HomeQuery, headers: Depth("1")))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*twice*");
    }

    [Fact]
    public void TwoSourcesWithOneName_FailAtStartup() {
        var services = new ServiceCollection();
        services.AddElarionCardDavSource<CustomerAddressBooks>();

        FluentActions.Invoking(() => services.AddElarionCardDavSource<CustomersAgain>()).Should().Throw<InvalidOperationException>()
            .WithMessage("*address-book sources are named 'customers'*");
        FluentActions.Invoking(() => services.AddElarionCardDavSource<BadName>()).Should().Throw<InvalidOperationException>().WithMessage("*BadName*");
    }

    [Fact]
    public void AddressesWithInvalidSegments_AreRefused() {
        FluentActions.Invoking(() => new CardDavAddress("a.b", "k")).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => new CardDavAddress("s", new string('k', 65))).Should().Throw<ArgumentException>();
        new CardDavAddress("customers", "u-1_A").ToString().Should().Be("customers.u-1_A");
    }

    [Fact]
    public async Task GivenAnAddressBookSourceRegisteredWithoutAHandler_WhenTheHostStarts_ThenTheStartFailsNamingTheSource() {
        await using var harness = CardDavTestHarness.Create(services => services.AddElarionCardDavSource<OrphanAddressBooks>());

        var validate = () => _ = harness.Services.GetRequiredService<IOptions<CardDavOptions>>().Value;

        validate.Should().Throw<OptionsValidationException>().Which.Failures.Should().ContainSingle()
            .Which.Should().Contain("address-book source 'orphans'").And.Contain("IHandler<OrphanAddressBooks, Result<CardDavSourceListing>>")
            .And.Contain("AddElarionCardDavSource");
    }

    [Fact]
    public async Task SourcesWithTheirHandlers_PassTheStartCheck() {
        await using var harness = CardDavTestHarness.Create();

        var validate = () => _ = harness.Services.GetRequiredService<IOptions<CardDavOptions>>().Value;

        validate.Should().NotThrow();
    }

    public sealed record CustomersAgain : ICardDavSource<CustomersAgain> {
        public static string SourceName => "customers";
    }

    public sealed record BadName : ICardDavSource<BadName> {
        public static string SourceName => "has space";
    }
}

/// <summary>A source whose handler the application forgot.</summary>
public sealed record OrphanAddressBooks : ICardDavSource<OrphanAddressBooks> {
    public static string SourceName => "orphans";
}
