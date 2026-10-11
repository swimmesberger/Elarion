using System.Net;
using AwesomeAssertions;
using Elarion.AspNetCore.ProxyIdentity;
using Xunit;

namespace Elarion.Tests.ProxyIdentity;

/// <summary>
/// The Development stand-in identity: its claim shape, switching users by header or cookie, and replacing the user's
/// roles and groups with the roles and groups switches.
/// </summary>
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

    [Fact]
    public async Task RolesHeader_GivesAnAdHocUserRoles() {
        await using var host = await StartAsync(new Dictionary<string, string?>());

        var whoAmI = await host.WhoAmIAsync(headers: new Dictionary<string, string> {
            [ProxyIdentityDefaults.DevelopmentUserHeader] = "new.person@example.com",
            [ProxyIdentityDefaults.DevelopmentRolesHeader] = " owner , admin,owner,, ",
        });

        whoAmI.UserId.Should().Be("dev:new.person@example.com");
        whoAmI.Roles.Should().Be("owner,admin");
        whoAmI.IsDevelopmentIdentity.Should().BeTrue();
    }

    [Fact]
    public async Task RolesCookie_GivesAnAdHocUserRoles() {
        await using var host = await StartAsync(new Dictionary<string, string?>());

        var whoAmI = await host.WhoAmIAsync(cookies: new Dictionary<string, string> {
            [ProxyIdentityDefaults.DevelopmentUserCookie] = Uri.EscapeDataString("new.person@example.com"),
            [ProxyIdentityDefaults.DevelopmentRolesCookie] = Uri.EscapeDataString("owner,admin"),
        });

        whoAmI.UserId.Should().Be("dev:new.person@example.com");
        whoAmI.Roles.Should().Be("owner,admin");
    }

    [Fact]
    public async Task RolesSwitch_ReplacesAConfiguredUsersRoles() {
        await using var host = await StartAsync(ConfiguredUser());

        var whoAmI = await host.WhoAmIAsync(headers: new Dictionary<string, string> {
            [ProxyIdentityDefaults.DevelopmentUserHeader] = "reader@example.com",
            [ProxyIdentityDefaults.DevelopmentRolesHeader] = "editor",
        });

        whoAmI.UserId.Should().Be("reader-1");
        whoAmI.Roles.Should().Be("editor");
    }

    [Fact]
    public async Task RolesSwitch_ReplacesTheDevelopersRoles() {
        await using var host = await StartAsync(new Dictionary<string, string?> { ["ProxyIdentity:Development:Roles:0"] = "admin" });

        var whoAmI = await host.WhoAmIAsync(headers: new Dictionary<string, string> {
            [ProxyIdentityDefaults.DevelopmentRolesHeader] = "reader",
        });

        whoAmI.UserId.Should().Be("developer");
        whoAmI.Roles.Should().Be("reader");
    }

    [Fact]
    public async Task EmptyRolesHeader_MeansNoRoles() {
        await using var host = await StartAsync(new Dictionary<string, string?> { ["ProxyIdentity:Development:Roles:0"] = "admin" });

        var whoAmI = await host.WhoAmIAsync(headers: new Dictionary<string, string> {
            [ProxyIdentityDefaults.DevelopmentRolesHeader] = "",
        });

        whoAmI.Status.Should().Be(HttpStatusCode.OK);
        whoAmI.Roles.Should().BeEmpty();
    }

    [Fact]
    public async Task RolesCookieWithoutEntries_MeansNoRoles() {
        // ASP.NET Core drops a cookie whose value is empty, so a cookie clears the roles with a list of no entries.
        await using var host = await StartAsync(new Dictionary<string, string?> { ["ProxyIdentity:Development:Roles:0"] = "admin" });

        var whoAmI = await host.WhoAmIAsync(cookies: new Dictionary<string, string> {
            [ProxyIdentityDefaults.DevelopmentRolesCookie] = Uri.EscapeDataString(","),
        });

        whoAmI.Roles.Should().BeEmpty();
    }

    [Fact]
    public async Task RolesHeader_WinsOverTheRolesCookie() {
        await using var host = await StartAsync(new Dictionary<string, string?>());

        var whoAmI = await host.WhoAmIAsync(
            headers: new Dictionary<string, string> { [ProxyIdentityDefaults.DevelopmentRolesHeader] = "from-header" },
            cookies: new Dictionary<string, string> { [ProxyIdentityDefaults.DevelopmentRolesCookie] = "from-cookie" });

        whoAmI.Roles.Should().Be("from-header");
    }

    [Fact]
    public async Task AbsentRolesSwitch_KeepsTheConfiguredRoles() {
        await using var host = await StartAsync(ConfiguredUser());

        var whoAmI = await host.WhoAmIAsync(header: "reader@example.com", headerName: ProxyIdentityDefaults.DevelopmentUserHeader);

        whoAmI.Roles.Should().Be("reader");
    }

    [Fact]
    public async Task DisabledRolesSwitch_IgnoresTheHeaderAndCookie() {
        await using var host = await StartAsync(new Dictionary<string, string?> {
            ["ProxyIdentity:Development:Roles:0"] = "admin",
            ["ProxyIdentity:Development:RolesHeader"] = "",
            ["ProxyIdentity:Development:RolesCookie"] = "",
        });

        var whoAmI = await host.WhoAmIAsync(
            headers: new Dictionary<string, string> { [ProxyIdentityDefaults.DevelopmentRolesHeader] = "owner" },
            cookies: new Dictionary<string, string> { [ProxyIdentityDefaults.DevelopmentRolesCookie] = "owner" });

        whoAmI.Roles.Should().Be("admin");
    }

    [Fact]
    public async Task OverlongRolesSwitch_IsIgnored() {
        await using var host = await StartAsync(new Dictionary<string, string?> { ["ProxyIdentity:Development:Roles:0"] = "admin" });

        var whoAmI = await host.WhoAmIAsync(headers: new Dictionary<string, string> {
            [ProxyIdentityDefaults.DevelopmentRolesHeader] = new string('r', 257),
        });

        whoAmI.Roles.Should().Be("admin");
    }

    [Fact]
    public async Task RolesSwitch_UsesTheConfiguredRoleClaim() {
        await using var host = await StartAsync(new Dictionary<string, string?> { ["ProxyIdentity:Claims:Roles"] = "app_roles" });

        var whoAmI = await host.WhoAmIAsync(headers: new Dictionary<string, string> {
            [ProxyIdentityDefaults.DevelopmentRolesHeader] = "owner",
        });

        whoAmI.Roles.Should().Be("owner");
    }

    [Fact]
    public async Task GroupsSwitch_ReplacesTheGroups() {
        await using var host = await StartAsync(new Dictionary<string, string?> { ["ProxyIdentity:Development:Groups:0"] = "staff" });

        var fromHeader = await host.WhoAmIAsync(headers: new Dictionary<string, string> {
            [ProxyIdentityDefaults.DevelopmentUserHeader] = "new.person@example.com",
            [ProxyIdentityDefaults.DevelopmentGroupsHeader] = "auditors,staff",
        });
        var fromCookie = await host.WhoAmIAsync(cookies: new Dictionary<string, string> {
            [ProxyIdentityDefaults.DevelopmentGroupsCookie] = Uri.EscapeDataString("auditors"),
        });
        var cleared = await host.WhoAmIAsync(headers: new Dictionary<string, string> {
            [ProxyIdentityDefaults.DevelopmentGroupsHeader] = "",
        });

        fromHeader.Groups.Should().Be("auditors,staff");
        fromHeader.Roles.Should().BeEmpty();
        fromCookie.Groups.Should().Be("auditors");
        cleared.Groups.Should().BeEmpty();
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
