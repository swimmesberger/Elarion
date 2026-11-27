using System.Net;
using AwesomeAssertions;
using Elarion.Tests.CalDav;
using Elarion.Tests.WebDav;
using Elarion.WebDav;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elarion.Tests.CardDav;

/// <summary>
/// The discovery and sync sequences of iOS, macOS Contacts, Thunderbird, DAVx5 and Evolution, transcribed as request
/// lists from the research note "CardDAV clients", each asserted against a host that serves calendars and address books:
/// the status codes, the properties each client reads, the version each asks for, the read-only privilege set. "Add an
/// account in a contacts app" (spec 0007, P1).
/// </summary>
public sealed class CardDavClientReplayTests {
    private const string Ns = CardDavTestHarness.Namespaces;
    private const string Customers = "/dav/addressbooks/alice/customers.all/";

    private static async Task<(CalDavHost Host, string Password)> StartAsync() {
        var host = await CardDavHost.StartAsync();
        host.App.Services.GetRequiredService<CardDavTestDirectory>().Teams.Clear(); // one address book per account, as Apple needs
        var password = await host.CreatePasswordAsync("alice");
        await host.PublishContactAsync("customers", "all", CardDavTestData.Person("c-1", "Anna", "Berger", "anna@example.com", "+43 1 234"));
        await host.PublishContactAsync("customers", "all", CardDavTestData.Person("c-2", "Bernd", "Huber", "bernd@example.org"));
        return (host, password);
    }

    private static string PropFind(params string[] props) => $"<D:propfind {Ns}><D:prop>{string.Concat(props.Select(static p => "<" + p + "/>"))}</D:prop></D:propfind>";

    private static IEnumerable<string> Privileges(DavTestResponse response, string href) =>
        response.Prop(href, DavNames.D.CurrentUserPrivilegeSet)!.ChildrenNamed(DavNames.D.Privilege).SelectMany(static p => p.Children)
            .Select(static p => p.Name.LocalName);

    private static IEnumerable<string> Members(DavTestResponse response, string collection) =>
        response.Responses.Keys.Where(href => href.StartsWith(collection, StringComparison.Ordinal) && href != collection);

    // Every client discovers through the well-known URL: PROPFIND, redirect, PROPFIND current-user-principal.
    private static async Task<string> DiscoverPrincipalAsync(CalDavHost host, string user, string password) {
        var wellKnown = await host.SendAsync("PROPFIND", "/.well-known/carddav", PropFind("D:current-user-principal"), depth: "0");
        wellKnown.Status.Should().Be(HttpStatusCode.MovedPermanently);
        var context = wellKnown.Headers["Location"];
        (await host.SendAsync("PROPFIND", context, PropFind("D:current-user-principal"), depth: "0")).Status.Should().Be(HttpStatusCode.Unauthorized);
        var root = await host.SendAsync("PROPFIND", context, PropFind("D:current-user-principal"), user, password, "0");
        root.Status.Should().Be((HttpStatusCode)207);
        return root.Dav.Prop("/dav/", DavNames.D.CurrentUserPrincipal)!.Child(DavNames.D.Href)!.Text;
    }

    [Fact]
    public async Task IOS_AddsTheAccountFromHostNameEmailAndAppPassword_AndFetchesVCard30() {
        var (host, password) = await StartAsync();
        await using var _ = host;
        const string user = "alice@example.com";

        var principal = await DiscoverPrincipalAsync(host, user, password);
        (await host.SendAsync("OPTIONS", principal, user: user, password: password)).Headers["DAV"].Should().Contain("addressbook");
        var principalProps = await host.SendAsync("PROPFIND", principal, PropFind("CARD:addressbook-home-set", "D:principal-collection-set",
            "D:displayname", "D:supported-report-set", "CS:email-address-set", "CARD:directory-gateway"), user, password, "0");
        principalProps.Status.Should().Be((HttpStatusCode)207);
        principalProps.Dav.IsMissing(principal, new DavName(DavNames.CardDav, "directory-gateway")).Should().BeTrue();
        var home = principalProps.Dav.Prop(principal, DavNames.Card.AddressBookHomeSet)!.Child(DavNames.D.Href)!.Text;

        var homeProps = await host.SendAsync("PROPFIND", home, PropFind("D:resourcetype", "D:displayname", "D:current-user-privilege-set",
            "D:sync-token", "D:supported-report-set", "CS:me-card", "CS:max-image-size", "D:owner"), user, password, "1");
        homeProps.Dav.Prop(Customers, DavNames.D.ResourceType)!.Child(DavNames.Card.AddressBook).Should().NotBeNull();
        homeProps.Dav.IsMissing(Customers, new DavName(DavNames.CalendarServer, "me-card")).Should().BeTrue();
        Privileges(homeProps.Dav, Customers).Should().Equal("read", "read-current-user-privilege-set");

        var ctag = await host.SendAsync("PROPFIND", Customers, PropFind("CS:getctag", "D:sync-token"), user, password, "0");
        ctag.Dav.Prop(Customers, new DavName(DavNames.CalendarServer, "getctag"))!.Text.Should().NotBeEmpty();
        var sync = await host.SendAsync("REPORT", Customers, CardDavTestHarness.SyncCollection(null), user, password);
        var multiget = await host.SendAsync("REPORT", Customers,
            CardDavTestHarness.Multiget("<CARD:address-data content-type=\"text/vcard\" version=\"3.0\"/>", [.. Members(sync.Dav, Customers)]),
            user, password, "1");

        multiget.Status.Should().Be((HttpStatusCode)207);
        Members(multiget.Dav, Customers).Should().HaveCount(2);
        multiget.Dav.Prop(Customers + "c-1.vcf", DavNames.Card.AddressData)!.Text.Should().Contain("VERSION:3.0").And.Contain("FN:Anna Berger");
    }

    [Fact]
    public async Task MacOS_FindsTheAddressBookAndItsCTag_AndAWriteIsRefusedWith403NotA401() {
        var (host, password) = await StartAsync();
        await using var _ = host;
        const string user = "alice@example.com";

        var principal = await DiscoverPrincipalAsync(host, user, password);
        (await host.SendAsync("OPTIONS", principal, user: user, password: password)).Status.Should().Be(HttpStatusCode.OK);
        var home = (await host.SendAsync("PROPFIND", principal, PropFind("CARD:addressbook-home-set"), user, password, "0"))
            .Dav.Prop(principal, DavNames.Card.AddressBookHomeSet)!.Child(DavNames.D.Href)!.Text;
        var listing = await host.SendAsync("PROPFIND", home, PropFind("D:resourcetype", "CS:getctag", "D:displayname"), user, password, "1");
        var put = await host.SendAsync("PUT", Customers + "c-1.vcf", "BEGIN:VCARD\r\nVERSION:3.0\r\nFN:Edited\r\nUID:c-1\r\nEND:VCARD\r\n", user, password,
            headers: new Dictionary<string, string> { ["If-Match"] = "\"x\"" });

        Members(listing.Dav, home).Should().Equal(Customers);
        listing.Dav.Prop(Customers, new DavName(DavNames.CalendarServer, "getctag"))!.Text.Should().StartWith("urn:elarion:sync:");
        // A 401 would make Apple take the password for wrong and stop syncing the account.
        put.Status.Should().Be(HttpStatusCode.Forbidden);
        put.Dav.Xml.Child(DavNames.D.NeedPrivileges).Should().NotBeNull();
    }

    [Fact]
    public async Task Thunderbird_DiscoversListsReadOnlyAndSyncsWithBareAddressData() {
        var (host, password) = await StartAsync();
        await using var _ = host;

        var principal = await DiscoverPrincipalAsync(host, "alice", password);
        var home = (await host.SendAsync("PROPFIND", principal, PropFind("CARD:addressbook-home-set"), "alice", password, "0"))
            .Dav.Prop(principal, DavNames.Card.AddressBookHomeSet)!.Child(DavNames.D.Href)!.Text;
        var listing = await host.SendAsync("PROPFIND", home, PropFind("D:resourcetype", "D:displayname", "D:current-user-privilege-set"),
            "alice", password, "1");
        var book = await host.SendAsync("PROPFIND", Customers, PropFind("D:getetag", "CS:getctag", "D:sync-token", "CARD:supported-address-data"),
            "alice", password, "0");
        var sync = await host.SendAsync("REPORT", Customers, CardDavTestHarness.SyncCollection(null), "alice", password, "1");
        var multiget = await host.SendAsync("REPORT", Customers, CardDavTestHarness.Multiget("<CARD:address-data/>", [.. Members(sync.Dav, Customers)]),
            "alice", password, "1");

        // Thunderbird marks a book read-only when it may read and holds none of write, write-content, bind or all.
        Privileges(listing.Dav, Customers).Should().Contain("read").And.NotContain(["write", "write-content", "bind", "all"]);
        book.Dav.Prop(Customers, DavNames.Card.SupportedAddressData)!.Children.Select(static c => c.Attribute("version")).Should().Contain("4.0");
        multiget.Dav.Prop(Customers + "c-2.vcf", DavNames.Card.AddressData)!.Text.Should().Contain("VERSION:3.0");
    }

    [Fact]
    public async Task DAVx5_AcceptsThePrincipalByOptions_SyncsByToken_AsksForVCard40_AndSeesAWriteRefused() {
        var (host, password) = await StartAsync();
        await using var _ = host;

        var principal = await DiscoverPrincipalAsync(host, "alice", password);
        // DAVx5 accepts a principal for CardDAV only when OPTIONS on it names addressbook (DavResourceFinder.providesService).
        (await host.SendAsync("OPTIONS", principal, user: "alice", password: password)).Headers["DAV"].Should().Contain("addressbook");
        var principalProps = await host.SendAsync("PROPFIND", principal,
            PropFind("CARD:addressbook-home-set", "D:displayname").Replace("<D:prop>", "<D:prop><C:calendar-home-set xmlns:C=\"urn:ietf:params:xml:ns:caldav\"/>"),
            "alice", password, "0");
        principalProps.Dav.Prop(principal, DavNames.C.CalendarHomeSet).Should().NotBeNull();
        var home = principalProps.Dav.Prop(principal, DavNames.Card.AddressBookHomeSet)!.Child(DavNames.D.Href)!.Text;
        var listing = await host.SendAsync("PROPFIND", home,
            PropFind("D:resourcetype", "D:displayname", "CARD:addressbook-description", "D:current-user-privilege-set"), "alice", password, "1");
        var capabilities = await host.SendAsync("PROPFIND", Customers,
            PropFind("D:supported-report-set", "D:sync-token", "CS:getctag", "CARD:max-resource-size", "CARD:supported-address-data"), "alice", password, "0");
        var reports = capabilities.Dav.Prop(Customers, DavNames.D.SupportedReportSet)!.Children.Select(static r => r.Child(DavNames.D.Report)!.Children[0].Name.LocalName);
        var initial = await host.SendAsync("REPORT", Customers, CardDavTestHarness.SyncCollection(null), "alice", password);
        var multiget = await host.SendAsync("REPORT", Customers,
            CardDavTestHarness.Multiget("<CARD:address-data content-type=\"text/vcard\" version=\"4.0\"/>", [.. Members(initial.Dav, Customers)]),
            "alice", password);
        await host.PublishContactAsync("customers", "all", CardDavTestData.Person("c-3", "Clara", "Maier"));
        var delta = await host.SendAsync("REPORT", Customers, CardDavTestHarness.SyncCollection(initial.Dav.SyncToken), "alice", password);
        var delete = await host.SendAsync("DELETE", Customers + "c-1.vcf", user: "alice", password: password);

        listing.Dav.Prop(Customers, DavNames.Card.AddressBookDescription)!.Text.Should().Be("Every active customer");
        Privileges(listing.Dav, Customers).Should().NotContain("write-content");
        reports.Should().Contain("sync-collection");
        multiget.Dav.Prop(Customers + "c-1.vcf", DavNames.Card.AddressData)!.Text.Should().Contain("VERSION:4.0");
        Members(delta.Dav, Customers).Should().Equal(Customers + "c-3.vcf");
        delete.Status.Should().Be(HttpStatusCode.Forbidden);
        delete.Dav.Xml.Child(DavNames.D.NeedPrivileges).Should().NotBeNull();
    }

    [Fact]
    public async Task Evolution_DiscoversBothServices_SyncsByAUriToken_AndListsByAQueryWithoutFilter() {
        var (host, password) = await StartAsync();
        await using var _ = host;

        var discover = await host.SendAsync("PROPFIND", "/dav/", PropFind("D:resourcetype", "D:current-user-principal", "D:principal-URL",
            "CARD:addressbook-home-set").Replace("<D:prop>", "<D:prop><C:calendar-home-set xmlns:C=\"urn:ietf:params:xml:ns:caldav\"/>"),
            "alice", password, "0");
        var principal = discover.Dav.Prop("/dav/", DavNames.D.CurrentUserPrincipal)!.Child(DavNames.D.Href)!.Text;
        var home = (await host.SendAsync("PROPFIND", principal, PropFind("CARD:addressbook-home-set"), "alice", password, "0"))
            .Dav.Prop(principal, DavNames.Card.AddressBookHomeSet)!.Child(DavNames.D.Href)!.Text;
        var book = await host.SendAsync("PROPFIND", Customers, PropFind("CS:getctag", "CARD:supported-address-data", "D:supported-report-set",
            "D:current-user-privilege-set"), "alice", password, "0");
        var sync = await host.SendAsync("REPORT", Customers, CardDavTestHarness.SyncCollection(null), "alice", password);
        var query = await host.SendAsync("REPORT", Customers,
            CardDavTestHarness.Query(null, "<CARD:address-data><CARD:prop name=\"VERSION\"/><CARD:prop name=\"UID\"/></CARD:address-data>"),
            "alice", password, "0");

        home.Should().Be("/dav/addressbooks/alice/");
        // Evolution ignores a sync token without ':' or '/', taking it for a CTag.
        sync.Dav.SyncToken.Should().Contain(":");
        Privileges(book.Dav, Customers).Should().NotContain(["write", "write-content", "all"]);
        Members(query.Dav, Customers).Should().HaveCount(2);
        query.Dav.Prop(Customers + "c-1.vcf", DavNames.Card.AddressData)!.Text.Should().Contain("UID:c-1").And.NotContain("TEL");
    }
}
