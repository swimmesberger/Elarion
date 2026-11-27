using System.Net;
using System.Net.Http.Headers;
using System.Text;
using AwesomeAssertions;
using Elarion.CalDav;
using Elarion.Calendars;
using Xunit;

namespace Elarion.Tests.CalDav;

/// <summary>
/// The client against handwritten answers that reproduce the quirks the research note records for iCloud, Google,
/// Nextcloud, Fastmail, Zimbra and servers without <c>sync-collection</c> (plan 0006, "The client").
/// </summary>
public sealed class CalDavClientServerQuirkTests {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Ns = "xmlns:D=\"DAV:\" xmlns:C=\"urn:ietf:params:xml:ns:caldav\" xmlns:CS=\"http://calendarserver.org/ns/\" xmlns:ICAL=\"http://apple.com/ns/ical/\"";

    private static string Ics(string uid, string summary) =>
        $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Test//EN\r\nBEGIN:VEVENT\r\nUID:{uid}\r\nDTSTAMP:20261101T000000Z\r\nDTSTART:20261201T090000Z\r\n"
        + $"DTEND:20261201T100000Z\r\nSUMMARY:{summary}\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";

    private static string Multistatus(params string[] responses) => $"<?xml version=\"1.0\"?><D:multistatus {Ns}>{string.Concat(responses)}</D:multistatus>";

    private static string Response(string href, string props, string status = "HTTP/1.1 200 OK") =>
        $"<D:response><D:href>{href}</D:href><D:propstat><D:prop>{props}</D:prop><D:status>{status}</D:status></D:propstat></D:response>";

    private static string Gone(string href) => $"<D:response><D:href>{href}</D:href><D:status>HTTP/1.1 404 Not Found</D:status></D:response>";

    private static string CalendarData(string ics) => "<C:calendar-data>" + ics.Replace("&", "&amp;").Replace("<", "&lt;") + "</C:calendar-data>";

    private static CalDavClient Client(FakeDavServer server, CalDavClientOptions? options = null) =>
        new(new HttpClient(server), options ?? new CalDavClientOptions {
            Authorization = _ => ValueTask.FromResult<AuthenticationHeaderValue?>(new AuthenticationHeaderValue("Basic", "dXNlcjpwYXNz")),
        });

    [Fact]
    public async Task ICloud_HomeOnAPartitionHost_AndNeverDepth1OnThePrincipal() {
        var server = new FakeDavServer();
        server.On("PROPFIND", "https://caldav.icloud.com/", _ => (207, Multistatus(Response("/",
            "<D:current-user-principal><D:href>/123456/principal/</D:href></D:current-user-principal>"))));
        server.On("PROPFIND", "https://caldav.icloud.com/123456/principal/", request => request.Headers.GetValues("Depth").Single() == "1"
            ? (400, "depth 1 on the principal")
            : (207, Multistatus(Response("/123456/principal/",
                "<C:calendar-home-set><D:href>https://p42-caldav.icloud.com:443/123456/calendars/</D:href></C:calendar-home-set>"))));
        server.On("PROPFIND", "https://p42-caldav.icloud.com/123456/calendars/", _ => (207, Multistatus(
            Response("/123456/calendars/", "<D:resourcetype><D:collection/></D:resourcetype>"),
            Response("/123456/calendars/home/", "<D:resourcetype><D:collection/><C:calendar/></D:resourcetype><D:displayname>Home</D:displayname>"
                                                + "<ICAL:calendar-color>#FF2968FF</ICAL:calendar-color><D:current-user-privilege-set><D:privilege><D:all/></D:privilege>"
                                                + "</D:current-user-privilege-set><D:supported-report-set><D:supported-report><D:report><D:sync-collection/></D:report>"
                                                + "</D:supported-report></D:supported-report-set>"))));

        var account = await Client(server).DiscoverAsync(new Uri("https://caldav.icloud.com/"), Ct);

        account.HasValue.Should().BeTrue(account.Error?.ToString());
        account.Value!.HomeUrl.Should().Be(new Uri("https://p42-caldav.icloud.com/123456/calendars/"));
        var home = account.Value.Calendars.Single();
        home.Url.Should().Be(new Uri("https://p42-caldav.icloud.com/123456/calendars/home/"));
        home.Color.Should().Be("#FF2968FF");
        home.IsWritable.Should().BeTrue();
        home.SupportsSyncCollection.Should().BeTrue();
        server.Requests.Should().NotContain(r => r.Method == "PROPFIND" && r.Url.EndsWith("/principal/", StringComparison.Ordinal) && r.Depth == "1");
    }

    [Fact]
    public async Task Google_WorksWithABearerTokenAndNoPrivilegeSet() {
        var server = new FakeDavServer();
        const string calendar = "https://apidata.googleusercontent.com/caldav/v2/someone%40gmail.com/events/";
        server.On("PROPFIND", "https://apidata.googleusercontent.com/caldav/v2/someone%40gmail.com/user", request =>
            request.Headers.Authorization?.Scheme == "Bearer"
                ? (207, Multistatus(Response("/caldav/v2/someone%40gmail.com/user",
                    "<D:current-user-principal><D:href>/caldav/v2/someone%40gmail.com/user</D:href></D:current-user-principal>"
                    + "<C:calendar-home-set><D:href>/caldav/v2/someone%40gmail.com/</D:href></C:calendar-home-set>")))
                : (401, ""));
        server.On("PROPFIND", "https://apidata.googleusercontent.com/caldav/v2/someone%40gmail.com/", _ => (207, Multistatus(
            Response("/caldav/v2/someone%40gmail.com/events/", "<D:resourcetype><D:collection/><C:calendar/></D:resourcetype><D:displayname>someone</D:displayname>"
                                                               + "<D:supported-report-set><D:supported-report><D:report><D:sync-collection/></D:report></D:supported-report></D:supported-report-set>"))));
        server.On("REPORT", calendar, request => request.Body.Contains("sync-collection")
            ? (207, Multistatus(Response("/caldav/v2/someone%40gmail.com/events/abc.ics", "<D:getetag>\"1\"</D:getetag>"),
                "<D:sync-token>https://www.google.com/calendar/dav/sync/1</D:sync-token>"))
            : (207, Multistatus(Response("/caldav/v2/someone%40gmail.com/events/abc.ics", "<D:getetag>\"1\"</D:getetag>" + CalendarData(Ics("abc", "Lunch"))))));
        var client = new CalDavClient(new HttpClient(server), new CalDavClientOptions {
            Authorization = _ => ValueTask.FromResult<AuthenticationHeaderValue?>(new AuthenticationHeaderValue("Bearer", "ya29.token")),
        });

        var account = await client.DiscoverAsync(new Uri("https://apidata.googleusercontent.com/caldav/v2/someone%40gmail.com/user"), Ct);
        var step = await client.SyncAsync(account.Value!.Calendars.Single(), null, Ct);

        account.Value.Calendars.Single().IsWritable.Should().BeTrue("Google has no ACL, so no privilege set");
        step.Value!.Changed.Single().Data.Events.Single().Value!.Summary.Should().Be("Lunch");
        server.Requests.Should().AllSatisfy(r => r.Authorization.Should().Be("Bearer ya29.token"));
    }

    [Fact]
    public async Task Zimbra_AMultigetAnswering404ForADeletedObject_IsARemoval() {
        var server = new FakeDavServer();
        const string calendar = "https://zimbra.example.com/dav/u/Calendar/";
        server.On("REPORT", calendar, request => request.Body.Contains("sync-collection")
            ? (207, Multistatus(Response("/dav/u/Calendar/a.ics", "<D:getetag>\"1\"</D:getetag>"), Response("/dav/u/Calendar/b.ics", "<D:getetag>\"1\"</D:getetag>"),
                "<D:sync-token>urn:zimbra:1</D:sync-token>"))
            : (207, Multistatus(Response("/dav/u/Calendar/a.ics", "<D:getetag>\"1\"</D:getetag>" + CalendarData(Ics("a", "A"))), Gone("/dav/u/Calendar/b.ics"))));

        var step = await Client(server).SyncAsync(new CalDavCalendar { Url = new Uri(calendar), SupportsSyncCollection = true }, null, Ct);

        step.Value!.Changed.Select(static o => o.Name).Should().Equal("a.ics");
        step.Value.Removed.Should().Equal("b.ics");
    }

    [Fact]
    public async Task Nextcloud_ATokenRefusedWith412_FallsBackToAFullComparison() {
        var server = new FakeDavServer();
        const string calendar = "https://cloud.example.com/remote.php/dav/calendars/u/personal/";
        var syncs = 0;
        server.On("REPORT", calendar, request => {
            if (!request.Body.Contains("sync-collection"))
                return (207, Multistatus(Response("/remote.php/dav/calendars/u/personal/a.ics", "<D:getetag>\"2\"</D:getetag>" + CalendarData(Ics("a", "A2")))));
            syncs++;
            return request.Body.Contains("<D:sync-token></D:sync-token>")
                ? (207, Multistatus(Response("/remote.php/dav/calendars/u/personal/a.ics", "<D:getetag>\"2\"</D:getetag>"),
                    Response("/remote.php/dav/calendars/u/personal/c.ics", "<D:getetag>\"1\"</D:getetag>"),
                    "<D:sync-token>http://sabre.io/ns/sync/9</D:sync-token>"))
                : (412, $"<D:error {Ns}><D:valid-sync-token/></D:error>");
        });
        var client = Client(server);
        var calendarInfo = new CalDavCalendar { Url = new Uri(calendar), SupportsSyncCollection = true };
        // A state that knows a.ics at ETag 1, b.ics, and c.ics at its current ETag, with a token the server forgot.
        var state = SyncStateOf("http://sabre.io/ns/sync/3", new() { ["a.ics"] = "\"1\"", ["b.ics"] = "\"1\"", ["c.ics"] = "\"1\"" });

        var step = await client.SyncAsync(calendarInfo, state, Ct);

        step.HasValue.Should().BeTrue(step.Error?.ToString());
        syncs.Should().Be(2);
        step.Value!.Changed.Select(static o => o.Name).Should().Equal("a.ics");
        step.Value.Removed.Should().Equal("b.ics");
    }

    [Fact]
    public async Task AServerWithoutSyncCollection_IsSyncedByCTagAndETags() {
        var server = new FakeDavServer();
        const string calendar = "https://yahoo.example.com/dav/u/Calendar/";
        var ctag = "1";
        server.On("PROPFIND", calendar, request => request.Headers.GetValues("Depth").Single() == "0"
            ? (207, Multistatus(Response("/dav/u/Calendar/", $"<CS:getctag>{ctag}</CS:getctag>")))
            : (207, Multistatus(Response("/dav/u/Calendar/", "<D:resourcetype><D:collection/><C:calendar/></D:resourcetype>"),
                Response("/dav/u/Calendar/a.ics", "<D:getetag>\"1\"</D:getetag><D:resourcetype/>"))));
        server.On("REPORT", calendar, _ => (207, Multistatus(Response("/dav/u/Calendar/a.ics", "<D:getetag>\"1\"</D:getetag>" + CalendarData(Ics("a", "A"))))));
        var client = Client(server);
        var info = new CalDavCalendar { Url = new Uri(calendar) };

        var first = await client.SyncAsync(info, null, Ct);
        var requestsAfterFirst = server.Requests.Count;
        var unchanged = await client.SyncAsync(info, first.Value!.State, Ct);

        first.Value.Changed.Select(static o => o.Name).Should().Equal("a.ics");
        unchanged.Value!.Changed.Should().BeEmpty();
        server.Requests.Count.Should().Be(requestsAfterFirst + 1, "an unchanged CTag needs no listing");
        server.Requests.Should().NotContain(r => r.Body.Contains("sync-collection"));
    }

    [Fact]
    public async Task AnObjectThatIsNotValidICalendar_ComesBackWithItsCodedErrors_AndTheStepSucceeds() {
        var server = new FakeDavServer();
        const string calendar = "https://fastmail.example.com/dav/calendars/user/u/Default/";
        server.On("REPORT", calendar, request => request.Body.Contains("sync-collection")
            ? (207, Multistatus(Response("/dav/calendars/user/u/Default/bad.ics", "<D:getetag>\"1\"</D:getetag>"), "<D:sync-token>urn:x:1</D:sync-token>"))
            : (207, Multistatus(Response("/dav/calendars/user/u/Default/bad.ics", "<D:getetag>\"1\"</D:getetag>"
                + CalendarData("BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nUID:bad\r\nDTSTART:not-a-date\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n")))));

        var step = await Client(server).SyncAsync(new CalDavCalendar { Url = new Uri(calendar), SupportsSyncCollection = true }, null, Ct);

        step.HasValue.Should().BeTrue();
        var bad = step.Value!.Changed.Single();
        bad.Data.HasErrors.Should().BeTrue();
        bad.Data.Errors.Should().OnlyContain(e => e.Code.StartsWith("calendar.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AWriteWhoseETagIsStale_IsAConflict_AndACreateSendsIfNoneMatch() {
        var server = new FakeDavServer();
        const string calendar = "https://dav.example.com/cal/";
        server.On("PUT", calendar + "a.ics", request => request.Headers.TryGetValues("If-Match", out var match) && match.Single() == "\"old\""
            ? (412, "")
            : request.Headers.TryGetValues("If-None-Match", out var none) && none.Single() == "*"
                ? (201, "")
                : (400, ""), etag: "\"new\"");
        var client = Client(server);
        var info = new CalDavCalendar { Url = new Uri(calendar) };
        var item = new CalendarEvent {
            Uid = "a", Start = CalendarDateTime.Utc(new DateTimeOffset(2026, 12, 1, 9, 0, 0, TimeSpan.Zero)), Summary = "A",
        };

        var created = await client.PutAsync(info, "a.ics", item, null, Ct);
        var conflict = await client.PutAsync(info, "a.ics", item, "\"old\"", Ct);

        created.Value!.ETag.Should().Be("\"new\"");
        conflict.Error!.Code.Should().Be(CalDavErrorCodes.Conflict);
        conflict.Error.StatusCode.Should().Be(412);
        server.Requests.First(r => r.Method == "PUT").Body.Should().Contain("DTSTAMP:", "the client stamps what it writes");
    }

    [Fact]
    public async Task AnAnswerOverTheLimit_IsTooLargeWithTheLimit_AndInvalidXmlIsAnInvalidResponse() {
        var server = new FakeDavServer();
        server.On("REPORT", "https://big.example.com/cal/", _ => (207, Multistatus(new string(' ', 4096))));
        server.On("REPORT", "https://broken.example.com/cal/", _ => (207, "<D:multistatus xmlns:D=\"DAV:\"><D:response>"));
        server.On("REPORT", "https://html.example.com/cal/", _ => (200, "<html>login</html>"));
        var client = Client(server, new CalDavClientOptions { MaxResponseBytes = 2048 });

        var big = await client.SyncAsync(new CalDavCalendar { Url = new Uri("https://big.example.com/cal/"), SupportsSyncCollection = true }, null, Ct);
        var broken = await client.SyncAsync(new CalDavCalendar { Url = new Uri("https://broken.example.com/cal/"), SupportsSyncCollection = true }, null, Ct);
        var html = await client.SyncAsync(new CalDavCalendar { Url = new Uri("https://html.example.com/cal/"), SupportsSyncCollection = true }, null, Ct);

        big.Error!.Code.Should().Be(CalDavErrorCodes.TooLarge);
        big.Error.Limit.Should().Be(2048);
        broken.Error!.Code.Should().Be(CalDavErrorCodes.InvalidResponse);
        html.Error!.Code.Should().Be(CalDavErrorCodes.InvalidResponse);
    }

    [Fact]
    public async Task ACalendarThatNoLongerExists_IsNotFound_AndAServerErrorCarriesItsStatus() {
        var server = new FakeDavServer();
        server.On("REPORT", "https://dav.example.com/gone/", _ => (404, ""));
        server.On("REPORT", "https://dav.example.com/sick/", _ => (503, ""));
        var client = Client(server);

        var gone = await client.SyncAsync(new CalDavCalendar { Url = new Uri("https://dav.example.com/gone/"), SupportsSyncCollection = true }, null, Ct);
        var sick = await client.SyncAsync(new CalDavCalendar { Url = new Uri("https://dav.example.com/sick/"), SupportsSyncCollection = true }, null, Ct);

        gone.Error!.Code.Should().Be(CalDavErrorCodes.NotFound);
        sick.Error!.Code.Should().Be(CalDavErrorCodes.ServerError);
        sick.Error.StatusCode.Should().Be(503);
    }

    [Fact]
    public async Task Redirects_AreFollowedWithTheSameMethodAndCredentials() {
        var server = new FakeDavServer();
        server.On("PROPFIND", "https://example.com/", _ => (404, ""));
        server.On("PROPFIND", "https://example.com/.well-known/caldav", _ => (301, ""), location: "https://dav.example.com/dav/");
        server.On("PROPFIND", "https://dav.example.com/dav/", _ => (207, Multistatus(Response("/dav/",
            "<D:current-user-principal><D:href>/dav/principals/u/</D:href></D:current-user-principal>"))));
        server.On("PROPFIND", "https://dav.example.com/dav/principals/u/", _ => (207, Multistatus(Response("/dav/principals/u/",
            "<C:calendar-home-set><D:href>/dav/calendars/u/</D:href></C:calendar-home-set>"))));
        server.On("PROPFIND", "https://dav.example.com/dav/calendars/u/", _ => (207, Multistatus(Response("/dav/calendars/u/", "<D:resourcetype><D:collection/></D:resourcetype>"))));

        var account = await Client(server).DiscoverAsync(new Uri("https://example.com/"), Ct);

        account.Value!.PrincipalUrl.Should().Be(new Uri("https://dav.example.com/dav/principals/u/"));
        server.Requests.Single(r => r.Url == "https://dav.example.com/dav/").Authorization.Should().StartWith("Basic ");
        server.Requests.Single(r => r.Url == "https://dav.example.com/dav/").Body.Should().Contain("current-user-principal");
    }

    private static string SyncStateOf(string token, Dictionary<string, string> etags) =>
        new CalDavSyncState { Token = token, ETags = etags }.Serialize();

    /// <summary>A recorded request.</summary>
    private sealed record Recorded(string Method, string Url, string? Depth, string? Authorization, string Body);

    /// <summary>Answers requests from a table of handwritten responses, and records them.</summary>
    private sealed class FakeDavServer : HttpMessageHandler {
        private readonly List<(string Method, string Url, Func<Incoming, (int Status, string Body)> Answer, string? ETag, string? Location)> _routes = [];

        public List<Recorded> Requests { get; } = [];

        public void On(string method, string url, Func<Incoming, (int Status, string Body)> answer, string? etag = null, string? location = null) =>
            _routes.Add((method, new Uri(url).AbsoluteUri, answer, etag, location));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            var url = request.RequestUri!.AbsoluteUri;
            Requests.Add(new Recorded(request.Method.Method, url, request.Headers.TryGetValues("Depth", out var d) ? d.Single() : null,
                request.Headers.Authorization?.ToString(), body));
            var route = _routes.FirstOrDefault(r => r.Method == request.Method.Method && r.Url == url);
            if (route.Answer is null) return new HttpResponseMessage(HttpStatusCode.NotFound);
            var (status, text) = route.Answer(new Incoming(request.Headers, body));
            var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(text, Encoding.UTF8, "application/xml") };
            if (route.ETag is not null && status < 300) response.Headers.ETag = EntityTagHeaderValue.Parse(route.ETag);
            if (route.Location is not null) response.Headers.Location = new Uri(route.Location);
            return response;
        }
    }

    private sealed record Incoming(HttpRequestHeaders Headers, string Body);
}
