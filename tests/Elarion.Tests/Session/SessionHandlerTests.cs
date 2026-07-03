using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.Abstractions.Authorization;
using Elarion.Abstractions.Dispatch;
using Elarion.Abstractions.Features;
using Elarion.Abstractions.Identity;
using Elarion.Abstractions.Serialization;
using Elarion.Session;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elarion.Tests.Session;

public sealed class SessionHandlerTests {
    [Fact]
    public async Task HandleAsync_ProjectsModulesFlagsVariantsAndGrants() {
        var manifest = new ClientCapabilityManifest {
            Modules = [
                new ClientModuleManifest { Name = "Billing", Enabled = true, Features = ["new-checkout", "forecast"] },
                new ClientModuleManifest { Name = "Experiments", Enabled = false, Features = ["beta-x"] },
            ],
        };
        var user = new FakeCurrentUser {
            UserId = "u-123",
            IsAuthenticated = true,
            Roles = ["admin"],
            Claims = { ["permission"] = ["billing.write"] },
        };
        var flags = new FakeFeatureFlags { ["new-checkout"] = true, ["forecast"] = true, ["beta-x"] = true };
        var variants = new FakeFeatureVariants { ["forecast"] = "neural" };

        var handler = new SessionHandler(user, manifest, new AuthorizationOptions(), flags, variants);
        var result = await handler.HandleAsync(new SessionRequest(), TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        var response = result.Value!;
        response.User.Id.Should().Be("u-123");
        response.User.IsAuthenticated.Should().BeTrue();
        response.User.Roles.Should().ContainSingle().Which.Should().Be("admin");
        response.User.Permissions.Should().ContainSingle().Which.Should().Be("billing.write");

        response.Modules.Should().Contain("Billing", true);
        response.Modules.Should().Contain("Experiments", false);

        // Only the enabled module's features are evaluated.
        response.Flags.Should().Contain("new-checkout", true);
        response.Flags.Should().Contain("forecast", true);
        response.Flags.Should().NotContainKey("beta-x");

        // A feature resolves a variant only when the variant accessor returns one; a boolean flag does not.
        response.Variants.Should().Contain("forecast", "neural");
        response.Variants.Should().NotContainKey("new-checkout");
    }

    [Fact]
    public async Task HandleAsync_WithNoFeatureServices_StillReturnsModulesAndGrants() {
        var manifest = new ClientCapabilityManifest {
            Modules = [new ClientModuleManifest { Name = "Billing", Enabled = true, Features = ["new-checkout"] }],
        };
        var user = new FakeCurrentUser { UserId = string.Empty, IsAuthenticated = false, Roles = [] };

        // No feature-flag/variant services and no authorization options registered.
        var handler = new SessionHandler(user, manifest);
        var result = await handler.HandleAsync(new SessionRequest(), TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Modules.Should().ContainKey("Billing");
        result.Value!.Flags.Should().BeEmpty();
        result.Value!.Variants.Should().BeEmpty();
        result.Value!.User.IsAuthenticated.Should().BeFalse();
    }

    [Fact]
    public async Task HandleAsync_UsesConfiguredPermissionClaimType() {
        var manifest = ClientCapabilityManifest.Empty;
        var user = new FakeCurrentUser {
            UserId = "u-1",
            IsAuthenticated = true,
            Claims = { ["scope"] = ["orders.read"] },
        };
        var options = new AuthorizationOptions { PermissionClaimType = "scope" };

        var handler = new SessionHandler(user, manifest, options);
        var result = await handler.HandleAsync(new SessionRequest(), TestContext.Current.CancellationToken);

        result.Value!.User.Permissions.Should().ContainSingle().Which.Should().Be("orders.read");
    }

    [Fact]
    public async Task AddElarionSession_RegistersHandler_AndMapElarionSessionRoutesIt() {
        var services = new ServiceCollection();
        services.AddSingleton<ICurrentUser>(new FakeCurrentUser { UserId = "u-1", IsAuthenticated = true });
        services.AddElarionSession(new ClientCapabilityManifest {
            Modules = [new ClientModuleManifest { Name = "Billing", Enabled = true, Features = [] }],
        });
        using var provider = services.BuildServiceProvider();

        provider.GetService<IHandler<SessionRequest, Result<SessionResponse>>>()
            .Should().BeOfType<SessionHandler>();

        var dispatcher = new HandlerDispatcher().MapElarionSession().Freeze();
        dispatcher.TryGetRoute("elarion.session", HandlerTransports.JsonRpc, out _).Should().BeTrue();
        dispatcher.TryGetRoute("elarion.session", HandlerTransports.Mcp, out _).Should().BeTrue();

        using var scope = provider.CreateScope();
        var result = await dispatcher.DispatchAsync(
            "elarion.session", new SessionRequest(), scope.ServiceProvider, TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeOfType<SessionResponse>()
            .Which.Modules.Should().ContainKey("Billing");
    }

    [Fact]
    public void AddElarionSession_ContributesSessionJsonContextToCanonicalSerialization() {
        var services = new ServiceCollection();
        services.AddElarionSession(ClientCapabilityManifest.Empty);
        using var provider = services.BuildServiceProvider();

        var serialization = provider.GetRequiredService<IElarionJsonSerialization>();

        // AOT-strict: GetTypeInfo throws for a type absent from every source-gen context, so a non-null result
        // proves AddElarionSession self-registered SessionJsonContext — the host wires no serialization for it.
        serialization.GetTypeInfo<SessionResponse>().Should().NotBeNull();
        serialization.GetTypeInfo<SessionRequest>().Should().NotBeNull();
    }

    private sealed class FakeCurrentUser : ICurrentUser {
        public string UserId { get; init; } = string.Empty;
        public string? Email { get; init; }
        public IReadOnlyList<string> Roles { get; init; } = [];
        public bool IsAuthenticated { get; init; }
        public Dictionary<string, string[]> Claims { get; } = new();

        public bool IsInRole(string role) => Roles.Contains(role);

        public bool HasClaim(string type, string value) =>
            Claims.TryGetValue(type, out var values) && values.Contains(value);

        public IEnumerable<string> GetClaimValues(string type) =>
            Claims.TryGetValue(type, out var values) ? values : [];
    }

    private sealed class FakeFeatureFlags : Dictionary<string, bool>, IFeatureFlagService {
        public ValueTask<bool> IsEnabledAsync(string feature, CancellationToken ct = default) =>
            ValueTask.FromResult(TryGetValue(feature, out var value) && value);
    }

    private sealed class FakeFeatureVariants : Dictionary<string, string>, IFeatureVariantService {
        public ValueTask<string?> GetVariantAsync(string feature, CancellationToken ct = default) =>
            ValueTask.FromResult(TryGetValue(feature, out var value) ? value : null);
    }
}
