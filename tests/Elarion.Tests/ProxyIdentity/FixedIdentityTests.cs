using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using Elarion.Abstractions.Modules;
using Elarion.AspNetCore;
using Elarion.AspNetCore.ProxyIdentity;
using Elarion.Session;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elarion.Tests.ProxyIdentity;

/// <summary>
/// The fixed identity: every request is signed in as one configured user in any environment, with no switch and no
/// proxy token able to change who that is.
/// </summary>
public sealed class FixedIdentityTests {
    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public async Task EveryRequest_SignsInAsTheFixedUser(string environment) {
        await using var host = await StartAsync(environment);

        var whoAmI = await host.WhoAmIAsync();

        whoAmI.Status.Should().Be(HttpStatusCode.OK);
        whoAmI.UserId.Should().Be("household");
        whoAmI.Email.Should().Be("home@example.com");
        whoAmI.Roles.Should().Be("admin");
        whoAmI.Groups.Should().Be("family");
        whoAmI.Name.Should().Be("Household");
        whoAmI.Issuer.Should().Be(ProxyIdentityDefaults.FixedIssuer);
        whoAmI.EmailVerified.Should().BeTrue();
        whoAmI.IsDevelopmentIdentity.Should().BeFalse();
    }

    [Fact]
    public async Task WithoutAnEmail_SignsInWithoutOne() {
        await using var host = await ProxyIdentityTestHost.StartAsync(new Dictionary<string, string?> {
            ["ProxyIdentity:Fixed:Enabled"] = "true",
            ["ProxyIdentity:Fixed:Subject"] = "household",
        });

        var whoAmI = await host.WhoAmIAsync();

        whoAmI.Status.Should().Be(HttpStatusCode.OK);
        whoAmI.UserId.Should().Be("household");
        whoAmI.Email.Should().BeEmpty();
        whoAmI.EmailVerified.Should().BeFalse();
    }

    [Fact]
    public async Task DevelopmentSwitches_AreIgnored() {
        await using var host = await StartAsync("Development");

        var whoAmI = await host.WhoAmIAsync(
            headers: new Dictionary<string, string> {
                [ProxyIdentityDefaults.DevelopmentUserHeader] = ProxyIdentityDefaults.DevelopmentSignedOutUser,
                [ProxyIdentityDefaults.DevelopmentRolesHeader] = "owner",
                [ProxyIdentityDefaults.DevelopmentGroupsHeader] = "staff",
            },
            cookies: new Dictionary<string, string> {
                [ProxyIdentityDefaults.DevelopmentUserCookie] = Uri.EscapeDataString("someone@example.com"),
                [ProxyIdentityDefaults.DevelopmentRolesCookie] = "owner",
            });

        whoAmI.Status.Should().Be(HttpStatusCode.OK);
        whoAmI.UserId.Should().Be("household");
        whoAmI.Roles.Should().Be("admin");
        whoAmI.Groups.Should().Be("family");
    }

    [Fact]
    public async Task ProxyTokens_AreNotRead() {
        await using var host = await StartAsync("Production");
        var token = TestSigningKey.Create("key-a").Mint(
            ProxyIdentityTestHost.Issuer, ProxyIdentityTestHost.Audience, new Dictionary<string, object> { ["sub"] = "intruder" });

        var whoAmI = await host.WhoAmIAsync(header: token, headerName: "X-Proxy-Token");

        whoAmI.UserId.Should().Be("household");
        whoAmI.Issuer.Should().Be(ProxyIdentityDefaults.FixedIssuer);
    }

    [Fact]
    public async Task SessionSection_FlagsTheFixedIdentity_WithoutLogoutOrSwitchCookies() {
        await using var host = await ProxyIdentityTestHost.StartAsync(
            Settings(new Dictionary<string, string?> {
                ["ProxyIdentity:LogoutUrl"] = "/logout",
                ["ProxyIdentity:TokenHeader"] = "X-Proxy-Token",
            }),
            services: services => {
                services.AddAuthorization();
                services.AddElarionHttpJson();
                services.AddElarionSession(ClientCapabilityManifest.Empty);
                services.AddElarionProxyIdentitySessionSection();
            },
            afterAuthentication: app => {
                app.UseAuthorization();
                app.MapElarionSession();
            });

        using var response = await host.Client.GetAsync("/session", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        var section = document.RootElement.GetProperty("sections").GetProperty("proxyIdentity");
        section.GetProperty("email").GetString().Should().Be("home@example.com");
        section.GetProperty("name").GetString().Should().Be("Household");
        section.GetProperty("isFixedIdentity").GetBoolean().Should().BeTrue();
        section.GetProperty("isDevelopmentIdentity").GetBoolean().Should().BeFalse();
        section.TryGetProperty("logoutUrl", out _).Should().BeFalse();
        section.TryGetProperty("developmentUserCookie", out _).Should().BeFalse();
        section.TryGetProperty("developmentRolesCookie", out _).Should().BeFalse();
        section.TryGetProperty("developmentGroupsCookie", out _).Should().BeFalse();
        section.TryGetProperty("developmentSignedOutUser", out _).Should().BeFalse();
    }

    private static Task<ProxyIdentityTestHost> StartAsync(string environment) {
        return ProxyIdentityTestHost.StartAsync(Settings(), environment: environment);
    }

    private static Dictionary<string, string?> Settings(Dictionary<string, string?>? overrides = null) {
        var settings = new Dictionary<string, string?> {
            ["ProxyIdentity:Fixed:Enabled"] = "true",
            ["ProxyIdentity:Fixed:Subject"] = " household ",
            ["ProxyIdentity:Fixed:Email"] = "home@example.com",
            ["ProxyIdentity:Fixed:Name"] = "Household",
            ["ProxyIdentity:Fixed:Roles:0"] = "admin",
            ["ProxyIdentity:Fixed:Groups:0"] = "family",
        };
        foreach (var (key, value) in overrides ?? []) {
            settings[key] = value;
        }
        return settings;
    }
}
