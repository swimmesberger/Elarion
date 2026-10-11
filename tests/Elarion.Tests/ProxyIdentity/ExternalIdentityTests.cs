using System.Security.Claims;
using AwesomeAssertions;
using Elarion.AspNetCore.ProxyIdentity;
using Elarion.Tests.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Xunit;

namespace Elarion.Tests.ProxyIdentity;

/// <summary>
/// Reading <see cref="ExternalIdentity"/> from a request, a principal or <c>ICurrentUser</c>, and the session section built
/// on it.
/// </summary>
public sealed class ExternalIdentityTests {
    private readonly ProxyIdentityOptions _options = new();

    [Fact]
    public void Anonymous_ReadsNothing() {
        ExternalIdentity.TryRead(new ClaimsPrincipal(new ClaimsIdentity([new Claim("iss", "i"), new Claim("sub", "s")])), _options)
            .Should().BeNull();
        ExternalIdentity.TryRead(new FakeCurrentUser { IsAuthenticated = false }, _options).Should().BeNull();
    }

    [Theory]
    [InlineData("iss")]
    [InlineData("sub")]
    public void MissingIssuerOrSubject_ReadsNothing(string missing) {
        var claims = new[] { new Claim("iss", "https://issuer.example"), new Claim("sub", "user-1") }.Where(claim => claim.Type != missing);

        ExternalIdentity.TryRead(Principal(claims.ToArray()), _options).Should().BeNull();
    }

    [Fact]
    public void Principal_ReadsEveryField_TrimmedAndDeduplicated() {
        var identity = ExternalIdentity.TryRead(Principal(
            new Claim("iss", "https://issuer.example"),
            new Claim("sub", " user-1 "),
            new Claim("email", "user@example.com"),
            new Claim("email_verified", "True"),
            new Claim("name", "A User"),
            new Claim("groups", "staff"),
            new Claim("groups", "staff"),
            new Claim("groups", " "),
            new Claim("roles", "admin")), _options);

        identity.Should().BeEquivalentTo(new ExternalIdentity {
            Issuer = "https://issuer.example",
            Subject = "user-1",
            Email = "user@example.com",
            EmailVerified = true,
            Name = "A User",
            Groups = ["staff"],
            Roles = ["admin"],
        });
        identity!.IsDevelopmentIdentity.Should().BeFalse();
    }

    [Theory]
    [InlineData(ProxyEmailTrust.Claim, null, false)]
    [InlineData(ProxyEmailTrust.Claim, "yes", false)]
    [InlineData(ProxyEmailTrust.Claim, "true", true)]
    [InlineData(ProxyEmailTrust.Trusted, null, true)]
    [InlineData(ProxyEmailTrust.None, "true", false)]
    public void EmailTrust_DecidesVerified(ProxyEmailTrust trust, string? claimed, bool expected) {
        _options.EmailTrust = trust;
        var claims = new List<Claim> { new("iss", "i"), new("sub", "s"), new("email", "user@example.com") };
        if (claimed is not null) {
            claims.Add(new Claim("email_verified", claimed));
        }

        ExternalIdentity.TryRead(Principal([.. claims]), _options)!.EmailVerified.Should().Be(expected);
    }

    [Fact]
    public void WithoutAnAddress_NothingIsVerified() {
        _options.EmailTrust = ProxyEmailTrust.Trusted;

        ExternalIdentity.TryRead(Principal(new Claim("iss", "i"), new Claim("sub", "s")), _options)!.EmailVerified.Should().BeFalse();
    }

    [Fact]
    public void CurrentUser_ReadsTheSameFields() {
        var user = new FakeCurrentUser {
            IsAuthenticated = true,
            Claims = [("iss", ProxyIdentityDefaults.DevelopmentIssuer), ("sub", "developer"), ("email", "dev@example.com"), ("roles", "a"), ("roles", "b")],
        };

        var identity = ExternalIdentity.TryRead(user, _options)!;

        identity.Subject.Should().Be("developer");
        identity.Roles.Should().Equal("a", "b");
        identity.IsDevelopmentIdentity.Should().BeTrue();
    }

    [Fact]
    public void DisabledClaim_IsNotRead() {
        _options.Claims.Email = null;
        _options.Claims.Groups = "";

        var identity = ExternalIdentity.TryRead(
            Principal(new Claim("iss", "i"), new Claim("sub", "s"), new Claim("email", "x@example.com"), new Claim("groups", "g")), _options)!;

        identity.Email.Should().BeNull();
        identity.Groups.Should().BeEmpty();
    }

    [Fact]
    public async Task SessionSection_ProjectsTheSignedInUser() {
        _options.LogoutUrl = "/logout";
        var user = new FakeCurrentUser {
            IsAuthenticated = true,
            Claims = [("iss", ProxyIdentityDefaults.DevelopmentIssuer), ("sub", "developer"), ("email", "dev@example.com"), ("name", "Dev")],
        };
        Elarion.Session.IClientSnapshotContributor contributor = Contributor(user);

        var section = await contributor.GetSectionAsync(TestContext.Current.CancellationToken);

        contributor.SectionName.Should().Be("proxyIdentity");
        section.Should().BeEquivalentTo(new ProxyIdentitySection {
            Email = "dev@example.com",
            Name = "Dev",
            IsDevelopmentIdentity = true,
            DevelopmentUserCookie = ProxyIdentityDefaults.DevelopmentUserCookie,
            DevelopmentRolesCookie = ProxyIdentityDefaults.DevelopmentRolesCookie,
            DevelopmentGroupsCookie = ProxyIdentityDefaults.DevelopmentGroupsCookie,
        });
    }

    [Fact]
    public async Task SessionSection_OmitsTheLogoutUrlForTheStandIn_ButKeepsTheSwitchCookies() {
        _options.LogoutUrl = "/logout";
        var user = new FakeCurrentUser {
            IsAuthenticated = true,
            Claims = [("iss", ProxyIdentityDefaults.DevelopmentIssuer), ("sub", "developer")],
        };

        var section = (ProxyIdentitySection)(await Contributor(user).GetSectionAsync(TestContext.Current.CancellationToken))!;

        section.IsDevelopmentIdentity.Should().BeTrue();
        section.LogoutUrl.Should().BeNull();
        section.DevelopmentUserCookie.Should().Be(ProxyIdentityDefaults.DevelopmentUserCookie);
        section.DevelopmentRolesCookie.Should().Be(ProxyIdentityDefaults.DevelopmentRolesCookie);
        section.DevelopmentGroupsCookie.Should().Be(ProxyIdentityDefaults.DevelopmentGroupsCookie);
    }

    [Fact]
    public async Task SessionSection_CarriesTheLogoutUrlForAProxyIdentity() {
        _options.Enabled = true;
        _options.LogoutUrl = "/logout";
        var user = new FakeCurrentUser {
            IsAuthenticated = true,
            Claims = [("iss", "https://issuer.example"), ("sub", "user-1")],
        };

        var section = (ProxyIdentitySection)(await Contributor(user).GetSectionAsync(TestContext.Current.CancellationToken))!;

        section.IsDevelopmentIdentity.Should().BeFalse();
        section.LogoutUrl.Should().Be("/logout");
    }

    [Fact]
    public async Task SessionSection_OmitsDisabledSwitchCookies() {
        _options.Development.UserCookie = null;
        _options.Development.RolesCookie = "";
        _options.Development.GroupsCookie = "team-cookie";
        var user = new FakeCurrentUser {
            IsAuthenticated = true,
            Claims = [("iss", ProxyIdentityDefaults.DevelopmentIssuer), ("sub", "developer")],
        };
        var contributor = Contributor(user);

        var section = (ProxyIdentitySection)(await contributor.GetSectionAsync(TestContext.Current.CancellationToken))!;

        section.DevelopmentUserCookie.Should().BeNull();
        section.DevelopmentRolesCookie.Should().BeNull();
        section.DevelopmentGroupsCookie.Should().Be("team-cookie");
    }

    [Fact]
    public async Task SessionSection_OmitsSwitchCookiesForAProxyIdentity() {
        _options.Enabled = true;
        var user = new FakeCurrentUser {
            IsAuthenticated = true,
            Claims = [("iss", "https://issuer.example"), ("sub", "user-1")],
        };
        var contributor = Contributor(user);

        var section = (ProxyIdentitySection)(await contributor.GetSectionAsync(TestContext.Current.CancellationToken))!;

        section.IsDevelopmentIdentity.Should().BeFalse();
        section.DevelopmentUserCookie.Should().BeNull();
        section.DevelopmentRolesCookie.Should().BeNull();
        section.DevelopmentGroupsCookie.Should().BeNull();
    }

    [Fact]
    public async Task SessionSection_IsOmittedForAnAnonymousCaller_WithoutTheStandIn() {
        _options.Enabled = true;
        var contributor = Contributor(new FakeCurrentUser());

        (await contributor.GetSectionAsync(TestContext.Current.CancellationToken)).Should().BeNull();
    }

    [Fact]
    public async Task SessionSection_NamesTheSwitchCookiesForAnAnonymousCaller_WhileTheStandInIsConfigured() {
        _options.LogoutUrl = "/logout";
        var contributor = Contributor(new FakeCurrentUser());

        var section = await contributor.GetSectionAsync(TestContext.Current.CancellationToken);

        section.Should().BeEquivalentTo(new ProxyIdentitySection {
            DevelopmentUserCookie = ProxyIdentityDefaults.DevelopmentUserCookie,
            DevelopmentRolesCookie = ProxyIdentityDefaults.DevelopmentRolesCookie,
            DevelopmentGroupsCookie = ProxyIdentityDefaults.DevelopmentGroupsCookie,
        });
    }

    [Fact]
    public async Task SessionSection_ReadsTheCapturedIdentity_WhenTheRequestPrincipalNoLongerCarriesIt() {
        _options.Enabled = true;
        var context = new DefaultHttpContext { User = Principal(new Claim("sub", "account-1")) };
        ExternalIdentityFeature.Capture(context, Principal(
            new Claim("iss", "https://issuer.example"), new Claim("sub", "user-1"), new Claim("email", "user@example.com")), _options);
        var contributor = new ProxyIdentitySessionContributor(
            new FakeCurrentUser { IsAuthenticated = true, Claims = [("sub", "account-1")] },
            Options.Create(_options),
            new HttpContextAccessor { HttpContext = context });

        var section = (ProxyIdentitySection)(await contributor.GetSectionAsync(TestContext.Current.CancellationToken))!;

        section.Email.Should().Be("user@example.com");
        section.IsDevelopmentIdentity.Should().BeFalse();
    }

    [Fact]
    public void HttpContext_PrefersTheCapturedIdentity_OverTheReplacedPrincipal() {
        var context = new DefaultHttpContext();
        ExternalIdentityFeature.Capture(context, Principal(new Claim("iss", "https://issuer.example"), new Claim("sub", "user-1")), _options);
        context.User = Principal(new Claim("iss", "https://other.example"), new Claim("sub", "account-1"));

        var identity = ExternalIdentity.TryRead(context, _options)!;

        identity.Issuer.Should().Be("https://issuer.example");
        identity.Subject.Should().Be("user-1");
    }

    [Fact]
    public void HttpContext_WithoutACapturedIdentity_ReadsThePrincipal() {
        var context = new DefaultHttpContext { User = Principal(new Claim("iss", "https://issuer.example"), new Claim("sub", "user-1")) };

        ExternalIdentity.TryRead(context, _options)!.Subject.Should().Be("user-1");
        ExternalIdentity.TryRead(new DefaultHttpContext(), _options).Should().BeNull();
    }

    [Fact]
    public void Capture_WithoutAnIdentity_ClearsAnEarlierOne() {
        var context = new DefaultHttpContext();
        ExternalIdentityFeature.Capture(context, Principal(new Claim("iss", "i"), new Claim("sub", "s")), _options);

        ExternalIdentityFeature.Capture(context, Principal(new Claim("sub", "s")), _options);

        context.Features.Get<IExternalIdentityFeature>().Should().BeNull();
    }

    /// <summary>A contributor outside an HTTP request, so it reads the current user.</summary>
    private ProxyIdentitySessionContributor Contributor(FakeCurrentUser user) {
        return new ProxyIdentitySessionContributor(user, Options.Create(_options), new HttpContextAccessor());
    }

    private static ClaimsPrincipal Principal(params Claim[] claims) {
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }
}
