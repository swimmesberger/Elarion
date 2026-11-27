using System.Net;
using System.Net.Http.Headers;
using System.Text;
using AwesomeAssertions;
using Elarion.CalDav;
using Elarion.Calendars;
using Xunit;

namespace Elarion.Tests.CalDav;

/// <summary>
/// The client against handwritten answers of real servers, ported from other projects' client suites: python-caldav (P),
/// caldav-server-tester (T), go-webdav (G), dav4jvm (D), vdirsyncer (V), the per-server flags of python-caldav's
/// <c>compatibility_hints.py</c> (named by flag) and sabre/dav's client tests. Case ids are those of the case catalogues
/// (RP response parsing, DI discovery, SY sync, MG multiget, WR writes, CT content).
/// </summary>
public sealed class CalDavClientConformanceTests {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Ns = "xmlns:D=\"DAV:\" xmlns:C=\"urn:ietf:params:xml:ns:caldav\" xmlns:CS=\"http://calendarserver.org/ns/\" xmlns:ICAL=\"http://apple.com/ns/ical/\"";
    private const string Cal = "https://dav.example.com/cal/";

    private static string Ics(string uid, string summary, string newline = "\r\n") =>
        string.Join(newline, "BEGIN:VCALENDAR", "VERSION:2.0", "PRODID:-//Test//EN", "BEGIN:VEVENT", $"UID:{uid}", "DTSTAMP:20261101T000000Z",
            "DTSTART:20261201T090000Z", "DTEND:20261201T100000Z", $"SUMMARY:{summary}", "END:VEVENT", "END:VCALENDAR", "");

    private static string Multistatus(params string[] responses) => $"<?xml version=\"1.0\" encoding=\"utf-8\"?><D:multistatus {Ns}>{string.Concat(responses)}</D:multistatus>";

    private static string Response(string href, string props, string status = "HTTP/1.1 200 OK") =>
        $"<D:response><D:href>{href}</D:href><D:propstat><D:prop>{props}</D:prop><D:status>{status}</D:status></D:propstat></D:response>";

    private static string Gone(string href, string status = "HTTP/1.1 404 Not Found") => $"<D:response><D:href>{href}</D:href><D:status>{status}</D:status></D:response>";

    private static string Token(string token) => $"<D:sync-token>{token}</D:sync-token>";

    private static string ETag(string etag) => $"<D:getetag>\"{etag}\"</D:getetag>";

    private static string CalendarData(string ics) => "<C:calendar-data>" + ics.Replace("&", "&amp;").Replace("<", "&lt;") + "</C:calendar-data>";

    private static string Item(string href, string etag, string summary) => Response(href, ETag(etag) + CalendarData(Ics(NameOf(href), summary)));

    private static string NameOf(string href) => href[(href.LastIndexOf('/') + 1)..].Replace(".ics", "", StringComparison.Ordinal);

    private static string CalendarProps(string name) =>
        $"<D:resourcetype><D:collection/><C:calendar/></D:resourcetype><D:displayname>{name}</D:displayname>";

    private static CalDavClient Client(FakeDavServer server, CalDavClientOptions? options = null) =>
        new(new HttpClient(server), options ?? new CalDavClientOptions {
            Authorization = _ => ValueTask.FromResult<AuthenticationHeaderValue?>(new AuthenticationHeaderValue("Basic", "dXNlcjpwYXNz")),
        });

    private static CalDavCalendar Calendar(string url = Cal, bool sync = true) => new() { Url = new Uri(url), SupportsSyncCollection = sync };

    private static string State(string? token, Dictionary<string, string> etags) => new CalDavSyncState { Token = token, ETags = etags }.Serialize();

    private static bool IsSync(Incoming request) => request.Body.Contains("sync-collection", StringComparison.Ordinal);

    private static CalendarEvent Event(string uid) =>
        new() { Uid = uid, Start = CalendarDateTime.Utc(new DateTimeOffset(2026, 12, 1, 9, 0, 0, TimeSpan.Zero)), Summary = "A" };

    // ---- Response parsing ----------------------------------------------------------------------------------------------

    /// <summary>RP-18 (D `propfind propstat without status assumes 200 OK`), RP-19 (V `test_xml_utilities`): status is required (RFC 4918 §14.22) but broken servers omit it.</summary>
    [Fact]
    public async Task APropstatWithoutStatus_IsReadAs200_WhenListingCalendars() {
        var server = new FakeDavServer();
        server.On("PROPFIND", "https://dav.example.com/home/", _ => Reply.Xml(207, Multistatus(
            $"<D:response><D:href>/home/work/</D:href><D:propstat><D:prop>{CalendarProps("Work")}</D:prop></D:propstat></D:response>")));

        var calendars = await Client(server).ListCalendarsAsync(new Uri("https://dav.example.com/home/"), Ct);

        calendars.HasValue.Should().BeTrue(calendars.Error?.ToString());
        calendars.Value!.Should().ContainSingle().Which.DisplayName.Should().Be("Work");
    }

    /// <summary>RP-18, RP-19 in a sync answer: a member whose propstat has no status must not be read as removed.</summary>
    [Fact]
    public async Task AMemberWhosePropstatHasNoStatus_IsAChange_NotARemoval() {
        var server = new FakeDavServer();
        server.On("REPORT", Cal, request => IsSync(request)
            ? Reply.Xml(207, Multistatus($"<D:response><D:href>/cal/a.ics</D:href><D:propstat><D:prop>{ETag("2")}</D:prop></D:propstat></D:response>", Token("t2")))
            : Reply.Xml(207, Multistatus(Item("/cal/a.ics", "2", "A2"))));

        var step = await Client(server).SyncAsync(Calendar(), State("t1", new() { ["a.ics"] = "\"1\"" }), Ct);

        step.HasValue.Should().BeTrue(step.Error?.ToString());
        step.Value!.Removed.Should().BeEmpty();
        step.Value.Changed.Select(static o => o.Name).Should().Equal("a.ics");
    }

    /// <summary>
    /// RP-08 (P `testPathWithEscapedCharacters`: status before prop, extra xmlns on response), RP-19, RP-20 (D duplicate property
    /// in a 200 and a 404 propstat: the 200 value wins), sabre W19 (an empty 200 prop next to a 404 propstat), RP-06 (P: a
    /// schedule inbox listed beside the calendars, its displayname in a 404 propstat), RP-13 (Bedework: a status without reason phrase).
    /// </summary>
    [Fact]
    public async Task PropstatOrderAndContradictoryPropstats_DoNotChangeWhatIsRead() {
        var server = new FakeDavServer();
        server.On("PROPFIND", "https://dav.example.com/home/", _ => Reply.Xml(207, Multistatus(
            Response("/home/", "<D:resourcetype><D:collection/></D:resourcetype>"),
            "<D:response xmlns:X=\"urn:unused\"><D:href>/home/work/</D:href>"
            + $"<D:propstat><D:status>HTTP/1.1 200</D:status><D:prop>{CalendarProps("Work")}</D:prop></D:propstat>"
            + "<D:propstat><D:prop>   </D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat>"
            + "<D:propstat><D:prop><D:resourcetype/><D:displayname/><ICAL:calendar-color/></D:prop><D:status>HTTP/1.1 404 Not Found</D:status></D:propstat>"
            + "</D:response>",
            Response("/home/inbox/", "<D:resourcetype><D:collection/><C:schedule-inbox/></D:resourcetype>"))));

        var calendars = await Client(server).ListCalendarsAsync(new Uri("https://dav.example.com/home/"), Ct);

        var work = calendars.Value!.Should().ContainSingle().Which;
        work.Url.Should().Be(new Uri("https://dav.example.com/home/work/"));
        work.DisplayName.Should().Be("Work");
        work.Color.Should().BeNull();
    }

    /// <summary>
    /// Namespaces are matched by URI, never by prefix: a default <c>DAV:</c> namespace, lower-case and unusual prefixes, and a
    /// prefix redeclared on an inner element (P, D and V all parse with a namespace-aware reader).
    /// </summary>
    [Fact]
    public async Task DefaultNamespacesAndUnusualPrefixes_AreReadByNamespaceUri() {
        var server = new FakeDavServer();
        server.On("PROPFIND", "https://dav.example.com/home/", _ => Reply.Xml(207,
            "<multistatus xmlns=\"DAV:\" xmlns:cal=\"urn:ietf:params:xml:ns:caldav\"><response><href>/home/work/</href><propstat><prop>"
            + "<resourcetype><collection/><cal:calendar/></resourcetype><displayname>Work</displayname>"
            + "<x1:calendar-color xmlns:x1=\"http://apple.com/ns/ical/\">#112233</x1:calendar-color>"
            + "<C:supported-calendar-component-set xmlns:C=\"urn:ietf:params:xml:ns:caldav\"><C:comp name=\"VTODO\"/></C:supported-calendar-component-set>"
            + "<d:supported-report-set xmlns:d=\"DAV:\"><d:supported-report><d:report><d:sync-collection/></d:report></d:supported-report></d:supported-report-set>"
            + "</prop><status>HTTP/1.1 200 OK</status></propstat></response></multistatus>"));

        var work = (await Client(server).ListCalendarsAsync(new Uri("https://dav.example.com/home/"), Ct)).Value!.Single();

        work.DisplayName.Should().Be("Work");
        work.Color.Should().Be("#112233");
        work.Components.Should().Be(CalendarComponentKinds.Todo);
        work.SupportsSyncCollection.Should().BeTrue();
    }

    /// <summary>
    /// RP-07 (P `testAbsoluteURL` finds a multistatus wrapped in another root) against RP-34 (D `no multistatus root` is an
    /// error): the projects disagree; Elarion follows RFC 4918 §13 and dav4jvm and reads a 207 whose root is not
    /// <c>DAV:multistatus</c> as an invalid response. This test pins that choice.
    /// </summary>
    [Fact]
    public async Task A207WhoseRootIsNotMultistatus_IsAnInvalidResponse() {
        var server = new FakeDavServer();
        server.On("PROPFIND", "https://dav.example.com/home/", _ => Reply.Xml(207,
            $"<xml><D:multistatus {Ns}>{Response("/home/work/", CalendarProps("Work"))}</D:multistatus></xml>"));

        var calendars = await Client(server).ListCalendarsAsync(new Uri("https://dav.example.com/home/"), Ct);

        calendars.Error!.Code.Should().Be(CalDavErrorCodes.InvalidResponse);
    }

    /// <summary>RP-32 (P `testEmptyXMLNoContentLength`), RP-33 (D `assertMultiStatus` with an empty body): an empty 207 is a multistatus without responses.</summary>
    [Fact]
    public async Task AnEmpty207_ListsNoCalendars() {
        var server = new FakeDavServer();
        server.On("PROPFIND", "https://dav.example.com/home/", _ => Reply.Xml(207, ""));

        var calendars = await Client(server).ListCalendarsAsync(new Uri("https://dav.example.com/home/"), Ct);

        calendars.HasValue.Should().BeTrue(calendars.Error?.ToString());
        calendars.Value!.Should().BeEmpty();
    }

    /// <summary>
    /// RP-33 (D: a 207 sent as <c>application/octet-stream</c> with an XML body is accepted by sniffing), CT-20 (P, V: UTF-8
    /// everywhere): the media type of a 207 is not checked, and a <c>text/xml</c> without charset is read as the XML
    /// declaration says (UTF-8 by default).
    /// </summary>
    [Theory]
    [InlineData("text/xml")]
    [InlineData("application/xml")]
    [InlineData("application/octet-stream")]
    [InlineData(null)]
    public async Task TheMediaTypeOfA207_IsNotChecked_AndTheBodyIsUtf8(string? contentType) {
        var server = new FakeDavServer();
        server.On("PROPFIND", "https://dav.example.com/home/", _ => new Reply(207,
            Encoding.UTF8.GetBytes($"<D:multistatus {Ns}>{Response("/home/at/", CalendarProps("Österreich 中国"))}</D:multistatus>"), contentType));

        var calendars = await Client(server).ListCalendarsAsync(new Uri("https://dav.example.com/home/"), Ct);

        calendars.Value!.Single().DisplayName.Should().Be("Österreich 中国");
    }

    /// <summary>RP-09 (P `testPropfindResponseLevelNotFound`), RP-10 (P `...OnlyWhenAllAreMissing`): a response-level 404 among the children of a listing skips that child only.</summary>
    [Fact]
    public async Task AResponseLevel404InADepth1Listing_SkipsThatChildOnly() {
        var server = new FakeDavServer();
        server.On("PROPFIND", "https://dav.example.com/home/", _ => Reply.Xml(207, Multistatus(
            Response("/home/", "<D:resourcetype><D:collection/></D:resourcetype>"),
            Gone("/home/deleted/"),
            Gone("/home/deleted-too/", "HTTP/1.1 404"),
            Response("/home/work/", CalendarProps("Work")))));

        var calendars = await Client(server).ListCalendarsAsync(new Uri("https://dav.example.com/home/"), Ct);

        calendars.Value!.Select(static c => c.DisplayName).Should().Equal("Work");
    }

    /// <summary>
    /// DI-13 (P `test_get_supported_components_absent`), flag <c>supported-components.property-exists</c> (Robur,
    /// Purelymail), flag <c>propfind.displayname</c>, RP-12 (a 404 propstat is "not set"): missing properties are
    /// <see langword="null"/>. Without <c>supported-calendar-component-set</c> RFC 4791 §5.2.3 means "all components";
    /// Elarion documents events and to-dos for that case (<see cref="CalDavCalendar.Components"/>), and this test pins it.
    /// </summary>
    [Fact]
    public async Task MissingNameColorDescriptionAndComponentSet_AreNullAndTheDocumentedDefault() {
        var server = new FakeDavServer();
        server.On("PROPFIND", "https://dav.example.com/home/", _ => Reply.Xml(207, Multistatus(
            "<D:response><D:href>/home/robur/</D:href>"
            + "<D:propstat><D:prop><D:resourcetype><D:collection/><C:calendar/></D:resourcetype></D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat>"
            + "<D:propstat><D:prop><D:displayname/><ICAL:calendar-color/><C:calendar-description/><C:supported-calendar-component-set/>"
            + "<D:current-user-privilege-set/><D:supported-report-set/></D:prop><D:status>HTTP/1.1 404 Not Found</D:status></D:propstat></D:response>",
            Response("/home/empty-name/", "<D:resourcetype><D:collection/><C:calendar/></D:resourcetype><D:displayname/><ICAL:calendar-color>  </ICAL:calendar-color>"))));

        var calendars = (await Client(server).ListCalendarsAsync(new Uri("https://dav.example.com/home/"), Ct)).Value!;

        calendars.Should().HaveCount(2).And.AllSatisfy(c => {
            c.DisplayName.Should().BeNull();
            c.Color.Should().BeNull();
            c.Description.Should().BeNull();
            c.Components.Should().Be(CalendarComponentKinds.Event | CalendarComponentKinds.Todo);
            c.IsWritable.Should().BeTrue();
            c.SupportsSyncCollection.Should().BeFalse();
        });
    }

    // ---- Hrefs ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// RP-07 (absolute hrefs), RP-21 (D: relative hrefs resolve against the request URL), SY-04 (D: whitespace around the
    /// href, the token after the responses, an unknown property in a 404 propstat), RP-23 (D, V: a response without href is
    /// skipped), SY-25/SY-26 (members listed without <c>getetag</c> are still fetched).
    /// </summary>
    [Fact]
    public async Task AbsoluteRelativeAndPathHrefs_AllNameTheirMember() {
        var server = new FakeDavServer();
        server.On("REPORT", Cal, request => IsSync(request)
            ? Reply.Xml(207, Multistatus(
                Response("https://dav.example.com/cal/a.ics", ""),
                Response("/cal/b.ics", ""),
                Response("c.ics", "") + "<D:response><D:propstat><D:prop>" + ETag("9") + "</D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response>",
                "<D:response><D:href>\n   /cal/d.ics\n  </D:href><D:propstat><D:prop><X:unknown xmlns:X=\"urn:x\"/></D:prop>"
                + "<D:status>HTTP/1.1 404 Not Found</D:status></D:propstat><D:propstat><D:prop/><D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response>",
                Token("t1")))
            : Reply.Xml(207, Multistatus(Item("https://dav.example.com/cal/a.ics", "1", "A"), Item("/cal/b.ics", "1", "B"), Item("c.ics", "1", "C"),
                Item("/cal/d.ics", "1", "D"))));

        var step = await Client(server).SyncAsync(Calendar(), null, Ct);

        step.HasValue.Should().BeTrue(step.Error?.ToString());
        step.Value!.Changed.Select(static o => o.Name).Should().BeEquivalentTo("a.ics", "b.ics", "c.ics", "d.ics");
        step.Value.Changed.Should().AllSatisfy(o => o.ETag.Should().Be("\"1\""));
        server.Requests.Single(r => !r.Body.Contains("sync-collection")).Body.Should().Contain("<D:href>/cal/c.ics</D:href>");
    }

    /// <summary>
    /// RP-21 (D `propfind collection with multiple members`: the relative href <c>relative:colon.vcf</c> is a path, not a
    /// scheme), CT-23 (G: <c>urn:uuid:</c> UIDs as resource names).
    /// </summary>
    [Fact]
    public async Task ARelativeHrefWithAColon_IsAPathSegment_NotAScheme() {
        var server = new FakeDavServer();
        server.On("REPORT", Cal, request => IsSync(request)
            ? Reply.Xml(207, Multistatus(Response("urn:uuid:4fbe8971.ics", ETag("1")), Token("t1")))
            : Reply.Xml(207, Multistatus(Item("/cal/urn:uuid:4fbe8971.ics", "1", "U"))));

        var step = await Client(server).SyncAsync(Calendar(), null, Ct);

        step.Value!.Changed.Select(static o => o.Name).Should().Equal("urn:uuid:4fbe8971.ics");
    }

    /// <summary>
    /// RP-05 (P: <c>%40</c> decoded once for identity), RP-27 (V `test_normalize_href`: <c>%253A</c> stays double-encoded),
    /// RP-28 (P `test_an_absolute_href_is_decoded_once`: a <c>/</c> stored as <c>%2F</c> listed as <c>%252F</c>): names are
    /// decoded exactly once, and the multiget sends the spelling the server listed.
    /// </summary>
    [Fact]
    public async Task PercentEncodedHrefs_AreDecodedOnce_AndFetchedAsListed() {
        string[] hrefs = ["/cal/20010712T182145Z-123401%40example.com.ics", "/cal/UID%253A20210609T084907Z%250A.ics", "/cal/a%252Fb.ics"];
        var server = new FakeDavServer();
        server.On("REPORT", Cal, request => IsSync(request)
            ? Reply.Xml(207, Multistatus([.. hrefs.Select(static h => Response(h, ETag("1"))), Token("t1")]))
            : Reply.Xml(207, Multistatus([.. hrefs.Select(static h => Response(h, ETag("1") + CalendarData(Ics("x", "X"))))])));

        var step = await Client(server).SyncAsync(Calendar(), null, Ct);

        step.Value!.Changed.Select(static o => o.Name).Should().BeEquivalentTo("20010712T182145Z-123401@example.com.ics", "UID%3A20210609T084907Z%0A.ics", "a%2Fb.ics");
        var multiget = server.Requests.Single(r => r.Body.Contains("calendar-multiget")).Body;
        hrefs.Should().AllSatisfy(h => multiget.Should().Contain($"<D:href>{h}</D:href>"));
    }

    /// <summary>
    /// RP-24 (D `resolveHref keeps percent-encoding`: hrefs are opaque, Stalwart only accepts the form it returned), MG-06
    /// (T `test_robur_takes_a_percent_40_href_as_it_is`: Robur answers 404 to a multiget href with <c>%40</c> for an object it
    /// lists with a literal <c>@</c>), flags <c>url.encode-at.encoded.href</c> (Robur) and <c>url.encode-at.identity</c>
    /// (Stalwart 0.16): the client must send an href back with the spelling the server listed.
    /// </summary>
    [Fact]
    public async Task AnHrefListedWithALiteralAt_IsFetchedWithTheSameSpelling() {
        var server = new FakeDavServer();
        server.On("REPORT", Cal, request => IsSync(request)
            ? Reply.Xml(207, Multistatus(Response("/cal/foo@example.com.ics", ETag("1")), Token("t1")))
            : request.Body.Contains("<D:href>/cal/foo@example.com.ics</D:href>", StringComparison.Ordinal)
                ? Reply.Xml(207, Multistatus(Response("/cal/foo@example.com.ics", ETag("1") + CalendarData(Ics("foo@example.com", "Foo")))))
                : Reply.Xml(207, Multistatus(Gone("/cal/foo@example.com.ics"))));

        var step = await Client(server).SyncAsync(Calendar(), State("t0", new() { ["foo@example.com.ics"] = "\"0\"" }), Ct);

        step.Value!.Removed.Should().BeEmpty();
        step.Value.Changed.Select(static o => o.Name).Should().Equal("foo@example.com.ics");
    }

    /// <summary>
    /// RP-25 (D `parseResponse member with differently encoded href`), RP-26 (D `equalsForWebDAV`: <c>@</c> and <c>%40</c> name
    /// the same URL), SY-10 (T: the 507 marker names the collection without its trailing slash): the truncation marker of a
    /// calendar whose URL holds <c>%40</c>, spelled by the server with a literal <c>@</c> (Google lists both spellings).
    /// </summary>
    [Fact]
    public async Task ATruncationMarkerSpellingTheCollectionDifferently_IsStillTheCollection() {
        const string calendar = "https://dav.example.com/cal/someone%40example.com/events/";
        var server = new FakeDavServer();
        server.On("REPORT", calendar, request => IsSync(request)
            ? Reply.Xml(207, Multistatus(Response("/cal/someone%40example.com/events/a.ics", ETag("1")),
                Gone("/cal/someone@example.com/events", "HTTP/1.1 507 Insufficient Storage"), Token("t1")))
            : Reply.Xml(207, Multistatus(Item("/cal/someone%40example.com/events/a.ics", "1", "A"))));

        var step = await Client(server).SyncAsync(Calendar(calendar), null, Ct);

        step.Value!.Changed.Select(static o => o.Name).Should().Equal("a.ics");
        step.Value.HasMore.Should().BeTrue();
        server.Requests.Single(r => r.Body.Contains("calendar-multiget")).Body.Should().NotContain("events</D:href>");
    }

    // ---- Sync ----------------------------------------------------------------------------------------------------------

    /// <summary>
    /// RP-03, RP-04 (P iCloud: the collection itself in a REPORT answer, with whitespace-only prop and a 404 calendar-data),
    /// SY-17 (T: an initial sync listing deleted members with 404, as Radicale, or the collection itself, as CCS and
    /// Stalwart), flags <c>sync-token.initial-sync</c> (Radicale, CCS, Stalwart) and <c>sync-token.limit</c> (iCloud lists the
    /// collection as a member).
    /// </summary>
    [Fact]
    public async Task TheCollectionItselfAndDeletedMembersInAnInitialSync_AreNeitherChangesNorRemovals() {
        var server = new FakeDavServer();
        server.On("REPORT", Cal, request => IsSync(request)
            ? Reply.Xml(207, Multistatus(Response("/cal/", "<D:getetag/>"), Response("/cal", "<D:resourcetype><D:collection/></D:resourcetype>"),
                Gone("/cal/deleted.ics"), Response("/cal/a.ics", ETag("1")), Token("t1")))
            : Reply.Xml(207, Multistatus(
                "<D:response><D:href>/cal/</D:href><D:propstat><D:prop>    </D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat>"
                + "<D:propstat><D:prop><C:calendar-data/></D:prop><D:status>HTTP/1.1 404 Not Found</D:status></D:propstat></D:response>",
                Item("/cal/a.ics", "1", "A"))));

        var step = await Client(server).SyncAsync(Calendar(), null, Ct);

        step.HasValue.Should().BeTrue(step.Error?.ToString());
        step.Value!.Changed.Select(static o => o.Name).Should().Equal("a.ics");
        step.Value.Removed.Should().BeEmpty();
        step.Value.HasMore.Should().BeFalse();
        server.Requests.Single(r => r.Body.Contains("calendar-multiget")).Body.Should().NotContain("<D:href>/cal/</D:href>");
    }

    /// <summary>
    /// RP-01 (P `test_xml_parsing`, issue 136: the same response twice), SY-15 (P: Cyrus lists a member as removed and again
    /// as changed), SY-16 (T `test_a_recreated_member_reported_twice`), flag <c>sync-token.recreated</c> (Stalwart reports
    /// removed, then changed): the same href twice in one answer, the last entry wins.
    /// </summary>
    [Fact]
    public async Task TheSameHrefTwiceInASyncAnswer_TheLastEntryWins() {
        var server = new FakeDavServer();
        server.On("REPORT", Cal, request => IsSync(request)
            ? Reply.Xml(207, Multistatus(Gone("/cal/a.ics"), Response("/cal/a.ics", ETag("2")), Response("/cal/b.ics", ETag("2")), Gone("/cal/b.ics"), Token("t2")))
            : Reply.Xml(207, Multistatus(Item("/cal/a.ics", "2", "A2"))));

        var step = await Client(server).SyncAsync(Calendar(), State("t1", new() { ["a.ics"] = "\"1\"", ["b.ics"] = "\"1\"" }), Ct);

        step.Value!.Changed.Select(static o => o.Name).Should().Equal("a.ics");
        step.Value.Removed.Should().Equal("b.ics");
    }

    /// <summary>
    /// MG-13 (P `TestRepeatedHrefCalendarData`: Bedework 5 answers one response per instance under the same href), flag
    /// <c>search.recurrences.expanded.event</c> (Bedework 5): the same href twice in a multiget answer is one object, the last wins.
    /// </summary>
    [Fact]
    public async Task TheSameHrefTwiceInAMultigetAnswer_IsOneObject_TheLastWins() {
        var server = new FakeDavServer();
        server.On("REPORT", Cal, request => IsSync(request)
            ? Reply.Xml(207, Multistatus(Response("/cal/a.ics", ETag("1")), Token("t1")))
            : Reply.Xml(207, Multistatus(Item("/cal/a.ics", "1", "First"), Item("/cal/a.ics", "1", "Last"))));

        var step = await Client(server).SyncAsync(Calendar(), null, Ct);

        step.Value!.Changed.Should().ContainSingle().Which.Data.Events.Single().Value!.Summary.Should().Be("Last");
    }

    /// <summary>
    /// RP-13 (P: Bedework sends <c>HTTP/1.1 404</c> without reason phrase), RP-15 (D `HttpUtilsTest`: HTTP/1.0, HTTP/2, bare
    /// codes and extra spaces), SY-14 (P `test_deleted_urls`): a removed member's status line in every shape servers send.
    /// </summary>
    [Theory]
    [InlineData("HTTP/1.1 404 Not Found")]
    [InlineData("HTTP/1.1 404")]
    [InlineData("HTTP/1.0 404 Not Found")]
    [InlineData("HTTP/2 404")]
    [InlineData("HTTP/1.1   404   Not Found")]
    [InlineData("404")]
    public async Task ARemovedMembersStatusLine_IsReadInEveryShape(string status) {
        var server = new FakeDavServer();
        server.On("REPORT", Cal, _ => Reply.Xml(207, Multistatus(Gone("/cal/a.ics", status), Token("t2"))));

        var step = await Client(server).SyncAsync(Calendar(), State("t1", new() { ["a.ics"] = "\"1\"" }), Ct);

        step.Value!.Removed.Should().Equal("a.ics");
        step.Value.Changed.Should().BeEmpty();
    }

    /// <summary>
    /// SY-14 (P `test_sync_ignores_removal_of_unknown_member`: Nextcloud purging its trash bin reports hrefs the client never
    /// had; python-caldav drops them), flag <c>sync-token.delete.created-and-deleted</c>. Elarion reports every removal of an
    /// incremental step, known or not (CalDavClient.SyncAsync: a removal is reported when the listing is not full), so the
    /// application decides; this test pins that choice.
    /// </summary>
    [Fact]
    public async Task ARemovalOfAnUnknownMemberInAnIncrementalStep_IsReported() {
        var server = new FakeDavServer();
        server.On("REPORT", Cal, _ => Reply.Xml(207, Multistatus(Gone("/cal/never-seen.ics"), Token("t2"))));

        var step = await Client(server).SyncAsync(Calendar(), State("t1", new() { ["a.ics"] = "\"1\"" }), Ct);

        step.Value!.Removed.Should().Equal("never-seen.ics");
    }

    /// <summary>
    /// RP-05 (P: iCloud's sync-token is an opaque base64 string, not a URI): the token is kept as received and sent back
    /// XML-escaped.
    /// </summary>
    [Fact]
    public async Task AnOpaqueNonUriSyncToken_IsSentBackAsReceived() {
        const string token = "HwoQEgwAAQ+/=&x<y>";
        var escaped = token.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        var server = new FakeDavServer();
        server.On("REPORT", Cal, request => IsSync(request)
            ? Reply.Xml(207, Multistatus(Token(escaped)))
            : Reply.Xml(207, Multistatus()));
        var client = Client(server);

        var first = await client.SyncAsync(Calendar(), null, Ct);
        var second = await client.SyncAsync(Calendar(), first.Value!.State, Ct);

        second.HasValue.Should().BeTrue(second.Error?.ToString());
        server.Requests[1].Body.Should().Contain($"<D:sync-token>{escaped}</D:sync-token>");
    }

    /// <summary>
    /// SY-19 (T `TestSyncTokenInvalidTokenGrading`), SY-20 (P `test_report_412_keeps_valid_sync_token`: Radicale 409),
    /// SY-21 (P `test_sync_token_expired_falls_back`, bare 403), SY-22 (T: Ox answers a bare 400 to tokens older than the
    /// delete of an RDATE-only event), flags <c>sync-token.invalid-token</c> (Xandikos 412, CCS 500 without precondition)
    /// and <c>sync-token.deleted-rdate</c> (Ox): a refused token is a full comparison, whatever status the refusal has.
    /// 412 is covered by CalDavClientServerQuirkTests.
    /// </summary>
    [Theory]
    [InlineData(403, "<D:error xmlns:D=\"DAV:\"><D:valid-sync-token/></D:error>")]
    [InlineData(403, "")]
    [InlineData(409, "<D:error xmlns:D=\"DAV:\"><D:valid-sync-token/></D:error>")]
    [InlineData(400, "")]
    public async Task AStoredTokenRefusedWithAnyStatus_IsAFullComparison(int status, string body) {
        var server = new FakeDavServer();
        server.On("REPORT", Cal, request => !IsSync(request)
            ? Reply.Xml(207, Multistatus(Item("/cal/a.ics", "2", "A2")))
            : request.Body.Contains("<D:sync-token></D:sync-token>", StringComparison.Ordinal)
                ? Reply.Xml(207, Multistatus(Response("/cal/a.ics", ETag("2")), Response("/cal/c.ics", ETag("1")), Token("t9")))
                : Reply.Xml(status, body));

        var step = await Client(server).SyncAsync(Calendar(), State("t-stale", new() { ["a.ics"] = "\"1\"", ["b.ics"] = "\"1\"", ["c.ics"] = "\"1\"" }), Ct);

        step.HasValue.Should().BeTrue(step.Error?.ToString());
        step.Value!.Changed.Select(static o => o.Name).Should().Equal("a.ics");
        step.Value.Removed.Should().Equal("b.ics");
    }

    /// <summary>
    /// Elarion's choice against CCS's flag <c>sync-token.invalid-token</c> (a 500 without precondition for a stale token): a
    /// 500 stays an error, because retrying would hide a real outage behind a second failure; the caller keeps its state.
    /// </summary>
    [Fact]
    public async Task AStoredTokenAnsweredWith500_IsAServerError() {
        var server = new FakeDavServer();
        server.On("REPORT", Cal, _ => Reply.Xml(500, ""));

        var step = await Client(server).SyncAsync(Calendar(), State("t-stale", new() { ["a.ics"] = "\"1\"" }), Ct);

        step.Error!.Code.Should().Be(CalDavErrorCodes.ServerError);
        step.Error.StatusCode.Should().Be(500);
    }

    /// <summary>
    /// Flag <c>sync-token.invalid-token</c> (DAViCal, Synology, SOGo, Stalwart, Purelymail, Ox answer a stale token with a
    /// 207 full listing): that cannot be told from an incremental answer, so it is read as one; objects whose ETag the
    /// client holds are not fetched again, and nothing is removed.
    /// </summary>
    [Fact]
    public async Task AStaleTokenAnsweredWithAFullListing_FetchesOnlyWhatChanged() {
        var server = new FakeDavServer();
        server.On("REPORT", Cal, request => IsSync(request)
            ? Reply.Xml(207, Multistatus(Response("/cal/a.ics", ETag("1")), Response("/cal/b.ics", ETag("2")), Token("t9")))
            : Reply.Xml(207, Multistatus(Item("/cal/b.ics", "2", "B2"))));

        var step = await Client(server).SyncAsync(Calendar(), State("t-stale", new() { ["a.ics"] = "\"1\"", ["b.ics"] = "\"1\"" }), Ct);

        step.Value!.Changed.Select(static o => o.Name).Should().Equal("b.ics");
        step.Value.Removed.Should().BeEmpty();
        server.Requests.Single(r => r.Body.Contains("calendar-multiget")).Body.Should().NotContain("a.ics");
    }

    /// <summary>
    /// SY-05 (D `reportChanges truncated parses error and sync token`), SY-07 (P `test_parse_sync_collection_truncated`),
    /// SY-09 (P `test_507_without_reason_phrase`), SY-10 (T: the marker names the collection without its trailing slash),
    /// flags <c>sync-token.limit</c> and <c>propfind.limit</c> (Stalwart): a 507 for the collection is a truncation,
    /// continued from its token.
    /// </summary>
    [Fact]
    public async Task ATruncatedAnswer_IsContinuedFromItsToken() {
        var server = new FakeDavServer();
        server.On("REPORT", Cal, request => !IsSync(request)
            ? Reply.Xml(207, Multistatus(Item("/cal/a.ics", "1", "A"), Item("/cal/b.ics", "1", "B")))
            : request.Body.Contains("<D:sync-token>t1</D:sync-token>", StringComparison.Ordinal)
                ? Reply.Xml(207, Multistatus(Response("/cal/b.ics", ETag("1")), Token("t2")))
                : Reply.Xml(207, Multistatus(Response("/cal/a.ics", ETag("1")),
                    "<D:response><D:href>/cal</D:href><D:status>HTTP/1.1 507</D:status><D:error><D:number-of-matches-within-limits/></D:error></D:response>",
                    Token("t1"))));
        var client = Client(server);

        var first = await client.SyncAsync(Calendar(), null, Ct);
        var second = await client.SyncAsync(Calendar(), first.Value!.State, Ct);

        first.Value.Changed.Select(static o => o.Name).Should().Equal("a.ics");
        first.Value.HasMore.Should().BeTrue();
        second.Value!.Changed.Select(static o => o.Name).Should().Equal("b.ics");
        second.Value.HasMore.Should().BeFalse();
    }

    /// <summary>
    /// SY-08 (P `TestSyncCollectionTruncated.test_stops_when_the_token_does_not_move`), flag <c>sync-token.limit</c> (the
    /// client loops while a 507 for the request URL appears and the token moves): a server whose checkpoint does not advance
    /// must not keep the application stepping forever.
    /// </summary>
    [Fact]
    public async Task ATruncatedAnswerWhoseTokenDidNotMove_HasNoMore() {
        var server = new FakeDavServer();
        server.On("REPORT", Cal, _ => Reply.Xml(207, Multistatus(Gone("/cal/", "HTTP/1.1 507 Insufficient Storage"), Token("t1"))));

        var step = await Client(server).SyncAsync(Calendar(), State("t1", new() { ["a.ics"] = "\"1\"" }), Ct);

        step.HasValue.Should().BeTrue(step.Error?.ToString());
        step.Value!.HasMore.Should().BeFalse();
    }

    /// <summary>SY-09 (P `test_507_for_another_href_is_not_a_truncation`): only a 507 for the request URL marks a truncation.</summary>
    [Fact]
    public async Task A507ForAMember_IsNotATruncation() {
        var server = new FakeDavServer();
        server.On("REPORT", Cal, request => IsSync(request)
            ? Reply.Xml(207, Multistatus(Response("/cal/a.ics", ETag("1")), Gone("/cal/b.ics", "HTTP/1.1 507 Insufficient Storage"), Token("t1")))
            : Reply.Xml(207, Multistatus(Item("/cal/a.ics", "1", "A"))));

        var step = await Client(server).SyncAsync(Calendar(), null, Ct);

        step.Value!.Changed.Select(static o => o.Name).Should().Contain("a.ics");
        step.Value.HasMore.Should().BeFalse();
    }

    /// <summary>
    /// SY-21 (P `test_403_unsupported_report_falls_back`: Zimbra advertises but refuses the report with 403
    /// <c>DAV:supported-report</c>), flag <c>sync-token</c> (Robur, Posteo, GMX answer it with an error): a first step whose
    /// sync-collection is refused lists by CTag and ETags.
    /// </summary>
    [Theory]
    [InlineData(403, "<D:error xmlns:D=\"DAV:\"><D:supported-report/></D:error>")]
    [InlineData(405, "")]
    [InlineData(501, "")]
    public async Task AFirstStepWhoseSyncCollectionIsRefused_ListsByETags(int status, string body) {
        var server = new FakeDavServer();
        server.On("REPORT", Cal, request => IsSync(request) ? Reply.Xml(status, body) : Reply.Xml(207, Multistatus(Item("/cal/a.ics", "1", "A"))));
        server.On("PROPFIND", Cal, request => request.Depth == "0"
            ? Reply.Xml(207, Multistatus(Response("/cal/", "<CS:getctag>c1</CS:getctag>")))
            : Reply.Xml(207, Multistatus(Response("/cal/", "<D:resourcetype><D:collection/><C:calendar/></D:resourcetype>"),
                Response("/cal/a.ics", ETag("1") + "<D:resourcetype/>"))));

        var step = await Client(server).SyncAsync(Calendar(), null, Ct);

        step.HasValue.Should().BeTrue(step.Error?.ToString());
        step.Value!.Changed.Select(static o => o.Name).Should().Equal("a.ics");
    }

    /// <summary>
    /// SY-25 (P `test_fallback_fetches_etags_when_missing`), SY-26 (V `_parse_prop_responses`: members without ETag, a
    /// subcollection beside the objects), a server without <c>CS:getctag</c>: every step lists, members without an ETag are
    /// fetched, collections are skipped.
    /// </summary>
    [Fact]
    public async Task WithoutGetCTagOrGetETag_EveryStepListsAndTheObjectsArrive() {
        var server = new FakeDavServer();
        server.On("PROPFIND", Cal, request => request.Depth == "0"
            ? Reply.Xml(207, Multistatus(Response("/cal/", "<CS:getctag/>", "HTTP/1.1 404 Not Found")))
            : Reply.Xml(207, Multistatus(Response("/cal/", "<D:resourcetype><D:collection/><C:calendar/></D:resourcetype>"),
                Response("/cal/sub/", "<D:resourcetype><D:collection/></D:resourcetype>"),
                Response("/cal/a.ics", "<D:resourcetype/>"))));
        server.On("REPORT", Cal, _ => Reply.Xml(207, Multistatus(Item("/cal/a.ics", "1", "A"))));
        var client = Client(server);

        var first = await client.SyncAsync(Calendar(sync: false), null, Ct);
        var second = await client.SyncAsync(Calendar(sync: false), first.Value!.State, Ct);

        first.Value.Changed.Select(static o => o.Name).Should().Equal("a.ics");
        first.Value.Changed[0].ETag.Should().Be("\"1\"");
        second.HasValue.Should().BeTrue(second.Error?.ToString());
        server.Requests.Count(r => r.Method == "PROPFIND" && r.Depth == "1").Should().Be(2);
        server.Requests.Should().NotContain(r => r.Body.Contains("sync-collection"));
    }

    // ---- Multiget ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// MG-02 (T `test_the_missing_href_left_out_is_unsupported`), V `get_multi` (hrefs not returned raise NotFound), flag
    /// <c>multiget.missing-href</c> (Nextcloud, Baikal, DAViCal, Synology, Davis, Infomaniak leave a missing href out): an href
    /// asked for and not answered is not found, so a known object is removed.
    /// </summary>
    [Fact]
    public async Task AnHrefTheMultigetLeavesOut_IsARemoval() {
        var server = new FakeDavServer();
        server.On("REPORT", Cal, request => IsSync(request)
            ? Reply.Xml(207, Multistatus(Response("/cal/a.ics", ETag("2")), Response("/cal/b.ics", ETag("2")), Token("t2")))
            : Reply.Xml(207, Multistatus(Item("/cal/a.ics", "2", "A2"))));

        var step = await Client(server).SyncAsync(Calendar(), State("t1", new() { ["a.ics"] = "\"1\"", ["b.ics"] = "\"1\"" }), Ct);

        step.Value!.Changed.Select(static o => o.Name).Should().Equal("a.ics");
        step.Value.Removed.Should().Equal("b.ics");
    }

    /// <summary>MG-03 (T `MultigetServer missing_as`: a missing href answered as a 404 propstat instead of a response-level 404).</summary>
    [Fact]
    public async Task AMultigetAnsweringA404PropstatForAnHref_IsARemoval() {
        var server = new FakeDavServer();
        server.On("REPORT", Cal, request => IsSync(request)
            ? Reply.Xml(207, Multistatus(Response("/cal/b.ics", ETag("2")), Token("t2")))
            : Reply.Xml(207, Multistatus(Response("/cal/b.ics", "<D:getetag/><C:calendar-data/>", "HTTP/1.1 404 Not Found"))));

        var step = await Client(server).SyncAsync(Calendar(), State("t1", new() { ["b.ics"] = "\"1\"" }), Ct);

        step.Value!.Removed.Should().Equal("b.ics");
        step.Value.Changed.Should().BeEmpty();
    }

    // ---- Content -------------------------------------------------------------------------------------------------------

    /// <summary>
    /// CT-10 (G `TestQueryCalendarMalformedObject`: one truncated object beside good ones), Radicale/Xandikos H01 (a VEVENT
    /// without its END lines): the good objects come back clean and the broken one with its <c>calendar.*</c> errors.
    /// </summary>
    [Fact]
    public async Task OneTruncatedObject_DoesNotSpoilTheOthers() {
        var server = new FakeDavServer();
        server.On("REPORT", Cal, request => IsSync(request)
            ? Reply.Xml(207, Multistatus(Response("/cal/a.ics", ETag("1")), Response("/cal/bad.ics", ETag("1")), Token("t1")))
            : Reply.Xml(207, Multistatus(Item("/cal/a.ics", "1", "A"),
                Response("/cal/bad.ics", ETag("1") + CalendarData("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nBEGIN:VEVENT\r\nUID:bad\r\n")))));

        var step = await Client(server).SyncAsync(Calendar(), null, Ct);

        step.HasValue.Should().BeTrue(step.Error?.ToString());
        step.Value!.Changed.Single(o => o.Name == "a.ics").Data.HasErrors.Should().BeFalse();
        step.Value.Changed.Single(o => o.Name == "bad.ics").Data.HasErrors.Should().BeTrue();
    }

    /// <summary>
    /// RP-29 (V `test_xml_specialchars`: servers embed calendar data with raw C0 control characters, making the XML
    /// ill-formed; vdirsyncer strips them), Radicale/Xandikos/Cyrus H04 (Cyrus serves stored control characters): one object
    /// with a form feed in its SUMMARY must not fail the step.
    /// </summary>
    [Fact]
    public async Task RawControlCharactersInCalendarData_DoNotFailTheStep() {
        var server = new FakeDavServer();
        server.On("REPORT", Cal, request => IsSync(request)
            ? Reply.Xml(207, Multistatus(Response("/cal/a.ics", ETag("1")), Response("/cal/b.ics", ETag("1")), Token("t1")))
            : Reply.Xml(207, Multistatus(Item("/cal/a.ics", "1", "A"), Item("/cal/b.ics", "1", "ct\u0015rl\u000c"))));

        var step = await Client(server).SyncAsync(Calendar(), null, Ct);

        step.HasValue.Should().BeTrue(step.Error?.ToString());
        step.Value!.Changed.Select(static o => o.Name).Should().BeEquivalentTo("a.ics", "b.ics");
    }

    /// <summary>
    /// Line endings and wrapping of calendar-data as servers send it: bare LF (XML normalises raw CRLF to LF anyway), CRLF
    /// as character references, a CDATA section, non-ASCII text (CT-20).
    /// </summary>
    [Fact]
    public async Task CalendarDataWithLfCrLfReferencesOrCdata_IsRead() {
        var server = new FakeDavServer();
        server.On("REPORT", Cal, request => IsSync(request)
            ? Reply.Xml(207, Multistatus(Response("/cal/lf.ics", ETag("1")), Response("/cal/crlf.ics", ETag("1")), Response("/cal/cdata.ics", ETag("1")), Token("t1")))
            : Reply.Xml(207, Multistatus(
                Response("/cal/lf.ics", ETag("1") + CalendarData(Ics("lf", "Line feed", "\n"))),
                Response("/cal/crlf.ics", ETag("1") + "<C:calendar-data>" + Ics("crlf", "Carriage return").Replace("\r\n", "&#13;&#10;") + "</C:calendar-data>"),
                Response("/cal/cdata.ics", ETag("1") + "<C:calendar-data><![CDATA[" + Ics("cdata", "Grüße <&>") + "]]></C:calendar-data>"))));

        var step = await Client(server).SyncAsync(Calendar(), null, Ct);

        var summaries = step.Value!.Changed.ToDictionary(static o => o.Name, static o => o.Data.Events.Single().Value!.Summary);
        summaries.Should().BeEquivalentTo(new Dictionary<string, string?> { ["lf.ics"] = "Line feed", ["crlf.ics"] = "Carriage return", ["cdata.ics"] = "Grüße <&>" });
        step.Value.Changed.Should().AllSatisfy(o => o.Data.HasErrors.Should().BeFalse());
    }

    /// <summary>
    /// CT-09 (V `test_multiple_items`), flag <c>save.no-uid-conflict</c> (Baikal, DAViCal, Robur and others store what they
    /// get), sabre W65 (a body with two UIDs): an object holding two events with different UIDs is read whole.
    /// </summary>
    [Fact]
    public async Task AnObjectWithSeveralUids_IsReadWhole() {
        const string ics = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Test//EN\r\n"
                           + "BEGIN:VEVENT\r\nUID:one\r\nDTSTAMP:20261101T000000Z\r\nDTSTART:20261201T090000Z\r\nSUMMARY:One\r\nEND:VEVENT\r\n"
                           + "BEGIN:VEVENT\r\nUID:two\r\nDTSTAMP:20261101T000000Z\r\nDTSTART:20261202T090000Z\r\nSUMMARY:Two\r\nEND:VEVENT\r\n"
                           + "END:VCALENDAR\r\n";
        var server = new FakeDavServer();
        server.On("REPORT", Cal, request => IsSync(request)
            ? Reply.Xml(207, Multistatus(Response("/cal/both.ics", ETag("1")), Token("t1")))
            : Reply.Xml(207, Multistatus(Response("/cal/both.ics", ETag("1") + CalendarData(ics)))));

        var step = await Client(server).SyncAsync(Calendar(), null, Ct);

        step.Value!.Changed.Single().Data.Events.Select(static e => e.Value!.Uid).Should().Equal("one", "two");
    }

    // ---- Discovery -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// DI-07 (P `test_relative_location`, `test_chain_ends_at_the_last_hop`), DI-08 (D `followRedirects 302 Found updates
    /// location`): a chain of 302 and 307 with relative and absolute Locations, each hop repeated as PROPFIND with its body.
    /// </summary>
    [Fact]
    public async Task ARedirectChainWithRelativeLocations_IsFollowedToTheLastHop() {
        var server = new FakeDavServer();
        server.On("PROPFIND", "https://example.com/", _ => Reply.Xml(404, ""));
        server.On("PROPFIND", "https://example.com/.well-known/caldav", _ => Reply.Redirect(302, "/dav/"));
        server.On("PROPFIND", "https://example.com/dav/", _ => Reply.Redirect(307, "https://dav.example.com/dav/"));
        server.On("PROPFIND", "https://dav.example.com/dav/", _ => Reply.Xml(207, Multistatus(Response("/dav/",
            "<D:current-user-principal><D:href>/dav/principals/u/</D:href></D:current-user-principal>"))));
        server.On("PROPFIND", "https://dav.example.com/dav/principals/u/", _ => Reply.Xml(207, Multistatus(Response("/dav/principals/u/",
            "<C:calendar-home-set><D:href>/dav/calendars/u/</D:href></C:calendar-home-set>"))));
        server.On("PROPFIND", "https://dav.example.com/dav/calendars/u/", _ => Reply.Xml(207, Multistatus(Response("/dav/calendars/u/work/", CalendarProps("Work")))));

        var account = await Client(server).DiscoverAsync(new Uri("https://example.com/"), Ct);

        account.HasValue.Should().BeTrue(account.Error?.ToString());
        account.Value!.PrincipalUrl.Should().Be(new Uri("https://dav.example.com/dav/principals/u/"));
        account.Value.Calendars.Single().Url.Should().Be(new Uri("https://dav.example.com/dav/calendars/u/work/"));
        server.Requests.Where(r => r.Url.StartsWith("https://example.com/dav", StringComparison.Ordinal) || r.Url == "https://dav.example.com/dav/")
            .Should().HaveCount(2).And.AllSatisfy(r => {
                r.Method.Should().Be("PROPFIND");
                r.Body.Should().Contain("current-user-principal");
            });
    }

    /// <summary>DI-07 (P `test_redirect_loop_is_bounded`): a redirect loop ends after <see cref="CalDavClientOptions.MaxRedirects"/> hops with an error.</summary>
    [Fact]
    public async Task ARedirectLoop_IsBounded() {
        var server = new FakeDavServer();
        server.On("PROPFIND", "https://example.com/", _ => Reply.Redirect(301, "/loop"));
        server.On("PROPFIND", "https://example.com/loop", _ => Reply.Redirect(301, "/"));
        server.On("PROPFIND", "https://example.com/.well-known/caldav", _ => Reply.Redirect(308, "/.well-known/caldav"));

        var account = await Client(server, new CalDavClientOptions { MaxRedirects = 3 }).DiscoverAsync(new Uri("https://example.com/"), Ct);

        account.HasValue.Should().BeFalse();
        server.Requests.Count.Should().BeLessThanOrEqualTo(2 * (3 + 1));
    }

    /// <summary>
    /// DI-05 (P `TestRequireTLSDowngradeBlocked`), DI-06 (D `followRedirects HTTPS to HTTP throws`): a redirect from https to
    /// http is not followed, so the credentials never travel in clear text.
    /// </summary>
    [Fact]
    public async Task ARedirectFromHttpsToHttp_IsNotFollowed() {
        var server = new FakeDavServer();
        server.On("PROPFIND", "https://example.com/", _ => Reply.Redirect(301, "http://example.com/dav/"));
        server.On("PROPFIND", "https://example.com/.well-known/caldav", _ => Reply.Redirect(301, "http://example.com/dav/"));
        server.On("PROPFIND", "http://example.com/dav/", _ => Reply.Xml(207, Multistatus(Response("/dav/",
            "<D:current-user-principal><D:href>/dav/principals/u/</D:href></D:current-user-principal>"))));

        var account = await Client(server).DiscoverAsync(new Uri("https://example.com/"), Ct);

        account.HasValue.Should().BeFalse();
        server.Requests.Should().NotContain(r => r.Url.StartsWith("http://", StringComparison.Ordinal));
    }

    /// <summary>
    /// DI-03 (V `_find_principal_impl`: Synology answers without current-user-principal, so the request URL is the
    /// principal), flag <c>get-current-user-principal</c> (DavMail, mail.ru, Synology lack it).
    /// </summary>
    [Fact]
    public async Task WithoutCurrentUserPrincipal_TheGivenUrlIsThePrincipal() {
        const string url = "https://nas.example.com/caldav/u/";
        var server = new FakeDavServer();
        server.On("PROPFIND", url, request => request.Depth == "0"
            ? Reply.Xml(207, Multistatus(
                "<D:response><D:href>/caldav/u/</D:href>"
                + "<D:propstat><D:prop><D:resourcetype><D:collection/></D:resourcetype><C:calendar-home-set><D:href>/caldav/u/</D:href></C:calendar-home-set></D:prop>"
                + "<D:status>HTTP/1.1 200 OK</D:status></D:propstat>"
                + "<D:propstat><D:prop><D:current-user-principal/><D:displayname/></D:prop><D:status>HTTP/1.1 404 Not Found</D:status></D:propstat></D:response>"))
            : Reply.Xml(207, Multistatus(Response("/caldav/u/home/", CalendarProps("Home")))));
        server.On("PROPFIND", "https://nas.example.com/.well-known/caldav", _ => Reply.Xml(404, ""));

        var account = await Client(server).DiscoverAsync(new Uri(url), Ct);

        account.HasValue.Should().BeTrue(account.Error?.ToString());
        account.Value!.PrincipalUrl.Should().Be(new Uri(url));
        account.Value.Calendars.Single().DisplayName.Should().Be("Home");
    }

    /// <summary>
    /// Flag <c>well-known</c> (Posteo has none; GMX and Infomaniak redirect it to a URL that is not CalDAV), DI-02 (V: fall
    /// back to well-known when the URL names no principal): when the given URL answered a valid multistatus without a
    /// principal, the result says that, whatever the well-known URL answers.
    /// </summary>
    [Theory]
    [InlineData(404)]
    [InlineData(405)]
    [InlineData(200)]
    public async Task WhenTheWellKnownUrlFindsNoPrincipalEither_TheResultIsNotFound(int wellKnownStatus) {
        var server = new FakeDavServer();
        server.On("PROPFIND", "https://example.com/", _ => Reply.Xml(207, Multistatus(Response("/", "<D:resourcetype><D:collection/></D:resourcetype>"))));
        server.On("PROPFIND", "https://example.com/.well-known/caldav", _ => wellKnownStatus == 200
            ? new Reply(200, Encoding.UTF8.GetBytes("<html><body>Welcome</body></html>"), "text/html")
            : Reply.Xml(wellKnownStatus, ""));

        var account = await Client(server).DiscoverAsync(new Uri("https://example.com/"), Ct);

        account.HasValue.Should().BeFalse();
        account.Error!.Code.Should().Be(CalDavErrorCodes.NotFound);
    }

    /// <summary>DI-09 (P `testCalendarHomeSetOnOtherHost`, `testGetCalendarsOnOtherHost`): a principal on another host, its relative home-set resolved against that host.</summary>
    [Fact]
    public async Task APrincipalOnAnotherHost_IsFollowed_AndItsRelativeHomeIsOnThatHost() {
        var server = new FakeDavServer();
        server.On("PROPFIND", "https://example.com/", _ => Reply.Xml(207, Multistatus(Response("/",
            "<D:current-user-principal><D:href>https://p07.example.net/123/principal/</D:href></D:current-user-principal>"))));
        server.On("PROPFIND", "https://p07.example.net/123/principal/", _ => Reply.Xml(207, Multistatus(Response("/123/principal/",
            "<C:calendar-home-set><D:href>/123/calendars/</D:href></C:calendar-home-set>"))));
        server.On("PROPFIND", "https://p07.example.net/123/calendars/", _ => Reply.Xml(207, Multistatus(Response("/123/calendars/work/", CalendarProps("Work")))));

        var account = await Client(server).DiscoverAsync(new Uri("https://example.com/"), Ct);

        account.Value!.PrincipalUrl.Should().Be(new Uri("https://p07.example.net/123/principal/"));
        account.Value.HomeUrl.Should().Be(new Uri("https://p07.example.net/123/calendars/"));
        account.Value.Calendars.Single().Url.Should().Be(new Uri("https://p07.example.net/123/calendars/work/"));
    }

    /// <summary>
    /// RFC 4791 §6.2.1 lets <c>calendar-home-set</c> hold several hrefs (the task names it; P's home-set handling reads a
    /// list): the calendars of every home are the account's calendars.
    /// </summary>
    [Fact]
    public async Task AHomeSetWithSeveralHrefs_ListsTheCalendarsOfEveryHome() {
        var server = new FakeDavServer();
        server.On("PROPFIND", "https://example.com/", _ => Reply.Xml(207, Multistatus(Response("/",
            "<D:current-user-principal><D:href>/principals/u/</D:href></D:current-user-principal>"))));
        server.On("PROPFIND", "https://example.com/principals/u/", _ => Reply.Xml(207, Multistatus(Response("/principals/u/",
            "<C:calendar-home-set><D:href>/home-a/</D:href><D:href>/home-b/</D:href></C:calendar-home-set>"))));
        server.On("PROPFIND", "https://example.com/home-a/", _ => Reply.Xml(207, Multistatus(Response("/home-a/work/", CalendarProps("Work")))));
        server.On("PROPFIND", "https://example.com/home-b/", _ => Reply.Xml(207, Multistatus(Response("/home-b/team/", CalendarProps("Team")))));

        var account = await Client(server).DiscoverAsync(new Uri("https://example.com/"), Ct);

        account.Value!.Calendars.Select(static c => c.DisplayName).Should().BeEquivalentTo("Work", "Team");
    }

    /// <summary>
    /// DI-10 (P `test_get_calendars_client_url_is_calendar_without_home_set`, `..._prefers_principal`: Nextcloud public
    /// shares have no calendar-home-set, the calendars are below the principal).
    /// </summary>
    [Fact]
    public async Task WithoutCalendarHomeSet_ThePrincipalIsTheHome() {
        var server = new FakeDavServer();
        server.On("PROPFIND", "https://example.com/", _ => Reply.Xml(207, Multistatus(Response("/",
            "<D:current-user-principal><D:href>/principals/u/</D:href></D:current-user-principal>"))));
        server.On("PROPFIND", "https://example.com/principals/u/", request => request.Depth == "0"
            ? Reply.Xml(207, Multistatus(Response("/principals/u/", "<C:calendar-home-set/><D:displayname/>", "HTTP/1.1 404 Not Found")))
            : Reply.Xml(207, Multistatus(Response("/principals/u/", "<D:resourcetype><D:collection/></D:resourcetype>"),
                Response("/principals/u/shared/", CalendarProps("Shared")))));

        var account = await Client(server).DiscoverAsync(new Uri("https://example.com/"), Ct);

        account.HasValue.Should().BeTrue(account.Error?.ToString());
        account.Value!.HomeUrl.Should().Be(new Uri("https://example.com/principals/u/"));
        account.Value.Calendars.Single().DisplayName.Should().Be("Shared");
    }

    /// <summary>DI-11 (P `TestGetCalendarsPropfindErrors`): a home that answers an error is an error, never an empty calendar list.</summary>
    [Fact]
    public async Task AHomeThatAnswersAnError_IsAnError_NotAnEmptyList() {
        var server = new FakeDavServer();
        server.On("PROPFIND", "https://example.com/", _ => Reply.Xml(207, Multistatus(Response("/",
            "<D:current-user-principal><D:href>/principals/u/</D:href></D:current-user-principal>"))));
        server.On("PROPFIND", "https://example.com/principals/u/", _ => Reply.Xml(207, Multistatus(Response("/principals/u/",
            "<C:calendar-home-set><D:href>/home/</D:href></C:calendar-home-set>"))));
        server.On("PROPFIND", "https://example.com/home/", _ => Reply.Xml(500, ""));

        var account = await Client(server).DiscoverAsync(new Uri("https://example.com/"), Ct);

        account.Error!.Code.Should().Be(CalDavErrorCodes.ServerError);
        account.Error.StatusCode.Should().Be(500);
    }

    // ---- Writes --------------------------------------------------------------------------------------------------------

    /// <summary>
    /// WR-05 (P `test_if_match_true_with_only_weak_etag_refuses`, T: GMX answers PUT with a weak ETag), WR-06 (P
    /// `test_a_bare_etag_from_put_is_quoted`: Robur), WR-07 (P `test_percent_encoded_etag_from_put_is_decoded`: Bedework 5),
    /// WR-08 (P, V: no ETag after the server changed the data), sabre W66, flag <c>save.etag</c>: the ETag of a write is
    /// strong and quoted, or <see langword="null"/>.
    /// </summary>
    [Theory]
    [InlineData("\"abc\"", "\"abc\"")]
    [InlineData("W/\"abc\"", null)]
    [InlineData("abc", "\"abc\"")]
    [InlineData("%22abc%22", "\"abc\"")]
    [InlineData(null, null)]
    public async Task TheETagOfAPut_IsStrongAndQuoted_OrNull(string? header, string? expected) {
        var server = new FakeDavServer();
        server.On("PUT", Cal + "a.ics", _ => new Reply(201, [], null) { ETag = header });

        var put = await Client(server).PutAsync(Calendar(), "a.ics", Event("a"), null, Ct);

        put.HasValue.Should().BeTrue(put.Error?.ToString());
        put.Value!.ETag.Should().Be(expected);
    }

    /// <summary>WR-08 (V `_put`: 201 or 204), sabre W66: every 2xx answer to a write is a success.</summary>
    [Theory]
    [InlineData(200)]
    [InlineData(201)]
    [InlineData(204)]
    public async Task A2xxAnswerToAWrite_IsASuccess(int status) {
        var server = new FakeDavServer();
        server.On("PUT", Cal + "a.ics", _ => new Reply(status, [], null) { ETag = "\"1\"" });
        server.On("DELETE", Cal + "a.ics", _ => new Reply(status, [], null));
        var client = Client(server);

        var put = await client.PutAsync(Calendar(), "a.ics", Event("a"), "\"0\"", Ct);
        var delete = await client.DeleteAsync(Calendar(), "a.ics", "\"1\"", Ct);

        put.Value!.ETag.Should().Be("\"1\"");
        delete.HasValue.Should().BeTrue();
    }

    /// <summary>
    /// WR-12 (P `TestPreconditionsOnOtherStatuses`), flag <c>save.no-uid-conflict</c> (SOGo refuses a duplicate UID with a
    /// bare 409, Radicale with 409): a 409 to a conditional write is a conflict, like 412.
    /// </summary>
    [Fact]
    public async Task A409ToAWrite_IsAConflict() {
        var server = new FakeDavServer();
        server.On("PUT", Cal + "a.ics", _ => Reply.Xml(409, ""));

        var put = await Client(server).PutAsync(Calendar(), "a.ics", Event("a"), null, Ct);

        put.Error!.Code.Should().Be(CalDavErrorCodes.Conflict);
        put.Error.StatusCode.Should().Be(409);
    }

    /// <summary>
    /// WR-17 (V `_assert_multistatus_success`: Xandikos answers a PUT with a 207), RP-36 (D `copy/delete/move 207
    /// Multi-Status throws`): a 207 whose member failed is not a success.
    /// </summary>
    [Theory]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task A207WithAFailedMember_IsNotASuccess(string method) {
        var server = new FakeDavServer();
        server.On(method, Cal + "a.ics", _ => Reply.Xml(207, Multistatus(Gone("/cal/a.ics", "HTTP/1.1 423 Locked"))));
        var client = Client(server);

        var result = method == "PUT"
            ? await client.PutAsync(Calendar(), "a.ics", Event("a"), "\"1\"", Ct)
            : await client.DeleteAsync(Calendar(), "a.ics", "\"1\"", Ct);

        result.HasValue.Should().BeFalse();
    }

    /// <summary>
    /// WR-10 (P `test_put_repeated_at_location`; D `put If-None-Match follows 301 redirect`), flag <c>save-load.stable-url</c>
    /// (Zimbra redirects a PUT under another name to <c>&lt;uid&gt;.ics</c> with 302): the PUT is repeated at the Location
    /// with its body and precondition.
    /// </summary>
    [Fact]
    public async Task A302ToAPut_IsRepeatedAtTheLocation_WithBodyAndPrecondition() {
        var server = new FakeDavServer();
        server.On("PUT", Cal + "a.ics", _ => Reply.Redirect(302, "/cal/uid-a.ics"));
        server.On("PUT", Cal + "uid-a.ics", _ => new Reply(201, [], null) { ETag = "\"1\"" });

        var put = await Client(server).PutAsync(Calendar(), "a.ics", Event("uid-a"), null, Ct);

        put.Value!.ETag.Should().Be("\"1\"");
        var repeated = server.Requests.Single(r => r.Url == Cal + "uid-a.ics");
        repeated.Method.Should().Be("PUT");
        repeated.Body.Should().Contain("UID:uid-a");
        repeated.IfNoneMatch.Should().Be("*");
    }

    /// <summary>WR-10 (P `test_303_to_a_put_is_success_at_the_location`): a 303 after a PUT means the PUT succeeded (RFC 9110 §15.4.4); it is not repeated.</summary>
    [Fact]
    public async Task A303ToAPut_IsASuccess_NotRepeated() {
        var server = new FakeDavServer();
        server.On("PUT", Cal + "a.ics", _ => Reply.Redirect(303, "/cal/a.ics?view=1"));

        var put = await Client(server).PutAsync(Calendar(), "a.ics", Event("a"), null, Ct);

        put.HasValue.Should().BeTrue(put.Error?.ToString());
        server.Requests.Count(r => r.Method == "PUT").Should().Be(1);
    }

    /// <summary>What the fake server answers: a status, a body, its media type and the ETag and Location headers as raw text.</summary>
    private sealed record Reply(int Status, byte[] Body, string? ContentType) {
        public string? ETag { get; init; }

        public string? Location { get; init; }

        public static Reply Xml(int status, string body) => new(status, Encoding.UTF8.GetBytes(body), "application/xml; charset=utf-8");

        public static Reply Redirect(int status, string location) => new(status, [], null) { Location = location };
    }

    /// <summary>A recorded request.</summary>
    private sealed record Recorded(string Method, string Url, string? Depth, string? IfMatch, string? IfNoneMatch, string Body);

    private sealed record Incoming(string? Depth, string Body);

    /// <summary>Answers requests from a table of handwritten responses, sends headers exactly as written, and records the requests.</summary>
    private sealed class FakeDavServer : HttpMessageHandler {
        private readonly List<(string Method, string Url, Func<Incoming, Reply> Answer)> _routes = [];

        public List<Recorded> Requests { get; } = [];

        public void On(string method, string url, Func<Incoming, Reply> answer) => _routes.Add((method, new Uri(url).AbsoluteUri, answer));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            var url = request.RequestUri!.AbsoluteUri;
            var depth = Header(request, "Depth");
            Requests.Add(new Recorded(request.Method.Method, url, depth, Header(request, "If-Match"), Header(request, "If-None-Match"), body));
            var route = _routes.FirstOrDefault(r => r.Method == request.Method.Method && r.Url == url);
            if (route.Answer is null) return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new ByteArrayContent([]) };
            var reply = route.Answer(new Incoming(depth, body));
            var content = new ByteArrayContent(reply.Body);
            if (reply.ContentType is not null) content.Headers.TryAddWithoutValidation("Content-Type", reply.ContentType);
            var response = new HttpResponseMessage((HttpStatusCode)reply.Status) { Content = content };
            if (reply.ETag is not null) response.Headers.TryAddWithoutValidation("ETag", reply.ETag);
            if (reply.Location is not null) response.Headers.Location = new Uri(reply.Location, UriKind.RelativeOrAbsolute);
            return response;
        }

        private static string? Header(HttpRequestMessage request, string name) =>
            request.Headers.TryGetValues(name, out var values) ? string.Join(", ", values) : null;
    }
}
