using System.Security.Claims;
using AwesomeAssertions;
using Elarion.Abstractions.Dispatch;
using Elarion.Abstractions.Identity;
using Elarion.Identity;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elarion.Tests.Identity;

/// <summary>
/// The transport-neutral, claims-based <see cref="ICurrentUser"/> in Elarion core — seeded through the
/// dispatch-scope rail with a captured <see cref="ClaimsPrincipal"/>, no ASP.NET involved.
/// </summary>
public sealed class ClaimsPrincipalCurrentUserTests {
    [Fact]
    public void ExposesClaimsFromTheSeededPrincipal() {
        using var provider = new ServiceCollection()
            .AddElarionClaimsCurrentUser()
            .BuildServiceProvider();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("permission", "tenants.read"),
                new Claim("permission", "tenants.write"),
                new Claim("scope", "files"),
            ],
            authenticationType: "test"));
        var context = new DispatchScopeContext();
        context.Set<ClaimsPrincipal>(principal);

        using var scope = provider.CreateDispatchScope(context);
        var user = scope.ServiceProvider.GetRequiredService<ICurrentUser>();

        user.IsAuthenticated.Should().BeTrue();
        user.HasClaim("permission", "tenants.read").Should().BeTrue();
        user.HasClaim("permission", "absent").Should().BeFalse();
        user.GetClaimValues("permission").Should().BeEquivalentTo("tenants.read", "tenants.write");
        user.GetClaimValues("missing").Should().BeEmpty();
    }

    [Fact]
    public void MapsConfiguredClaimTypesForUserIdAndRoles() {
        using var provider = new ServiceCollection()
            .AddElarionClaimsCurrentUser(o => {
                o.UserIdClaimType = "sub";
                o.RoleClaimType = "role";
            })
            .BuildServiceProvider();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", "user-1"), new Claim("role", "admin"), new Claim("role", "user")],
            authenticationType: "test"));
        var context = new DispatchScopeContext();
        context.Set<ClaimsPrincipal>(principal);

        using var scope = provider.CreateDispatchScope(context);
        var user = scope.ServiceProvider.GetRequiredService<ICurrentUser>();

        user.UserId.Should().Be("user-1");
        user.Roles.Should().BeEquivalentTo("admin", "user");
        user.IsInRole("admin").Should().BeTrue();
    }
}
