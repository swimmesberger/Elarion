using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using Elarion.Abstractions.Identity;
using Elarion.Abstractions.Modules;
using Elarion.AspNetCore;
using Elarion.AspNetCore.ProxyIdentity;
using Elarion.Session;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elarion.Tests.ProxyIdentity;

/// <summary>
/// The reserved signed-out value of the Development user switch: the stand-in signs nobody in for the request, so the
/// application's anonymous behaviour can be seen locally, while the session section still names the switch cookies.
/// Validated proxy tokens never read it.
/// </summary>
public sealed class DevelopmentSignedOutTests {
    private const string SignedOut = ProxyIdentityDefaults.DevelopmentSignedOutUser;

    [Fact]
    public async Task UserHeader_SignsNobodyIn() {
        await using var host = await StartDevelopmentAsync();

        var probe = await ProbeAsync(host, headers: new() { [ProxyIdentityDefaults.DevelopmentUserHeader] = SignedOut });

        probe.Should().Be("authenticated=False|currentUser=False|feature=False|identity=False");
    }

    [Fact]
    public async Task UserCookie_SignsNobodyIn() {
        await using var host = await StartDevelopmentAsync();

        var probe = await ProbeAsync(host, cookies: new() { [ProxyIdentityDefaults.DevelopmentUserCookie] = Uri.EscapeDataString(SignedOut) });

        probe.Should().Be("authenticated=False|currentUser=False|feature=False|identity=False");
    }

    [Fact]
    public async Task SurroundingWhitespace_IsTrimmedLikeAnyOtherSwitchValue() {
        await using var host = await StartDevelopmentAsync();

        var probe = await ProbeAsync(host, headers: new() { [ProxyIdentityDefaults.DevelopmentUserHeader] = $"  {SignedOut} " });

        probe.Should().StartWith("authenticated=False");
    }

    [Fact]
    public async Task AuthorizedEndpoint_IsChallengedAsWithoutAnIdentity() {
        await using var host = await StartDevelopmentAsync();

        var fromHeader = await StatusAsync(host, "/secure", headers: new() { [ProxyIdentityDefaults.DevelopmentUserHeader] = SignedOut });
        var fromCookie = await StatusAsync(host, "/secure", cookies: new() { [ProxyIdentityDefaults.DevelopmentUserCookie] = SignedOut });
        var signedIn = await StatusAsync(host, "/secure");

        fromHeader.Should().Be(HttpStatusCode.Unauthorized);
        fromCookie.Should().Be(HttpStatusCode.Unauthorized);
        signedIn.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task RolesAndGroupsSwitches_AreIgnoredWhileSignedOut() {
        await using var host = await StartDevelopmentAsync();

        var probe = await ProbeAsync(host,
            headers: new() {
                [ProxyIdentityDefaults.DevelopmentUserHeader] = SignedOut,
                [ProxyIdentityDefaults.DevelopmentRolesHeader] = "admin",
                [ProxyIdentityDefaults.DevelopmentGroupsHeader] = "staff",
            },
            cookies: new() {
                [ProxyIdentityDefaults.DevelopmentRolesCookie] = "admin",
                [ProxyIdentityDefaults.DevelopmentGroupsCookie] = "staff",
            });
        var secure = await StatusAsync(host, "/secure", headers: new() {
            [ProxyIdentityDefaults.DevelopmentUserHeader] = SignedOut,
            [ProxyIdentityDefaults.DevelopmentRolesHeader] = "admin",
        });

        probe.Should().Be("authenticated=False|currentUser=False|feature=False|identity=False");
        secure.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task RemovingTheSwitch_ReturnsToTheConfiguredDeveloper() {
        await using var host = await StartDevelopmentAsync();

        var signedOut = await host.WhoAmIAsync(cookie: SignedOut, cookieName: ProxyIdentityDefaults.DevelopmentUserCookie);
        var signedIn = await host.WhoAmIAsync();

        signedOut.Status.Should().Be(HttpStatusCode.Unauthorized);
        signedIn.Status.Should().Be(HttpStatusCode.OK);
        signedIn.UserId.Should().Be("developer");
        signedIn.Roles.Should().Be("admin");
        signedIn.IsDevelopmentIdentity.Should().BeTrue();
    }

    [Fact]
    public async Task SessionSection_HasNoIdentity_ButNamesTheSwitchCookies() {
        await using var host = await StartDevelopmentAsync();

        var fromHeader = await SessionSectionAsync(host, headers: new() { [ProxyIdentityDefaults.DevelopmentUserHeader] = SignedOut });
        var fromCookie = await SessionSectionAsync(host, cookies: new() { [ProxyIdentityDefaults.DevelopmentUserCookie] = SignedOut });

        foreach (var section in new[] { fromHeader, fromCookie }) {
            section.Should().NotBeNull();
            section!.Value.TryGetProperty("email", out _).Should().BeFalse();
            section.Value.TryGetProperty("name", out _).Should().BeFalse();
            section.Value.TryGetProperty("logoutUrl", out _).Should().BeFalse();
            section.Value.GetProperty("isDevelopmentIdentity").GetBoolean().Should().BeFalse();
            section.Value.GetProperty("developmentUserCookie").GetString().Should().Be(ProxyIdentityDefaults.DevelopmentUserCookie);
            section.Value.GetProperty("developmentRolesCookie").GetString().Should().Be(ProxyIdentityDefaults.DevelopmentRolesCookie);
            section.Value.GetProperty("developmentGroupsCookie").GetString().Should().Be(ProxyIdentityDefaults.DevelopmentGroupsCookie);
        }
    }

    [Fact]
    public async Task ProxyTokens_IgnoreTheSignedOutValue() {
        var key = TestSigningKey.Create("key-a");
        var keys = new FakeKeyEndpoint();
        keys.Documents[ProxyIdentityTestHost.JwksUrl] = TestSigningKey.Jwks(key);
        await using var host = await ProxyIdentityTestHost.StartAsync(ProxyIdentityTestHost.JwksSettings(), keys, environment: "Development");
        var token = key.Mint(ProxyIdentityTestHost.Issuer, ProxyIdentityTestHost.Audience);

        var whoAmI = await host.WhoAmIAsync(headers: new Dictionary<string, string> {
            [ProxyIdentityTestHost.TokenHeader] = token,
            [ProxyIdentityDefaults.DevelopmentUserHeader] = SignedOut,
        }, cookies: new Dictionary<string, string> { [ProxyIdentityDefaults.DevelopmentUserCookie] = SignedOut });

        whoAmI.Status.Should().Be(HttpStatusCode.OK);
        whoAmI.UserId.Should().Be("user-1");
        whoAmI.Issuer.Should().Be(ProxyIdentityTestHost.Issuer);
        whoAmI.IsDevelopmentIdentity.Should().BeFalse();
    }

    [Theory]
    [InlineData("ProxyIdentity:Development:Subject")]
    [InlineData("ProxyIdentity:Development:Email")]
    [InlineData("ProxyIdentity:Development:Users:0:Subject")]
    [InlineData("ProxyIdentity:Development:Users:0:Email")]
    public async Task AStandInUserUnderTheReservedValue_IsRefusedAtStartup(string setting) {
        var settings = new Dictionary<string, string?> {
            ["ProxyIdentity:Development:Users:0:Email"] = "reader@example.com",
            [setting] = SignedOut,
        };

        var act = async () => await ProxyIdentityTestHost.StartAsync(settings, environment: "Development");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*reserved signed-out value of the user switch*");
    }

    private static Task<ProxyIdentityTestHost> StartDevelopmentAsync() {
        return ProxyIdentityTestHost.StartAsync(
            new Dictionary<string, string?> {
                ["ProxyIdentity:LogoutUrl"] = "/logout",
                ["ProxyIdentity:Development:Roles:0"] = "admin",
            },
            environment: "Development",
            services: services => {
                services.AddAuthorization();
                services.AddElarionHttpJson();
                services.AddElarionSession(ClientCapabilityManifest.Empty);
                services.AddElarionProxyIdentitySessionSection();
            },
            afterAuthentication: app => {
                app.UseAuthorization();
                app.MapElarionSession();
                app.MapGet("/secure", () => Results.Text("ok")).RequireAuthorization();
                app.MapGet("/probe", (HttpContext context, ICurrentUser user, Microsoft.Extensions.Options.IOptions<ProxyIdentityOptions> options) =>
                    Results.Text(string.Join("|",
                        $"authenticated={context.User.Identity?.IsAuthenticated is true}",
                        $"currentUser={user.IsAuthenticated}",
                        $"feature={context.Features.Get<IExternalIdentityFeature>() is not null}",
                        $"identity={ExternalIdentity.TryRead(context, options.Value) is not null}")));
            });
    }

    private static async Task<string> ProbeAsync(
        ProxyIdentityTestHost host, Dictionary<string, string>? headers = null, Dictionary<string, string>? cookies = null) {
        using var response = await SendAsync(host, "/probe", headers, cookies);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<HttpStatusCode> StatusAsync(
        ProxyIdentityTestHost host, string path, Dictionary<string, string>? headers = null, Dictionary<string, string>? cookies = null) {
        using var response = await SendAsync(host, path, headers, cookies);
        return response.StatusCode;
    }

    /// <summary>The <c>proxyIdentity</c> section of <c>GET /session</c>, or null when the snapshot omits it.</summary>
    private static async Task<JsonElement?> SessionSectionAsync(
        ProxyIdentityTestHost host, Dictionary<string, string>? headers = null, Dictionary<string, string>? cookies = null) {
        using var response = await SendAsync(host, "/session", headers, cookies);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.TryGetProperty("sections", out var sections)
            && sections.ValueKind == JsonValueKind.Object
            && sections.TryGetProperty("proxyIdentity", out var section)
                ? section.Clone()
                : null;
    }

    private static async Task<HttpResponseMessage> SendAsync(
        ProxyIdentityTestHost host, string path, Dictionary<string, string>? headers, Dictionary<string, string>? cookies) {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        foreach (var (name, value) in headers ?? new Dictionary<string, string>()) {
            request.Headers.TryAddWithoutValidation(name, value);
        }
        if (cookies is { Count: > 0 }) {
            request.Headers.Add("Cookie", string.Join("; ", cookies.Select(pair => $"{pair.Key}={pair.Value}")));
        }
        return await host.Client.SendAsync(request, TestContext.Current.CancellationToken);
    }
}
