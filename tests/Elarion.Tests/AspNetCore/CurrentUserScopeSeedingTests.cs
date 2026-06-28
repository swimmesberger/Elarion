using System.Security.Claims;
using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.Abstractions.Authorization;
using Elarion.Abstractions.Identity;
using Elarion.AspNetCore.Identity;
using Elarion.Authorization;
using Elarion.JsonRpc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elarion.Tests.AspNetCore;

/// <summary>
/// Verifies that <c>AddElarionCurrentUser</c> registers an <see cref="IDispatchScopeInitializer"/> that seeds
/// the per-call child scope from the captured principal, so <c>ICurrentUser</c> (and the authorization that
/// depends on it) resolves inside the fresh scope the JSON-RPC / MCP dispatchers create — the scope the
/// request-scope middleware snapshot never reaches.
/// </summary>
public sealed class CurrentUserScopeSeedingTests {
    private static ServiceProvider BuildProvider(bool withAuthorization = false) {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddElarionCurrentUser();
        if (withAuthorization) {
            services.AddElarionAuthorization();
        }

        return services.BuildServiceProvider();
    }

    private static ClaimsPrincipal Authenticated(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, authenticationType: "test"));

    [Fact]
    public void CreateDispatchScope_SeedsCurrentUserFromContextPrincipal() {
        using var provider = BuildProvider();
        var context = new DispatchScopeContext();
        context.Set<ClaimsPrincipal>(Authenticated(new Claim("sub", "user-42"), new Claim("email", "u@x.test")));

        using var scope = provider.CreateDispatchScope(context);
        var user = scope.ServiceProvider.GetRequiredService<ICurrentUser>();

        user.IsAuthenticated.Should().BeTrue();
        user.UserId.Should().Be("user-42");
        user.Email.Should().Be("u@x.test");
    }

    [Fact]
    public void CreateDispatchScope_NoPrincipal_CurrentUserIsAnonymousAndDoesNotThrow() {
        using var provider = BuildProvider();

        using var scope = provider.CreateDispatchScope();
        var user = scope.ServiceProvider.GetRequiredService<ICurrentUser>();

        user.IsAuthenticated.Should().BeFalse();
    }

    [Fact]
    public async Task CreateDispatchScope_SeededUser_IsVisibleToAuthorizer() {
        using var provider = BuildProvider(withAuthorization: true);
        var context = new DispatchScopeContext();
        context.Set<ClaimsPrincipal>(
            Authenticated(new Claim("sub", "user-1"), new Claim("permission", "tenants.read")));
        var requirements = new AuthorizationRequirements(
            AllowAnonymous: false, RequireAuthenticated: false, Permissions: ["tenants.read"], Roles: [],
            Claims: [], Policies: []);

        using var scope = provider.CreateDispatchScope(context);
        var authorizer = scope.ServiceProvider.GetRequiredService<IAuthorizer>();
        var error = await authorizer.AuthorizeAsync(requirements, null, TestContext.Current.CancellationToken);

        error.Should().BeNull();
    }

    [Fact]
    public async Task CreateDispatchScope_AnonymousUser_AuthorizerReturnsUnauthorized() {
        using var provider = BuildProvider(withAuthorization: true);
        var requirements = new AuthorizationRequirements(
            AllowAnonymous: false, RequireAuthenticated: false, Permissions: ["tenants.read"], Roles: [],
            Claims: [], Policies: []);

        using var scope = provider.CreateDispatchScope();
        var authorizer = scope.ServiceProvider.GetRequiredService<IAuthorizer>();
        var error = await authorizer.AuthorizeAsync(requirements, null, TestContext.Current.CancellationToken);

        error!.Kind.Should().Be(ErrorKind.Unauthorized);
    }
}
