using System.Net;
using System.Text;
using AwesomeAssertions;
using Elarion.Tests.CardDav;
using Elarion.Tests.WebDav;
using Elarion.WebDav;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elarion.Tests.CalDav;

/// <summary>
/// HTTP-level WebDAV cases ported from other servers' and clients' suites (Apple ccs-caldavtester, litmus, sabre/dav,
/// Radicale, Cyrus, DAVx5, python-caldav) against the Kestrel host serving calendars and address books: discovery,
/// authentication answers, HEAD and how a request path is decoded.
/// </summary>
public sealed class CalDavHostConformanceTests {
    private const string PropFindResourceType = "<D:propfind xmlns:D=\"DAV:\"><D:prop><D:resourcetype/></D:prop></D:propfind>";

    /// <summary>
    /// Apple well-known.xml (CalDAV and CardDAV) "Simple GET tests" 3, 4 and PROPFIND 3, 4, 6 (D-01–D-04, D-06), Radicale
    /// test_well_known (D01), DAVx5 "followRedirects" (DI-08): the well-known URIs with and without a trailing slash
    /// redirect every method to the prefix, before authentication and before any body or Depth is looked at.
    /// </summary>
    [Theory]
    [InlineData("/.well-known/caldav/", "GET", null, null)]
    [InlineData("/.well-known/caldav/", "PROPFIND", "0", null)]
    [InlineData("/.well-known/caldav/", "PROPFIND", "1", PropFindResourceType)]
    [InlineData("/.well-known/caldav", "PROPFIND", "1", PropFindResourceType)]
    [InlineData("/.well-known/caldav/", "OPTIONS", null, null)]
    [InlineData("/.well-known/caldav", "REPORT", "1", "<D:sync-collection xmlns:D=\"DAV:\"/>")]
    [InlineData("/.well-known/caldav", "PUT", null, "x")]
    [InlineData("/.well-known/caldav", "DELETE", null, null)]
    [InlineData("/.well-known/caldav", "PROPPATCH", null, "<D:propertyupdate xmlns:D=\"DAV:\"/>")]
    [InlineData("/.well-known/caldav", "MKCOL", null, null)]
    [InlineData("/.well-known/carddav/", "GET", null, null)]
    [InlineData("/.well-known/carddav/", "HEAD", null, null)]
    [InlineData("/.well-known/carddav/", "PROPFIND", "1", PropFindResourceType)]
    [InlineData("/.well-known/carddav", "REPORT", "0", "<D:sync-collection xmlns:D=\"DAV:\"/>")]
    public async Task TheWellKnownUris_RedirectEveryMethodWithOrWithoutTrailingSlash_WithoutCredentials(string path, string method, string? depth,
        string? body) {
        await using var host = await CardDavHost.StartAsync();

        var response = await host.SendAsync(method, path, body, depth: depth);

        response.Status.Should().Be(HttpStatusCode.MovedPermanently);
        response.Headers["Location"].Should().Be("/dav/");
    }

    /// <summary>
    /// Apple well-known.xml GET 1–2 and PROPFIND 1–2 (D-05), Radicale test_well_known_not_found (D02): the bare
    /// well-known path and other names below it are not Elarion's; they answer 404, never 5xx or a redirect loop.
    /// </summary>
    [Theory]
    [InlineData("GET", "/.well-known")]
    [InlineData("GET", "/.well-known/")]
    [InlineData("PROPFIND", "/.well-known/")]
    [InlineData("GET", "/.well-known/foo")]
    [InlineData("PROPFIND", "/.well-known/caldavx")]
    public async Task TheBareWellKnownPathAndOtherNames_Answer404(string method, string path) {
        await using var host = await CardDavHost.StartAsync();

        var response = await host.SendAsync(method, path, depth: method == "PROPFIND" ? "0" : null);

        response.Status.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// Apple errors.xml "Unauthenticated versus Forbidden" 1, 2 (D-13, D-14), Cyrus header_cache_control (W12): a wrong
    /// password for a known user and a password for a user nobody knows get the same 401 with the same challenge (no
    /// user enumeration), and a write without credentials is 401 before the read-only 403.
    /// </summary>
    [Fact]
    public async Task AWrongPasswordAndAnUnknownUser_GetTheSame401_AndAnUnauthenticatedWriteIs401() {
        await using var host = await CardDavHost.StartAsync();
        await host.CreatePasswordAsync("alice");

        var wrongPassword = await host.SendAsync("GET", "/dav/calendars/alice/shifts.u-alice/x.ics", user: "alice", password: "elap_wrong");
        var unknownUser = await host.SendAsync("GET", "/dav/calendars/nobody/shifts.u-nobody/x.ics", user: "nobody", password: "elap_wrong");
        var write = await host.SendAsync("PUT", "/dav/addressbooks/alice/customers.all/new.vcf", "BEGIN:VCARD\r\nEND:VCARD\r\n");

        wrongPassword.Status.Should().Be(HttpStatusCode.Unauthorized);
        unknownUser.Status.Should().Be(HttpStatusCode.Unauthorized);
        wrongPassword.Headers["WWW-Authenticate"].Should().Be(unknownUser.Headers["WWW-Authenticate"]);
        wrongPassword.Dav.Body.Should().Be(unknownUser.Dav.Body);
        write.Status.Should().Be(HttpStatusCode.Unauthorized);
        write.Headers["WWW-Authenticate"].Should().StartWith("Basic ");
    }

    /// <summary>DAVx5 "propfind sends proper request" (DI-22): a multi-valued Accept on a PROPFIND is answered 207, never 406.</summary>
    [Fact]
    public async Task APropFindWithAMultiValuedAccept_Answers207() {
        await using var host = await CardDavHost.StartAsync();
        var password = await host.CreatePasswordAsync("alice");

        var response = await host.SendAsync("PROPFIND", "/dav/principals/alice/", PropFindResourceType, "alice", password, "0",
            new Dictionary<string, string> { ["Accept"] = "application/xml, text/xml" });

        response.Status.Should().Be((HttpStatusCode)207);
        response.Headers["Content-Type"].Should().StartWith("application/xml");
    }

    /// <summary>
    /// litmus common.c get_etag (G-10), sabre/dav HttpHeadTest testHEAD and testDoubleAuth (W30, W32), Radicale test_head
    /// (D04): HEAD on a member answers the GET's status, ETag, type and length with an empty body.
    /// </summary>
    [Fact]
    public async Task HeadOnAContact_AnswersTheGetsHeadersWithoutABody() {
        await using var host = await CardDavHost.StartAsync();
        var password = await host.CreatePasswordAsync("alice");
        await host.PublishContactAsync("customers", "all", CardDavTestData.Person("c-1", "Anna", "Berger"));
        const string path = "/dav/addressbooks/alice/customers.all/c-1.vcf";

        var get = await host.SendAsync("GET", path, user: "alice", password: password);
        var head = await host.SendAsync("HEAD", path, user: "alice", password: password);

        head.Status.Should().Be(HttpStatusCode.OK);
        head.Headers["ETag"].Should().Be(get.Headers["ETag"]);
        head.Headers["Content-Type"].Should().Be(get.Headers["Content-Type"]);
        head.Headers["Content-Length"].Should().Be(get.Dav.Bytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        head.Dav.Bytes.Should().BeEmpty();
    }

    /// <summary>
    /// Apple encodedURIs.xml double-encoded 3, 9 (M-09), vdirsyncer test_normalize_href (RP-27), python-caldav
    /// TestASlashInTheName "decoded once" (RP-28): over HTTP a request path is decoded exactly once, end to end, so
    /// <c>3%2520a.txt</c> reaches the member literally named <c>3%20a.txt</c>, not <c>3 a.txt</c>.
    /// </summary>
    [Fact]
    public async Task ARequestPath_IsDecodedExactlyOnceOverHttp() {
        var things = new TestThingsProtocol();
        things.Show("alice", "s", "k");
        await using var host = await CalDavHost.StartAsync(services => services.AddSingleton<IDavProtocol>(things));
        var password = await host.CreatePasswordAsync("alice");
        await PublishAsync(host, "3 a", "space");
        await PublishAsync(host, "3%20a", "literal");

        var literal = await host.SendAsync("GET", "/dav/things/alice/s.k/3%2520a.txt", user: "alice", password: password);
        var space = await host.SendAsync("GET", "/dav/things/alice/s.k/3%20a.txt", user: "alice", password: password);

        space.Dav.Body.Should().Be("space");
        literal.Status.Should().Be(HttpStatusCode.OK);
        literal.Dav.Body.Should().Be("literal");
    }

    /// <summary>
    /// litmus basic.c put_get_utf8_segment (X-13), Cyrus utf8_url (D22), python-caldav TestAnHrefInAReportBody and
    /// caldav-server-tester "robur %40" (MG-06): a UTF-8 or <c>%40</c> segment sent over HTTP reaches the member.
    /// </summary>
    [Theory]
    [InlineData("€", "%E2%82%AC.txt")]
    [InlineData("x@y", "x%40y.txt")]
    [InlineData("x@y", "x@y.txt")]
    [InlineData("a b", "a%20b.txt")]
    public async Task AnEncodedSegment_ReachesTheMemberOverHttp(string uid, string segment) {
        var things = new TestThingsProtocol();
        things.Show("alice", "s", "k");
        await using var host = await CalDavHost.StartAsync(services => services.AddSingleton<IDavProtocol>(things));
        var password = await host.CreatePasswordAsync("alice");
        await PublishAsync(host, uid, "body of " + uid);

        var response = await host.SendAsync("GET", "/dav/things/alice/s.k/" + segment, user: "alice", password: password);

        response.Status.Should().Be(HttpStatusCode.OK);
        response.Dav.Body.Should().Be("body of " + uid);
    }

    private static async Task PublishAsync(CalDavHost host, string uid, string text) {
        await using var scope = host.App.Services.CreateAsyncScope();
        var bytes = Encoding.UTF8.GetBytes(text);
        await scope.ServiceProvider.GetRequiredService<IDavCollectionStore>().PublishAsync(TestThingsProtocol.Address("s", "k"), new DavObjectContent {
            Name = uid + ".txt", Uid = uid, Content = bytes, ContentType = "text/plain; charset=utf-8", ETag = DavETag.Compute(bytes),
        }, TestContext.Current.CancellationToken);
    }
}
