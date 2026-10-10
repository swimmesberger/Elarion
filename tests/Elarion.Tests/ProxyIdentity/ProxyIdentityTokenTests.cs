using System.Net;
using AwesomeAssertions;
using Xunit;

namespace Elarion.Tests.ProxyIdentity;

/// <summary>
/// Request-level validation of the proxy's token over a real Kestrel host: where the token is read from, which tokens
/// are refused, and what the application sees through <c>ICurrentUser</c> and <c>ExternalIdentity</c>.
/// </summary>
public sealed class ProxyIdentityTokenTests {
    private const string Issuer = ProxyIdentityTestHost.Issuer;
    private const string Audience = ProxyIdentityTestHost.Audience;

    private readonly TestSigningKey _key = TestSigningKey.Create("key-a");

    [Fact]
    public async Task HeaderToken_SignsIn_WithTheTokenClaimNames() {
        await using var host = await StartAsync();
        var token = _key.Mint(Issuer, Audience, new Dictionary<string, object> {
            ["sub"] = "user-1",
            ["email"] = "user@example.com",
            ["email_verified"] = true,
            ["name"] = "A User",
            ["roles"] = new[] { "admin", "editor" },
            ["groups"] = new[] { "staff" },
        });

        var whoAmI = await host.WhoAmIAsync(header: token);

        whoAmI.Status.Should().Be(HttpStatusCode.OK);
        whoAmI.UserId.Should().Be("user-1");
        whoAmI.Email.Should().Be("user@example.com");
        whoAmI.Roles.Should().Be("admin,editor");
        whoAmI.Issuer.Should().Be(Issuer);
        whoAmI.EmailVerified.Should().BeTrue();
        whoAmI.Name.Should().Be("A User");
        whoAmI.Groups.Should().Be("staff");
        whoAmI.IsDevelopmentIdentity.Should().BeFalse();
    }

    [Fact]
    public async Task SingleStringRole_IsReadLikeAnArray() {
        await using var host = await StartAsync();
        var token = _key.Mint(Issuer, Audience, new Dictionary<string, object> { ["sub"] = "user-1", ["roles"] = "admin" });

        var whoAmI = await host.WhoAmIAsync(header: token);

        whoAmI.Roles.Should().Be("admin");
    }

    [Fact]
    public async Task BearerPrefix_IsStripped() {
        await using var host = await StartAsync();

        var whoAmI = await host.WhoAmIAsync(header: "Bearer " + _key.Mint(Issuer, Audience));

        whoAmI.Status.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Cookie_IsReadWhenTheHeaderIsAbsent() {
        await using var host = await StartAsync();

        var whoAmI = await host.WhoAmIAsync(cookie: _key.Mint(Issuer, Audience));

        whoAmI.Status.Should().Be(HttpStatusCode.OK);
        whoAmI.UserId.Should().Be("user-1");
    }

    [Fact]
    public async Task StandardAuthorizationHeader_IsIgnoredWhenNotConfigured() {
        await using var host = await StartAsync();

        var whoAmI = await host.WhoAmIAsync(header: "Bearer " + _key.Mint(Issuer, Audience), headerName: "Authorization");

        whoAmI.Status.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AuthorizationHeader_IsReadWhenConfigured() {
        var settings = ProxyIdentityTestHost.JwksSettings();
        settings["ProxyIdentity:TokenHeader"] = "Authorization";
        await using var host = await StartAsync(settings);

        var whoAmI = await host.WhoAmIAsync(header: "Bearer " + _key.Mint(Issuer, Audience), headerName: "Authorization");

        whoAmI.Status.Should().Be(HttpStatusCode.OK);
    }

    public static TheoryData<string> RefusedTokens() {
        return ["wrong audience", "no audience", "wrong issuer", "expired", "unknown key", "unsigned", "garbage"];
    }

    [Theory]
    [MemberData(nameof(RefusedTokens))]
    public async Task RefusedToken_LeavesTheRequestAnonymous(string kind) {
        await using var host = await StartAsync();
        var token = kind switch {
            "wrong audience" => _key.Mint(Issuer, "another-app"),
            "no audience" => _key.Mint(Issuer, audience: null),
            "wrong issuer" => _key.Mint("https://other-issuer.example", Audience),
            "expired" => _key.Mint(Issuer, Audience, expires: DateTime.UtcNow.AddMinutes(-2)),
            "unknown key" => TestSigningKey.Create("key-z").Mint(Issuer, Audience),
            "unsigned" => Unsigned(),
            _ => "not-a-token",
        };

        var whoAmI = await host.WhoAmIAsync(header: token);

        whoAmI.Status.Should().Be(HttpStatusCode.Unauthorized, kind);
    }

    [Fact]
    public async Task ExpiredWithinTheClockSkew_IsAccepted() {
        await using var host = await StartAsync();

        var whoAmI = await host.WhoAmIAsync(header: _key.Mint(Issuer, Audience, expires: DateTime.UtcNow.AddSeconds(-20)));

        whoAmI.Status.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task CommaSeparatedAudiences_AreEachAccepted() {
        var settings = ProxyIdentityTestHost.JwksSettings();
        settings["ProxyIdentity:Audiences:0"] = "first-app, second-app";
        await using var host = await StartAsync(settings);

        (await host.WhoAmIAsync(header: _key.Mint(Issuer, "first-app"))).Status.Should().Be(HttpStatusCode.OK);
        (await host.WhoAmIAsync(header: _key.Mint(Issuer, "second-app"))).Status.Should().Be(HttpStatusCode.OK);
        (await host.WhoAmIAsync(header: _key.Mint(Issuer, Audience))).Status.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AllowAnyAudience_AcceptsAnyAudienceOfTheIssuer() {
        var settings = ProxyIdentityTestHost.JwksSettings();
        settings["ProxyIdentity:Audiences:0"] = null;
        settings["ProxyIdentity:AllowAnyAudience"] = "true";
        await using var host = await StartAsync(settings);

        (await host.WhoAmIAsync(header: _key.Mint(Issuer, "another-app"))).Status.Should().Be(HttpStatusCode.OK);
        (await host.WhoAmIAsync(header: _key.Mint("https://other-issuer.example", "another-app"))).Status
            .Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("Claim", true, true)]
    [InlineData("Claim", false, false)]
    [InlineData("Trusted", false, true)]
    [InlineData("None", true, false)]
    public async Task EmailTrust_DecidesWhetherTheAddressIsVerified(string trust, bool claimed, bool expected) {
        var settings = ProxyIdentityTestHost.JwksSettings();
        settings["ProxyIdentity:EmailTrust"] = trust;
        await using var host = await StartAsync(settings);
        var token = _key.Mint(Issuer, Audience, new Dictionary<string, object> {
            ["sub"] = "user-1", ["email"] = "user@example.com", ["email_verified"] = claimed,
        });

        var whoAmI = await host.WhoAmIAsync(header: token);

        whoAmI.EmailVerified.Should().Be(expected);
    }

    [Fact]
    public async Task CustomClaimNames_DriveTheCurrentUser() {
        var settings = ProxyIdentityTestHost.JwksSettings();
        settings["ProxyIdentity:Claims:Subject"] = "oid";
        settings["ProxyIdentity:Claims:Email"] = "upn";
        settings["ProxyIdentity:Claims:Roles"] = "app_roles";
        await using var host = await StartAsync(settings);
        var token = _key.Mint(Issuer, Audience, new Dictionary<string, object> {
            ["sub"] = "pairwise", ["oid"] = "object-1", ["upn"] = "user@example.com", ["app_roles"] = new[] { "reader" },
        });

        var whoAmI = await host.WhoAmIAsync(header: token);

        whoAmI.UserId.Should().Be("object-1");
        whoAmI.Email.Should().Be("user@example.com");
        whoAmI.Roles.Should().Be("reader");
    }

    private string Unsigned() {
        var signed = _key.Mint(Issuer, Audience);
        var parts = signed.Split('.');
        var header = Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode("""{"alg":"none","typ":"JWT"}""");
        return $"{header}.{parts[1]}.";
    }

    private async Task<ProxyIdentityTestHost> StartAsync(Dictionary<string, string?>? settings = null) {
        var keys = new FakeKeyEndpoint();
        keys.Documents[ProxyIdentityTestHost.JwksUrl] = TestSigningKey.Jwks(_key);
        return await ProxyIdentityTestHost.StartAsync(settings ?? ProxyIdentityTestHost.JwksSettings(), keys);
    }
}
