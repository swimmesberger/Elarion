using System.Security.Claims;
using AwesomeAssertions;
using Elarion.AspNetCore.ProxyIdentity;
using Elarion.Tests.Authorization;
using Microsoft.Extensions.Options;
using Xunit;

namespace Elarion.Tests.ProxyIdentity;

/// <summary>Reading <see cref="ExternalIdentity"/> from a principal or from <c>ICurrentUser</c>, and the session section built on it.</summary>
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
        Elarion.Session.IClientSnapshotContributor contributor = new ProxyIdentitySessionContributor(user, Options.Create(_options));

        var section = await contributor.GetSectionAsync(TestContext.Current.CancellationToken);

        contributor.SectionName.Should().Be("proxyIdentity");
        section.Should().BeEquivalentTo(new ProxyIdentitySection {
            Email = "dev@example.com",
            Name = "Dev",
            LogoutUrl = "/logout",
            IsDevelopmentIdentity = true,
            DevelopmentUserCookie = ProxyIdentityDefaults.DevelopmentUserCookie,
        });
    }

    [Fact]
    public async Task SessionSection_IsOmittedForAnAnonymousCaller() {
        var contributor = new ProxyIdentitySessionContributor(new FakeCurrentUser(), Options.Create(_options));

        (await contributor.GetSectionAsync(TestContext.Current.CancellationToken)).Should().BeNull();
    }

    private static ClaimsPrincipal Principal(params Claim[] claims) {
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }
}
