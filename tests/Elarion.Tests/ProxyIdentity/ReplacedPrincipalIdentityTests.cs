using System.Net;
using System.Security.Claims;
using System.Text.Json;
using AwesomeAssertions;
using Elarion.Abstractions.Modules;
using Elarion.AspNetCore;
using Elarion.AspNetCore.ProxyIdentity;
using Elarion.Session;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Elarion.Tests.ProxyIdentity;

/// <summary>
/// An application that replaces the proxy's principal after authentication — with a local account's principal, or
/// with an anonymous one for a person it has no account for — still learns who the proxy signed in: the identity is
/// captured when the token (or the stand-in) is accepted, and the <c>proxyIdentity</c> session section reads it there.
/// </summary>
public sealed class ReplacedPrincipalIdentityTests {
    private const string Issuer = ProxyIdentityTestHost.Issuer;

    private readonly TestSigningKey _key = TestSigningKey.Create("key-a");

    [Fact]
    public async Task StandIn_SessionSection_KeepsTheIdentity_AfterTheApplicationSignsInALocalAccount() {
        await using var host = await StartDevelopmentAsync(LocalAccount);

        var section = await SessionSectionAsync(host);

        section.Should().NotBeNull();
        section!.Value.GetProperty("email").GetString().Should().Be("developer@example.com");
        section.Value.GetProperty("name").GetString().Should().Be("Developer");
        section.Value.TryGetProperty("logoutUrl", out _).Should().BeFalse();
        section.Value.GetProperty("isDevelopmentIdentity").GetBoolean().Should().BeTrue();
        section.Value.GetProperty("developmentUserCookie").GetString().Should().Be(ProxyIdentityDefaults.DevelopmentUserCookie);
        section.Value.GetProperty("developmentRolesCookie").GetString().Should().Be(ProxyIdentityDefaults.DevelopmentRolesCookie);
        section.Value.GetProperty("developmentGroupsCookie").GetString().Should().Be(ProxyIdentityDefaults.DevelopmentGroupsCookie);
    }

    [Fact]
    public async Task StandIn_SessionSection_NamesTheSwitchCookies_ForAPersonTheApplicationTreatsAsAnonymous() {
        await using var host = await StartDevelopmentAsync(_ => new ClaimsPrincipal(new ClaimsIdentity()));

        var section = await SessionSectionAsync(host, user: "new.person@example.com");

        section.Should().NotBeNull();
        section!.Value.GetProperty("email").GetString().Should().Be("new.person@example.com");
        section.Value.GetProperty("isDevelopmentIdentity").GetBoolean().Should().BeTrue();
        section.Value.GetProperty("developmentUserCookie").GetString().Should().Be(ProxyIdentityDefaults.DevelopmentUserCookie);
        section.Value.GetProperty("developmentRolesCookie").GetString().Should().Be(ProxyIdentityDefaults.DevelopmentRolesCookie);
        section.Value.GetProperty("developmentGroupsCookie").GetString().Should().Be(ProxyIdentityDefaults.DevelopmentGroupsCookie);
    }

    [Fact]
    public async Task StandIn_TryReadFromTheRequest_ReturnsTheStandIn_AfterThePrincipalIsReplaced() {
        await using var host = await StartDevelopmentAsync(LocalAccount);

        var identity = await IdentityAsync(host);

        identity.Status.Should().Be(HttpStatusCode.OK);
        identity.Body.Should().Be($"{ProxyIdentityDefaults.DevelopmentIssuer}|developer|developer@example.com|True|fromPrincipal=False");
    }

    [Fact]
    public async Task Token_TryReadFromTheRequest_ReturnsTheValidatedIdentity_AfterThePrincipalIsReplaced() {
        await using var host = await StartWithTokensAsync(LocalAccount);
        var token = _key.Mint(Issuer, ProxyIdentityTestHost.Audience, new Dictionary<string, object> {
            ["sub"] = "user-1",
            ["email"] = "user@example.com",
            ["email_verified"] = true,
        });

        var identity = await IdentityAsync(host, token);

        identity.Status.Should().Be(HttpStatusCode.OK);
        identity.Body.Should().Be($"{Issuer}|user-1|user@example.com|False|fromPrincipal=False");
    }

    [Fact]
    public async Task Token_SessionSection_KeepsTheIdentity_ButNamesNoSwitchCookies() {
        await using var host = await StartWithTokensAsync(_ => new ClaimsPrincipal(new ClaimsIdentity()));
        var token = _key.Mint(Issuer, ProxyIdentityTestHost.Audience, new Dictionary<string, object> {
            ["sub"] = "user-1",
            ["email"] = "user@example.com",
            ["name"] = "A User",
        });

        var section = await SessionSectionAsync(host, token: token);

        section.Should().NotBeNull();
        section!.Value.GetProperty("email").GetString().Should().Be("user@example.com");
        section.Value.GetProperty("name").GetString().Should().Be("A User");
        section.Value.GetProperty("isDevelopmentIdentity").GetBoolean().Should().BeFalse();
        section.Value.TryGetProperty("developmentUserCookie", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Token_WithoutAToken_CapturesNothing() {
        await using var host = await StartWithTokensAsync(LocalAccount);

        var identity = await IdentityAsync(host);
        var section = await SessionSectionAsync(host);

        identity.Status.Should().Be(HttpStatusCode.Unauthorized);
        section.Should().BeNull();
    }

    /// <summary>A local account's principal: the mapping's claim names, but no issuer, so it is not a proxy identity.</summary>
    private static ClaimsPrincipal LocalAccount(HttpContext context) {
        return new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", "account-1"), new Claim("email", "account@example.com")], "local", "sub", "roles"));
    }

    private static Task<ProxyIdentityTestHost> StartDevelopmentAsync(Func<HttpContext, ClaimsPrincipal> replace) {
        return ProxyIdentityTestHost.StartAsync(
            new Dictionary<string, string?> { ["ProxyIdentity:LogoutUrl"] = "/logout" },
            environment: "Development",
            services: AddSession,
            afterAuthentication: app => Configure(app, replace));
    }

    private Task<ProxyIdentityTestHost> StartWithTokensAsync(Func<HttpContext, ClaimsPrincipal> replace) {
        var keys = new FakeKeyEndpoint();
        keys.Documents[ProxyIdentityTestHost.JwksUrl] = TestSigningKey.Jwks(_key);
        return ProxyIdentityTestHost.StartAsync(
            ProxyIdentityTestHost.JwksSettings(),
            keys,
            services: AddSession,
            afterAuthentication: app => Configure(app, replace));
    }

    private static void AddSession(IServiceCollection services) {
        services.AddElarionHttpJson();
        services.AddElarionSession(ClientCapabilityManifest.Empty);
        services.AddElarionProxyIdentitySessionSection();
    }

    /// <summary>
    /// Replaces an authenticated principal the way an application does after linking the person to a local account,
    /// and maps the session snapshot plus <c>/identity</c>, which answers what <c>TryRead(HttpContext)</c> returns.
    /// </summary>
    private static void Configure(WebApplication app, Func<HttpContext, ClaimsPrincipal> replace) {
        app.Use(async (context, next) => {
            if (context.User.Identity?.IsAuthenticated is true) {
                context.User = replace(context);
            }
            await next(context);
        });
        app.MapElarionSession();
        app.MapGet("/identity", (HttpContext context, IOptions<ProxyIdentityOptions> options) => {
            if (ExternalIdentity.TryRead(context, options.Value) is not { } identity) {
                return Results.StatusCode(StatusCodes.Status401Unauthorized);
            }
            var fromPrincipal = ExternalIdentity.TryRead(context.User, options.Value) is not null;
            return Results.Text(string.Join("|",
                identity.Issuer, identity.Subject, identity.Email, identity.IsDevelopmentIdentity, $"fromPrincipal={fromPrincipal}"));
        });
    }

    private static async Task<(HttpStatusCode Status, string Body)> IdentityAsync(ProxyIdentityTestHost host, string? token = null) {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/identity");
        if (token is not null) {
            request.Headers.TryAddWithoutValidation(ProxyIdentityTestHost.TokenHeader, token);
        }
        using var response = await host.Client.SendAsync(request, TestContext.Current.CancellationToken);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>The <c>proxyIdentity</c> section of <c>GET /session</c>, or null when the snapshot omits it.</summary>
    private static async Task<JsonElement?> SessionSectionAsync(ProxyIdentityTestHost host, string? token = null, string? user = null) {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/session");
        if (token is not null) {
            request.Headers.TryAddWithoutValidation(ProxyIdentityTestHost.TokenHeader, token);
        }
        if (user is not null) {
            request.Headers.TryAddWithoutValidation(ProxyIdentityDefaults.DevelopmentUserHeader, user);
        }
        using var response = await host.Client.SendAsync(request, TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return document.RootElement.TryGetProperty("sections", out var sections)
            && sections.ValueKind == JsonValueKind.Object
            && sections.TryGetProperty("proxyIdentity", out var section)
                ? section.Clone()
                : null;
    }
}
