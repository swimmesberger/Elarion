using System.Net;
using AwesomeAssertions;
using Elarion.WebDav;
using Elarion.WebDav.AspNetCore;
using Xunit;

namespace Elarion.Tests.CalDav;

/// <summary>The ASP.NET host (spec 0006 FR-014–FR-016, FR-033, FR-034, FR-036) and "Add an account in a calendar app".</summary>
public sealed class CalDavHostTests {
    private const string PropFindPrincipal =
        $"<D:propfind {CalDavTestHarness.Namespaces}><D:prop><D:current-user-principal/><D:resourcetype/></D:prop></D:propfind>";

    [Fact]
    public async Task GivenARequestWithoutCredentials_ThenTheAnswerIs401WithABasicChallenge() {
        await using var host = await CalDavHost.StartAsync();

        var response = await host.SendAsync("PROPFIND", "/dav/", PropFindPrincipal, depth: "0");

        response.Status.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers["WWW-Authenticate"].Should().Be("Basic realm=\"DAV\", charset=\"UTF-8\"");
    }

    [Theory]
    [InlineData("PROPFIND")]
    [InlineData("GET")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    public async Task WellKnownCalDav_RedirectsWith301ToThePrefix(string method) {
        await using var host = await CalDavHost.StartAsync();

        var response = await host.SendAsync(method, "/.well-known/caldav", method == "PROPFIND" ? PropFindPrincipal : null);

        response.Status.Should().Be(HttpStatusCode.MovedPermanently);
        response.Headers["Location"].Should().Be("/dav/");
    }

    [Fact]
    public async Task Options_AnswersTheDavHeaderAndNoWriteMethod_AndNoScheduling() {
        await using var host = await CalDavHost.StartAsync();
        var password = await host.CreatePasswordAsync("alice");

        var response = await host.SendAsync("OPTIONS", "/dav/principals/alice/", user: "alice", password: password);

        response.Status.Should().Be(HttpStatusCode.OK);
        response.Headers["DAV"].Should().Be("1, 3, calendar-access").And.NotContain("schedule");
        response.Headers["Allow"].Should().Be(DavDispatcher.AllowedMethods);
    }

    [Fact]
    public async Task ABrowserSession_DoesNotAuthenticateADavRequest_AndAnAppPasswordNoOtherEndpoint() {
        await using var host = await CalDavHost.StartAsync();
        var password = await host.CreatePasswordAsync("alice");

        var browserOnDav = await host.SendAsync("PROPFIND", "/dav/", PropFindPrincipal, depth: "0",
            headers: new Dictionary<string, string> { [CalDavHost.BrowserUserHeader] = "alice" });
        var passwordOnRpc = await host.SendAsync("POST", "/rpc", "{}", user: "alice", password: password);
        var browserOnRpc = await host.SendAsync("POST", "/rpc", "{}", headers: new Dictionary<string, string> { [CalDavHost.BrowserUserHeader] = "alice" });

        browserOnDav.Status.Should().Be(HttpStatusCode.Unauthorized);
        passwordOnRpc.Status.Should().Be(HttpStatusCode.Unauthorized);
        browserOnRpc.Status.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task TheDavRoutes_AreExemptFromCrossOriginProtection_WhileOtherRoutesAreNot() {
        await using var host = await CalDavHost.StartAsync();
        var password = await host.CreatePasswordAsync("alice");
        var crossSite = new Dictionary<string, string> { ["Origin"] = "https://evil.example", ["Sec-Fetch-Site"] = "cross-site" };

        var dav = await host.SendAsync("PROPFIND", "/dav/", PropFindPrincipal, user: "alice", password: password, depth: "0", headers: crossSite);
        var rpc = await host.SendAsync("POST", "/rpc", "{}",
            headers: new Dictionary<string, string>(crossSite) { [CalDavHost.BrowserUserHeader] = "alice" });

        dav.Status.Should().Be((HttpStatusCode)207);
        rpc.Status.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ARevokedOrDeactivatedCredential_IsRefusedWith401() {
        await using var host = await CalDavHost.StartAsync();
        var deactivated = await host.CreatePasswordAsync("deactivated");

        (await host.SendAsync("PROPFIND", "/dav/", PropFindPrincipal, user: "x", password: deactivated, depth: "0")).Status
            .Should().Be(HttpStatusCode.Unauthorized);
        (await host.SendAsync("PROPFIND", "/dav/", PropFindPrincipal, user: "x", password: "elap_unknown", depth: "0")).Status
            .Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task TheDispatchScope_IsSeededWithTheDavPrincipal_SoSourcesSeeIt() {
        await using var host = await CalDavHost.StartAsync();
        var password = await host.CreatePasswordAsync("bob");

        var home = await host.SendAsync("PROPFIND", "/dav/calendars/bob/", PropFindPrincipal, user: "bob", password: password, depth: "1");

        home.Dav.Responses.Keys.Should().Contain("/dav/calendars/bob/shifts.u-bob/", "the source ran as bob");
        home.Dav.Prop("/dav/calendars/bob/", DavNames.D.CurrentUserPrincipal)!.Child(DavNames.D.Href)!.Text.Should().Be("/dav/principals/bob/");
    }

    [Fact]
    public async Task AWriteMethod_Answers403NeedPrivilegesOverHttp() {
        await using var host = await CalDavHost.StartAsync();
        var password = await host.CreatePasswordAsync("alice");

        var response = await host.SendAsync("PUT", "/dav/calendars/alice/shifts.u-alice/new.ics", "BEGIN:VCALENDAR\r\nEND:VCALENDAR\r\n",
            user: "alice", password: password, headers: new Dictionary<string, string> { ["If-None-Match"] = "*" });

        response.Status.Should().Be(HttpStatusCode.Forbidden);
        response.Dav.Xml.Child(DavNames.D.NeedPrivileges).Should().NotBeNull();
        response.Headers.Should().NotContainKey("WWW-Authenticate");
    }

    [Fact]
    public void TheDefaultScheme_IsTheAppPasswordScheme() {
        WebDavHostOptions.AppPasswordScheme.Should().Be(Elarion.AppPasswords.AspNetCore.AppPasswordAuthenticationDefaults.AuthenticationScheme);
        new WebDavHostOptions().AuthenticationSchemes.Should().Equal(WebDavHostOptions.AppPasswordScheme);
    }
}
