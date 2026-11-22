using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.Abstractions.Authorization;
using Elarion.Abstractions.Identity;
using Elarion.Abstractions.Modules;
using Elarion.Authorization;
using Elarion.Session;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Elarion.Tests.Authorization;

public sealed class PermissionCheckerTests {
    [Fact]
    public void ClaimsCheckerHoldsAPermissionClaimOfTheConfiguredType() {
        var user = new FakeCurrentUser { IsAuthenticated = true, Claims = [("perm", "banking.read")] };
        var checker = new ClaimsPermissionChecker(user, new AuthorizationOptions { PermissionClaimType = "perm" });

        checker.Holds("banking.read").Should().BeTrue();
        checker.Holds("banking.write").Should().BeFalse();
        checker.Holds("banking", Verbs.Read).Should().BeTrue();
    }

    [Fact]
    public void ClaimsCheckerIgnoresClaimsOfAnotherType() {
        var user = new FakeCurrentUser { IsAuthenticated = true, Claims = [("permission", "banking.read")] };
        var checker = new ClaimsPermissionChecker(user, new AuthorizationOptions { PermissionClaimType = "perm" });

        checker.Holds("banking.read").Should().BeFalse();
    }

    [Fact]
    public void AnUnauthenticatedCallerHoldsNothing() {
        // A principal that carries the claim but is not authenticated must not pass a check the gate would answer
        // with 401.
        var user = new FakeCurrentUser { IsAuthenticated = false, Claims = [("permission", "banking.read")] };
        var checker = new ClaimsPermissionChecker(user, new AuthorizationOptions());

        checker.Holds("banking.read").Should().BeFalse();
    }

    [Fact]
    public void HoldsAnyAndHoldsAllCombineChecks() {
        var user = new FakeCurrentUser { IsAuthenticated = true, Claims = [("permission", "banking.read")] };
        var checker = new ClaimsPermissionChecker(user, new AuthorizationOptions());

        checker.HoldsAny("banking.write", "banking.read").Should().BeTrue();
        checker.HoldsAny("banking.write").Should().BeFalse();
        checker.HoldsAll("banking.read").Should().BeTrue();
        checker.HoldsAll("banking.read", "banking.write").Should().BeFalse();
    }

    [Fact]
    public async Task TheGateAndTheCheckerGiveTheSameAnswer() {
        string[] permissions = ["banking.read", "banking.write", "settlements.write"];
        var user = new FakeCurrentUser { IsAuthenticated = true, Claims = [("permission", "banking.read")] };
        var options = new AuthorizationOptions();
        var checker = new ClaimsPermissionChecker(user, options);
        var authorizer = new ClaimsAuthorizer(user, [], new StubResourceAuthorizer(), options,
            NullLogger<ClaimsAuthorizer>.Instance, permissions: checker);

        foreach (var permission in permissions) {
            var gate = await authorizer.AuthorizeGateAsync(Requirements(permission),
                TestContext.Current.CancellationToken);

            (gate is null).Should().Be(checker.Holds(permission), permission);
        }
    }

    [Fact]
    public async Task AReplacedCheckerDecidesTheGate() {
        // A host whose administrators hold every permission says so once, in the checker; the [RequirePermission]
        // gate follows without a permission claim.
        var admin = new FakeCurrentUser { IsAuthenticated = true, Roles = ["admin"] };
        var options = new AuthorizationOptions();
        var checker = new AdminHoldsEverything(new ClaimsPermissionChecker(admin, options), admin);
        var authorizer = new ClaimsAuthorizer(admin, [], new StubResourceAuthorizer(), options,
            NullLogger<ClaimsAuthorizer>.Instance, permissions: checker);

        var error = await authorizer.AuthorizeAsync(Requirements("settlements.write"), null,
            TestContext.Current.CancellationToken);

        error.Should().BeNull();
    }

    [Fact]
    public void AddElarionAuthorizationRegistersTheClaimsCheckerAndTheAuthorizerUsesIt() {
        var services = new ServiceCollection();
        services.AddSingleton<ICurrentUser>(new FakeCurrentUser {
            IsAuthenticated = true, Claims = [("permission", "banking.read")]
        });
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        services.AddElarionAuthorization();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var checker = scope.ServiceProvider.GetRequiredService<IPermissionChecker>();
        checker.Should().BeOfType<ClaimsPermissionChecker>();
        checker.Holds("banking.read").Should().BeTrue();
    }

    [Fact]
    public void AHostCheckerRegisteredFirstWinsAndCanWrapTheClaimsChecker() {
        var services = new ServiceCollection();
        var admin = new FakeCurrentUser { IsAuthenticated = true, Roles = ["admin"] };
        services.AddSingleton<ICurrentUser>(admin);
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        services.AddScoped<IPermissionChecker, AdminHoldsEverything>();
        services.AddElarionAuthorization();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<IPermissionChecker>().Holds("banking.write").Should().BeTrue();
    }

    [Fact]
    public async Task TheSessionSnapshotProjectsWhatTheCheckerGrants() {
        var admin = new FakeCurrentUser {
            IsAuthenticated = true, Roles = ["admin"], Claims = [("permission", "banking.read")]
        };
        var checker = new AdminHoldsEverything(new ClaimsPermissionChecker(admin, new AuthorizationOptions()), admin);
        var catalog = new PermissionCatalog([
            new PermissionCatalogModule {
                Module = "Banking",
                Permissions = [
                    new PermissionCatalogEntry { Permission = "banking.read", Resource = "banking", Verb = "read" },
                    new PermissionCatalogEntry { Permission = "banking.write", Resource = "banking", Verb = "write" }
                ],
                Roles = []
            }
        ]);
        var session = new SessionHandler(admin, ClientCapabilityManifest.Empty, permissionChecker: checker,
            permissionCatalog: catalog);

        var result = await session.HandleAsync(new SessionRequest(), TestContext.Current.CancellationToken);

        result.Value.User.Permissions.Should().Equal("banking.read", "banking.write");
    }

    [Fact]
    public async Task WithTheDefaultCheckerTheSessionSnapshotIsTheClaims() {
        var user = new FakeCurrentUser { IsAuthenticated = true, Claims = [("permission", "banking.read")] };
        var checker = new ClaimsPermissionChecker(user, new AuthorizationOptions());
        var catalog = new PermissionCatalog([
            new PermissionCatalogModule {
                Module = "Banking",
                Permissions = [
                    new PermissionCatalogEntry { Permission = "banking.write", Resource = "banking", Verb = "write" }
                ],
                Roles = []
            }
        ]);
        var session = new SessionHandler(user, ClientCapabilityManifest.Empty, permissionChecker: checker,
            permissionCatalog: catalog);

        var result = await session.HandleAsync(new SessionRequest(), TestContext.Current.CancellationToken);

        result.Value.User.Permissions.Should().Equal("banking.read");
    }

    private static AuthorizationRequirements Requirements(string permission) {
        return new AuthorizationRequirements(false, false, [permission], [], [], [], []);
    }

    private sealed class AdminHoldsEverything(ClaimsPermissionChecker claims, ICurrentUser user) : IPermissionChecker {
        public bool Holds(string permission) {
            return user.IsInRole("admin") || claims.Holds(permission);
        }
    }
}
