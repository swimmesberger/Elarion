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
                new Claim("scope", "files")
            ],
            "test"));
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
            "test"));
        var context = new DispatchScopeContext();
        context.Set<ClaimsPrincipal>(principal);

        using var scope = provider.CreateDispatchScope(context);
        var user = scope.ServiceProvider.GetRequiredService<ICurrentUser>();

        user.UserId.Should().Be("user-1");
        user.Roles.Should().BeEquivalentTo("admin", "user");
        user.IsInRole("admin").Should().BeTrue();
    }

    [Fact]
    public void ReseedingWithDifferentPrincipal_DropsCachedClaimsAndRoles() {
        // A reused per-connection dispatch scope re-seeds the same instance per message; after identity
        // promotion the lazily cached claims/roles must reflect the new principal, not the first one's.
        var user = new ClaimsPrincipalCurrentUser(new ClaimsCurrentUserOptions { RoleClaimType = "role" });
        var initial = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("role", "guest"), new Claim("permission", "read")], "test"));
        var promoted = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("role", "admin"), new Claim("permission", "write")], "test"));

        Seed(user, initial);
        user.Roles.Should().BeEquivalentTo("guest");
        user.HasClaim("permission", "read").Should().BeTrue();

        Seed(user, promoted);
        user.Roles.Should().BeEquivalentTo("admin");
        user.HasClaim("permission", "read").Should().BeFalse();
        user.HasClaim("permission", "write").Should().BeTrue();
    }

    private static void Seed(ClaimsPrincipalCurrentUser user, ClaimsPrincipal principal) {
        // Through the public rail (Initialize is internal): the initializer seeds from the context.
        var context = new DispatchScopeContext();
        context.Set(principal);
        using var provider = new ServiceCollection().AddSingleton(user).BuildServiceProvider();
        new CurrentUserScopeInitializer().Initialize(provider, context);
    }

    [Fact]
    public void UnseededUser_BehavesAsAnonymousInsteadOfThrowing() {
        // A background delivery scope (integration-event pump, outbox) never seeds a principal. The current
        // user must fail closed as anonymous so authorization denies (401) rather than crashing the consumer.
        var user = new ClaimsPrincipalCurrentUser(new ClaimsCurrentUserOptions());

        user.IsAuthenticated.Should().BeFalse();
        user.Roles.Should().BeEmpty();
        user.HasClaim("permission", "anything").Should().BeFalse();
        user.GetClaimValues("permission").Should().BeEmpty();
        user.IsInRole("admin").Should().BeFalse();
        user.Email.Should().BeNull();
    }
}
