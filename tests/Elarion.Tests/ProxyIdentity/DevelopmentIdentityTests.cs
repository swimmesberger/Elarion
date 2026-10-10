using System.Net;
using AwesomeAssertions;
using Elarion.AspNetCore.ProxyIdentity;
using Xunit;

namespace Elarion.Tests.ProxyIdentity;

/// <summary>The Development stand-in identity: its claim shape, and switching users by header or cookie.</summary>
public sealed class DevelopmentIdentityTests {
    [Fact]
    public async Task EveryRequest_SignsInAsTheConfiguredDeveloper() {
        await using var host = await StartAsync(new Dictionary<string, string?> {
            ["ProxyIdentity:Development:Roles:0"] = "admin",
            ["ProxyIdentity:Development:Groups:0"] = "staff",
        });

        var whoAmI = await host.WhoAmIAsync();

        whoAmI.Status.Should().Be(HttpStatusCode.OK);
        whoAmI.UserId.Should().Be("developer");
        whoAmI.Email.Should().Be("developer@example.com");
        whoAmI.Roles.Should().Be("admin");
        whoAmI.Groups.Should().Be("staff");
        whoAmI.Issuer.Should().Be(ProxyIdentityDefaults.DevelopmentIssuer);
        whoAmI.EmailVerified.Should().BeTrue();
        whoAmI.IsDevelopmentIdentity.Should().BeTrue();
    }

    [Fact]
    public async Task Header_SwitchesToAConfiguredUser() {
        await using var host = await StartAsync(ConfiguredUser());

        var whoAmI = await host.WhoAmIAsync(header: "Reader@Example.com", headerName: ProxyIdentityDefaults.DevelopmentUserHeader);

        whoAmI.UserId.Should().Be("reader-1");
        whoAmI.Email.Should().Be("reader@example.com");
        whoAmI.Roles.Should().Be("reader");
        whoAmI.Name.Should().Be("A Reader");
    }

    [Fact]
    public async Task Cookie_SwitchesByUrlEncodedSubject() {
        await using var host = await StartAsync(ConfiguredUser());

        var whoAmI = await host.WhoAmIAsync(cookie: Uri.EscapeDataString("reader-1"), cookieName: ProxyIdentityDefaults.DevelopmentUserCookie);

        whoAmI.Email.Should().Be("reader@example.com");
    }

    [Fact]
    public async Task UnknownAddress_BecomesAnAdHocUserWithoutRoles() {
        await using var host = await StartAsync(new Dictionary<string, string?> { ["ProxyIdentity:Development:Roles:0"] = "admin" });

        var whoAmI = await host.WhoAmIAsync(
            cookie: Uri.EscapeDataString("New.Person@example.com"), cookieName: ProxyIdentityDefaults.DevelopmentUserCookie);

        whoAmI.UserId.Should().Be("dev:new.person@example.com");
        whoAmI.Email.Should().Be("New.Person@example.com");
        whoAmI.Roles.Should().BeEmpty();
    }

    [Fact]
    public async Task DisabledSwitch_IgnoresTheHeader() {
        await using var host = await StartAsync(new Dictionary<string, string?> { ["ProxyIdentity:Development:UserHeader"] = "" });

        var whoAmI = await host.WhoAmIAsync(header: "someone@example.com", headerName: ProxyIdentityDefaults.DevelopmentUserHeader);

        whoAmI.UserId.Should().Be("developer");
    }

    [Fact]
    public async Task ProxyTokens_AreNotRead() {
        await using var host = await StartAsync(new Dictionary<string, string?>());

        var whoAmI = await host.WhoAmIAsync(
            header: TestSigningKey.Create("key-a").Mint(ProxyIdentityTestHost.Issuer, ProxyIdentityTestHost.Audience));

        whoAmI.UserId.Should().Be("developer");
    }

    private static Dictionary<string, string?> ConfiguredUser() {
        return new Dictionary<string, string?> {
            ["ProxyIdentity:Development:Users:0:Subject"] = "reader-1",
            ["ProxyIdentity:Development:Users:0:Email"] = "reader@example.com",
            ["ProxyIdentity:Development:Users:0:Name"] = "A Reader",
            ["ProxyIdentity:Development:Users:0:Roles:0"] = "reader",
        };
    }

    private static Task<ProxyIdentityTestHost> StartAsync(Dictionary<string, string?> settings) {
        return ProxyIdentityTestHost.StartAsync(settings, environment: "Development");
    }
}
