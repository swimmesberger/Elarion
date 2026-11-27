using System.Net;
using AwesomeAssertions;
using Elarion.Calendars;
using Elarion.Tests.WebDav;
using Elarion.WebDav;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elarion.Tests.CalDav;

/// <summary>
/// The discovery and sync sequences of iOS, macOS, Thunderbird, DAVx5 and Evolution, transcribed as request lists from the
/// research note's sources (plan 0006, "Client replays"), each asserted against the host: the status codes, the properties
/// each client reads, the read-only privilege set. "Add an account in a calendar app" (spec 0006, P1).
/// </summary>
public sealed class CalDavClientReplayTests {
    private const string Ns = CalDavTestHarness.Namespaces;
    private const string Shifts = "/dav/calendars/alice/shifts.u-alice/";

    private static async Task<(CalDavHost Host, string Password)> StartAsync() {
        var host = await CalDavHost.StartAsync();
        var password = await host.CreatePasswordAsync("alice");
        await host.PublishAsync("shifts", "u-alice", CalDavTestData.Event("shift-1", new DateTime(2026, 11, 18, 6, 0, 0), TimeSpan.FromHours(8)));
        var series = CalDavTestData.Event("weekly", new DateTime(2025, 11, 3, 9, 0, 0), TimeSpan.FromHours(1), "Stand-up");
        series.RecurrenceRules.Add(new CalendarRecurrenceRule { Frequency = CalendarFrequency.Weekly });
        await host.PublishAsync("shifts", "u-alice", series);
        return (host, password);
    }

    private static string PropFind(params string[] props) => $"<D:propfind {Ns}><D:prop>{string.Concat(props.Select(static p => "<" + p + "/>"))}</D:prop></D:propfind>";

    private static IEnumerable<string> Privileges(DavTestResponse response, string href) =>
        response.Prop(href, DavNames.D.CurrentUserPrivilegeSet)!.ChildrenNamed(DavNames.D.Privilege).SelectMany(static p => p.Children)
            .Select(static p => p.Name.LocalName);

    [Fact]
    public async Task IOS_AddsTheAccountFromHostNameEmailAndAppPassword() {
        var (host, password) = await StartAsync();
        await using var _ = host;
        const string user = "alice@example.com"; // iOS sends the e-mail as the user name; the default scheme does not check it

        // iOS 26.6: PROPFIND well-known (anonymous) → redirect → PROPFIND context (anonymous → 401 challenge, then with
        // credentials) → current-user-principal → OPTIONS principal → PROPFIND principal → PROPFIND home.
        var wellKnown = await host.SendAsync("PROPFIND", "/.well-known/caldav", PropFind("D:current-user-principal"), depth: "0");
        wellKnown.Status.Should().Be(HttpStatusCode.MovedPermanently);
        var context = wellKnown.Headers["Location"];
        (await host.SendAsync("PROPFIND", context, PropFind("D:current-user-principal"), depth: "0")).Status.Should().Be(HttpStatusCode.Unauthorized);
        var root = await host.SendAsync("PROPFIND", context, PropFind("D:current-user-principal"), user, password, "0");
        var principal = root.Dav.Prop("/dav/", DavNames.D.CurrentUserPrincipal)!.Child(DavNames.D.Href)!.Text;
        principal.Should().Be("/dav/principals/alice/");
        (await host.SendAsync("OPTIONS", principal, user: user, password: password)).Headers["DAV"].Should().Contain("calendar-access");
        var principalProps = await host.SendAsync("PROPFIND", principal,
            PropFind("C:calendar-home-set", "C:calendar-user-address-set", "D:displayname", "D:principal-URL", "D:resourcetype"), user, password, "0");
        var home = principalProps.Dav.Prop(principal, DavNames.C.CalendarHomeSet)!.Child(DavNames.D.Href)!.Text;
        var homeProps = await host.SendAsync("PROPFIND", home,
            PropFind("D:resourcetype", "D:displayname", "CS:getctag", "ICAL:calendar-color", "C:supported-calendar-component-set",
                "D:current-user-privilege-set"), user, password, "1");

        homeProps.Status.Should().Be((HttpStatusCode)207);
        homeProps.Dav.Prop(Shifts, DavNames.D.ResourceType)!.Child(DavNames.C.Calendar).Should().NotBeNull();
        Privileges(homeProps.Dav, Shifts).Should().BeEquivalentTo("read", "read-current-user-privilege-set");
        Privileges(homeProps.Dav, home).Should().BeEquivalentTo("read", "read-current-user-privilege-set");

        // Historic iOS sync: ctag on the calendar → calendar-query with a time range for ETags → multiget.
        var query = await host.SendAsync("REPORT", Shifts, CalDavTestHarness.Query(CalDavTestHarness.EventsIn("20261101T000000Z", "20261201T000000Z"),
            "<D:getetag/>"), user, password, "1");
        query.Dav.Responses.Keys.Should().BeEquivalentTo(Shifts + "shift-1.ics", Shifts + "weekly.ics");
        var multiget = await host.SendAsync("REPORT", Shifts,
            $"<C:calendar-multiget {Ns}><D:prop><D:getetag/><C:calendar-data/></D:prop><D:href>{Shifts}shift-1.ics</D:href></C:calendar-multiget>",
            user, password, "1");
        multiget.Dav.Prop(Shifts + "shift-1.ics", DavNames.C.CalendarData)!.Text.Should().Contain("UID:shift-1");
    }

    [Fact]
    public async Task MacOS_DiscoversWithATrailingSlashedPrincipalAndReadsSyncTokenAndCTag() {
        var (host, password) = await StartAsync();
        await using var _ = host;

        var principal = await host.SendAsync("PROPFIND", "/dav/principals/alice/",
            PropFind("C:calendar-home-set", "D:principal-URL", "D:displayname", "C:calendar-user-address-set"), "alice", password, "0");
        principal.Dav.Responses.Keys.Should().Equal("/dav/principals/alice/");
        var home = await host.SendAsync("PROPFIND", "/dav/calendars/alice/",
            PropFind("D:sync-token", "CS:getctag", "D:resourcetype", "D:displayname", "C:calendar-timezone"), "alice", password, "1");

        home.Dav.Prop(Shifts, DavNames.D.SyncToken)!.Text.Should().StartWith("urn:elarion:sync:");
        home.Dav.Prop(Shifts, new DavName(DavNames.CalendarServer, "getctag"))!.Text.Should().Be(home.Dav.Prop(Shifts, DavNames.D.SyncToken)!.Text);
        home.Dav.Prop(Shifts, DavNames.C.CalendarTimezone)!.Text.Should().Contain("BEGIN:VTIMEZONE");
        home.Headers["DAV"].Should().Contain("calendar-access");
    }

    [Fact]
    public async Task Thunderbird_DetectsTheCalendarAndSyncsWithSyncCollection() {
        var (host, password) = await StartAsync();
        await using var _ = host;

        // Detection: PROPFIND well-known with credentials, following the redirect, then principal and home.
        var wellKnown = await host.SendAsync("PROPFIND", "/.well-known/caldav",
            PropFind("D:resourcetype", "D:current-user-principal", "C:calendar-home-set"), "alice", password, "0");
        var root = await host.SendAsync("PROPFIND", wellKnown.Headers["Location"],
            PropFind("D:resourcetype", "D:current-user-principal", "C:calendar-home-set"), "alice", password, "0");
        root.Status.Should().Be((HttpStatusCode)207);
        var calendars = await host.SendAsync("PROPFIND", "/dav/calendars/alice/",
            PropFind("D:resourcetype", "D:displayname", "ICAL:calendar-color", "C:supported-calendar-component-set", "D:current-user-privilege-set",
                "D:supported-report-set"), "alice", password, "1");
        calendars.Dav.Prop(Shifts, DavNames.D.SupportedReportSet)!.Body().Should().Contain("sync-collection");
        Privileges(calendars.Dav, Shifts).Should().NotContain(["write", "write-content", "bind", "all", "write-properties"]);
        // OPTIONS on the calendar's parent must answer 200.
        (await host.SendAsync("OPTIONS", "/dav/calendars/alice/", user: "alice", password: password)).Status.Should().Be(HttpStatusCode.OK);

        var sync = await host.SendAsync("REPORT", Shifts,
            $"<D:sync-collection {Ns}><D:sync-token/><D:sync-level>1</D:sync-level><D:prop><D:getcontenttype/><D:getetag/></D:prop></D:sync-collection>",
            "alice", password, "1");
        sync.Dav.Responses.Keys.Should().BeEquivalentTo(Shifts + "shift-1.ics", Shifts + "weekly.ics");
        sync.Dav.Prop(Shifts + "shift-1.ics", DavNames.D.GetContentType)!.Text.Should().StartWith("text/calendar");
        sync.Dav.Prop(Shifts + "shift-1.ics", DavNames.D.GetETag)!.Text.Should().MatchRegex("^\"[^\"]+\"$");
    }

    [Fact]
    public async Task DavX5_FindsCalendarAccessReadsTheCollectionAndSyncsInBatches() {
        var (host, password) = await StartAsync();
        await using var _ = host;

        // DAVx5 drops a principal whose OPTIONS lacks calendar-access, and asks for the e-mail without a mailto: address.
        (await host.SendAsync("OPTIONS", "/dav/principals/alice/", user: "alice", password: password)).Headers["DAV"].Should().Contain("calendar-access");
        var principal = await host.SendAsync("PROPFIND", "/dav/principals/alice/",
            PropFind("C:calendar-home-set", "C:calendar-user-address-set", "D:displayname"), "alice", password, "0");
        principal.Dav.Prop("/dav/principals/alice/", DavNames.C.CalendarUserAddressSet)!.Children.Select(static h => h.Text)
            .Should().Contain("mailto:alice@example.com");
        var collections = await host.SendAsync("PROPFIND", "/dav/calendars/alice/",
            PropFind("D:resourcetype", "D:displayname", "D:current-user-privilege-set", "D:supported-report-set", "C:calendar-timezone",
                "C:supported-calendar-component-set", "D:sync-token", "ICAL:calendar-color", "C:calendar-description"), "alice", password, "1");
        Privileges(collections.Dav, Shifts).Should().NotContain(["write-content", "unbind", "write", "all"]);

        var sync = await host.SendAsync("REPORT", Shifts,
            $"<D:sync-collection {Ns}><D:sync-token/><D:sync-level>1</D:sync-level><D:prop><D:getetag/></D:prop></D:sync-collection>",
            "alice", password, "1");
        var hrefs = sync.Dav.Responses.Keys.ToList();
        var multiget = await host.SendAsync("REPORT", Shifts,
            $"<C:calendar-multiget {Ns}><D:prop><D:getetag/><C:calendar-data/></D:prop>{string.Concat(hrefs.Select(static h => "<D:href>" + h + "</D:href>"))}</C:calendar-multiget>",
            "alice", password, "0");
        multiget.Dav.Responses.Keys.Should().BeEquivalentTo(hrefs);

        // An upload to the read-only calendar is refused with need-privileges, which makes DAVx5 discard the local change.
        var put = await host.SendAsync("PUT", Shifts + "local.ics", "BEGIN:VCALENDAR\r\nEND:VCALENDAR\r\n", "alice", password,
            headers: new Dictionary<string, string> { ["If-None-Match"] = "*" });
        put.Status.Should().Be(HttpStatusCode.Forbidden);
        put.Dav.Xml.Child(DavNames.D.NeedPrivileges).Should().NotBeNull();

        var next = await host.SendAsync("REPORT", Shifts,
            $"<D:sync-collection {Ns}><D:sync-token>{sync.Dav.SyncToken}</D:sync-token><D:sync-level>1</D:sync-level><D:prop><D:getetag/></D:prop></D:sync-collection>",
            "alice", password, "1");
        next.Dav.Responses.Should().BeEmpty();
    }

    [Fact]
    public async Task Evolution_ReadsCTagThenSyncsAndAsksForFreeBusy() {
        var (host, password) = await StartAsync();
        await using var _ = host;

        var discover = await host.SendAsync("PROPFIND", "/dav/", PropFind("D:current-user-principal", "C:calendar-home-set"), "alice", password, "0");
        discover.Dav.Prop("/dav/", DavNames.D.CurrentUserPrincipal).Should().NotBeNull();
        var options = await host.SendAsync("OPTIONS", Shifts, user: "alice", password: password);
        options.Headers["Allow"].Should().NotContain("PUT").And.NotContain("DELETE").And.NotContain("POST");
        var ctag = await host.SendAsync("PROPFIND", Shifts, PropFind("CS:getctag", "D:sync-token", "D:supported-report-set"), "alice", password, "0");
        var token = ctag.Dav.Prop(Shifts, DavNames.D.SyncToken)!.Text;
        token.Should().Contain(":", "Evolution reads a token without ':' or '/' as a CTag");

        var sync = await host.SendAsync("REPORT", Shifts,
            $"<D:sync-collection {Ns}><D:sync-token>{token}</D:sync-token><D:sync-level>1</D:sync-level><D:prop><D:getetag/></D:prop></D:sync-collection>",
            "alice", password, "1");
        sync.Dav.Responses.Should().BeEmpty();
        var freeBusy = await host.SendAsync("REPORT", Shifts,
            $"<C:free-busy-query {Ns}><C:time-range start=\"20261118T000000Z\" end=\"20261119T000000Z\"/></C:free-busy-query>", "alice", password, "1");
        freeBusy.Status.Should().Be(HttpStatusCode.OK);
        freeBusy.Dav.Body.Should().Contain("BEGIN:VFREEBUSY").And.Contain("FREEBUSY;FBTYPE=BUSY:20261118T050000Z/20261118T130000Z");
    }

    [Fact]
    public async Task AnExpiredToken_Answers403ValidSyncToken_AndTheFullResyncEndsWithTheSameObjects() {
        await using var host = await CalDavHost.StartAsync(services => services.Configure<WebDavOptions>(o => o.MaxJournalRowsPerCollection = 1));
        var password = await host.CreatePasswordAsync("alice");
        await host.PublishAsync("shifts", "u-alice", CalDavTestData.Event("a", new DateTime(2026, 12, 1, 6, 0, 0), TimeSpan.FromHours(1)));
        var first = await host.SendAsync("REPORT", Shifts, DavTestHarness.SyncCollection(null), "alice", password, "1");
        await host.PublishAsync("shifts", "u-alice", CalDavTestData.Event("b", new DateTime(2026, 12, 2, 6, 0, 0), TimeSpan.FromHours(1)));
        await host.PublishAsync("shifts", "u-alice", CalDavTestData.Event("c", new DateTime(2026, 12, 3, 6, 0, 0), TimeSpan.FromHours(1)));

        var expired = await host.SendAsync("REPORT", Shifts, DavTestHarness.SyncCollection(first.Dav.SyncToken), "alice", password, "1");
        var full = await host.SendAsync("REPORT", Shifts, DavTestHarness.SyncCollection(null), "alice", password, "1");

        expired.Status.Should().Be(HttpStatusCode.Forbidden);
        expired.Dav.Xml.Child(DavNames.D.ValidSyncToken).Should().NotBeNull();
        full.Dav.Responses.Keys.Should().BeEquivalentTo(Shifts + "a.ics", Shifts + "b.ics", Shifts + "c.ics");
    }
}
