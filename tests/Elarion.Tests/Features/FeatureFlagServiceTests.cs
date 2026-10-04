using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.Abstractions.Authorization;
using Elarion.Abstractions.Features;
using Elarion.Abstractions.Identity;
using Elarion.Abstractions.Modules;
using Elarion.Abstractions.MultiTenancy;
using Elarion.Abstractions.Pipeline;
using Elarion.Features;
using Elarion.Pipeline;
using Elarion.Session;
using Elarion.Tests.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Elarion.Tests.Features;

public sealed class FeatureFlagServiceTests {
    private static ServiceProvider Build(
        Action<IServiceCollection> configure,
        ICurrentUser? user = null,
        ITenantContext? tenant = null,
        List<(LogLevel Level, string Message)>? log = null) {
        var services = new ServiceCollection();
        services.AddLogging(builder => {
            if (log is not null) builder.AddProvider(new CapturingLoggerProvider(log));
        });
        services.AddSingleton(user ?? new FakeCurrentUser { IsAuthenticated = true, UserId = "u-1" });
        if (tenant is not null) services.AddSingleton(tenant);
        configure(services);
        return services.BuildServiceProvider();
    }

    private static IFeatureFlagService Flags(IServiceScope scope) {
        return scope.ServiceProvider.GetRequiredService<IFeatureFlagService>();
    }

    [Fact]
    public async Task CodeOwnedFlag_IsAnsweredByItsResolver_AndNeverReachesTheBackend() {
        var ct = TestContext.Current.CancellationToken;
        var backend = new RecordingBackend();
        using var provider = Build(services => {
            services.AddElarionFeatureFlag<AuthenticatedOnlyFlag>("members-only", "Billing");
            services.AddSingleton<IBackendFeatureFlagEvaluator>(backend);
        });
        using var scope = provider.CreateScope();

        (await Flags(scope).IsEnabledAsync("members-only", ct)).Should().BeTrue();

        backend.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task BackendOwnedFlag_IsAnsweredByTheBackend_WithItsNameAndTheAmbientContext() {
        var ct = TestContext.Current.CancellationToken;
        var backend = new RecordingBackend { Enabled = { ["new-export"] = true } };
        using var provider = Build(services => {
            services.AddElarionBackendFeatureFlag("new-export", "Billing");
            services.AddSingleton<IBackendFeatureFlagEvaluator>(backend);
        }, new FakeCurrentUser { IsAuthenticated = true, UserId = "u-9", Roles = ["admin"] });
        using var scope = provider.CreateScope();

        (await Flags(scope).IsEnabledAsync("new-export", ct)).Should().BeTrue();

        var call = backend.Calls.Should().ContainSingle().Subject;
        call.Flag.Should().Be("new-export");
        call.Context.UserId.Should().Be("u-9");
        call.Context.Roles.Should().Equal("admin");
    }

    [Fact]
    public async Task ExplicitContext_EvaluatesForTheSuppliedSubject_NotTheAmbientUser() {
        var ct = TestContext.Current.CancellationToken;
        using var provider = Build(services =>
                services.AddElarionFeatureFlag<VipOnlyFlag>("vip-only", "Billing"),
            new FakeCurrentUser { IsAuthenticated = true, UserId = "u-ordinary" });
        using var scope = provider.CreateScope();
        var flags = Flags(scope);

        (await flags.IsEnabledAsync("vip-only", ct)).Should().BeFalse();

        var vip = flags.CreateContext() with { UserId = "u-vip" };
        (await flags.IsEnabledAsync("vip-only", vip, ct)).Should().BeTrue();
        // The ambient answer is unchanged by the explicit evaluation.
        (await flags.IsEnabledAsync("vip-only", ct)).Should().BeFalse();
    }

    [Fact]
    public void AmbientContext_CarriesUserRolesAndTenant_AndIsAnonymousWithoutAnAuthenticatedUser() {
        using var provider = Build(
            services => services.AddElarionBackendFeatureFlag("x", "Billing"),
            new FakeCurrentUser { IsAuthenticated = true, UserId = "u-1", Roles = ["admin", "auditor"] },
            new StubTenantContext("tenant-7"));
        using var scope = provider.CreateScope();

        var context = Flags(scope).CreateContext();

        context.UserId.Should().Be("u-1");
        context.IsAuthenticated.Should().BeTrue();
        context.Roles.Should().Equal("admin", "auditor");
        context.TenantId.Should().Be("tenant-7");
        context.Services.Should().BeSameAs(scope.ServiceProvider);

        using var anonymousProvider = Build(
            services => services.AddElarionBackendFeatureFlag("x", "Billing"),
            new FakeCurrentUser { IsAuthenticated = false },
            new StubTenantContext("tenant-7", systemScope: true));
        using var anonymousScope = anonymousProvider.CreateScope();

        var anonymous = Flags(anonymousScope).CreateContext();
        anonymous.UserId.Should().BeNull();
        anonymous.IsAuthenticated.Should().BeFalse();
        // A system scope spans every tenant, so no tenant targets the evaluation.
        anonymous.TenantId.Should().BeNull();
    }

    [Fact]
    public async Task ResolverReceivesTheScopeServices_AndCanInjectScopedDependencies() {
        var ct = TestContext.Current.CancellationToken;
        using var provider = Build(services => {
            services.AddScoped<RosterService>();
            services.AddElarionFeatureFlag<RosterFlag>("roster-flag", "Billing");
        });
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<RosterService>().Members.Add("u-1");

        (await Flags(scope).IsEnabledAsync("roster-flag", ct)).Should().BeTrue();
    }

    [Fact]
    public async Task UnknownFlag_IsDisabled_AndLoggedOncePerName() {
        var ct = TestContext.Current.CancellationToken;
        var log = new List<(LogLevel, string)>();
        using var provider = Build(
            services => services.AddElarionBackendFeatureFlag("declared", "Billing"), log: log);
        using var scope = provider.CreateScope();
        var flags = Flags(scope);
        var name = $"undeclared-{Guid.CreateVersion7()}";

        (await flags.IsEnabledAsync(name, ct)).Should().BeFalse();
        (await flags.IsEnabledAsync(name, ct)).Should().BeFalse();
        (await flags.GetVariantAsync(name, ct)).Should().BeNull();

        log.Where(entry => entry.Item2.Contains(name)).Should().ContainSingle()
            .Which.Item1.Should().Be(LogLevel.Warning);
    }

    [Fact]
    public async Task Variants_AreResolvedByTheSameOwner() {
        var ct = TestContext.Current.CancellationToken;
        using var provider = Build(services => {
            services.AddElarionFeatureFlag<AlgoVariantFlag>("algo", "Billing");
            services.AddElarionBackendFeatureFlag("backend-algo", "Billing");
            services.AddSingleton<IBackendFeatureFlagEvaluator>(new RecordingBackend {
                Variants = { ["backend-algo"] = "neural" }
            });
        });
        using var scope = provider.CreateScope();
        var flags = Flags(scope);

        (await flags.GetVariantAsync("algo", ct)).Should().Be("linear");
        (await flags.GetVariantAsync("backend-algo", ct)).Should().Be("neural");
    }

    [Fact]
    public async Task PlainResolver_HasNoVariant() {
        var ct = TestContext.Current.CancellationToken;
        using var provider = Build(services => services.AddElarionFeatureFlag<AuthenticatedOnlyFlag>("members-only", "Billing"));
        using var scope = provider.CreateScope();

        (await Flags(scope).GetVariantAsync("members-only", ct)).Should().BeNull();
    }

    [Fact]
    public async Task BackendFlagWithoutARegisteredBackend_FailsLoudlyAtEvaluation() {
        var ct = TestContext.Current.CancellationToken;
        using var provider = Build(services => services.AddElarionBackendFeatureFlag("new-export", "Billing"));
        using var scope = provider.CreateScope();

        var act = async () => await Flags(scope).IsEnabledAsync("new-export", ct);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*IBackendFeatureFlagEvaluator*");
    }

    [Fact]
    public void Catalog_ListsDeclaredFlagsSortedByName_WithDescriptorData() {
        using var provider = Build(services => {
            services.AddElarionBackendFeatureFlag("zeta", "Billing", "The zeta flag", exposeToClient: true);
            services.AddElarionFeatureFlag<AuthenticatedOnlyFlag>("alpha", "Clients");
        });

        var catalog = provider.GetRequiredService<IFeatureFlagCatalog>();

        catalog.All.Select(f => f.Name).Should().Equal("alpha", "zeta");
        catalog.TryGet("zeta", out var zeta).Should().BeTrue();
        zeta.Should().BeEquivalentTo(new FeatureFlagDescriptor {
            Name = "zeta", Module = "Billing", Description = "The zeta flag", ExposeToClient = true,
            Owner = FeatureFlagOwner.Backend
        });
        catalog.All[0].Owner.Should().Be(FeatureFlagOwner.Code);
        catalog.TryGet("missing", out _).Should().BeFalse();
    }

    [Fact]
    public void FlagRegisteredTwice_FailsCatalogConstruction() {
        using var provider = Build(services => {
            services.AddElarionBackendFeatureFlag("dup", "Billing");
            services.AddElarionFeatureFlag<AuthenticatedOnlyFlag>("dup", "Clients");
        });

        var act = () => provider.GetRequiredService<IFeatureFlagCatalog>();

        act.Should().Throw<InvalidOperationException>().WithMessage("*'dup'*declared twice*");
    }

    [Fact]
    public async Task Startup_FailsWhenBackendFlagsAreDeclaredWithoutABackend() {
        var ct = TestContext.Current.CancellationToken;
        using var withoutBackend = Build(services => services.AddElarionBackendFeatureFlag("new-export", "Billing"));
        var validator = withoutBackend.GetServices<IHostedService>().Single();

        var act = async () => await validator.StartAsync(ct);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*'new-export'*");

        using var withBackend = Build(services => {
            services.AddElarionBackendFeatureFlag("new-export", "Billing");
            services.AddScoped<IBackendFeatureFlagEvaluator, RecordingBackend>();
        });
        await withBackend.GetServices<IHostedService>().Single().StartAsync(ct);

        using var codeOnly = Build(services => services.AddElarionFeatureFlag<AuthenticatedOnlyFlag>("members-only", "Billing"));
        await codeOnly.GetServices<IHostedService>().Single().StartAsync(ct);
    }

    [Fact]
    public async Task GateAndSessionSnapshot_EvaluateThroughTheSameCatalogAndAgree() {
        var ct = TestContext.Current.CancellationToken;
        using var provider = Build(services => {
            services.AddElarionFeatureFlag<VipOnlyFlag>("vip-only", "Billing", exposeToClient: true);
            services.AddElarionFeatureFlag<AuthenticatedOnlyFlag>("internal-flag", "Billing");
        }, new FakeCurrentUser { IsAuthenticated = true, UserId = "u-vip" });
        using var scope = provider.CreateScope();
        var sp = scope.ServiceProvider;

        var gate = new FeatureGateDecorator<GatedCommand, Result<string>>(
            new EchoHandler(),
            new HandlerMetadata(typeof(VipGatedHandler), typeof(GatedCommand), typeof(Result<string>)),
            sp.GetRequiredService<IFeatureFlagService>());
        var gated = await gate.HandleAsync(new GatedCommand(), ct);

        var session = new SessionHandler(
            sp.GetRequiredService<ICurrentUser>(),
            new ClientCapabilityManifest { Modules = [new ClientModuleManifest { Name = "Billing", Enabled = true }] },
            null,
            sp.GetRequiredService<IFeatureFlagCatalog>(),
            sp.GetRequiredService<IFeatureFlagService>());
        var snapshot = (await session.HandleAsync(new SessionRequest(), ct)).Value!;

        gated.IsSuccess.Should().BeTrue();
        snapshot.Flags.Should().Contain("vip-only", true);
        // An internal flag is evaluable by gates but never projected to the client.
        snapshot.Flags.Should().NotContainKey("internal-flag");
    }

    [Fact]
    public async Task SessionSnapshot_ProjectsOwnerAllocatedVariants() {
        var ct = TestContext.Current.CancellationToken;
        using var provider = Build(services => {
            services.AddElarionFeatureFlag<AlgoVariantFlag>("algo", "Billing", exposeToClient: true);
        });
        using var scope = provider.CreateScope();
        var sp = scope.ServiceProvider;

        var session = new SessionHandler(
            sp.GetRequiredService<ICurrentUser>(),
            ClientCapabilityManifest.Empty,
            null,
            sp.GetRequiredService<IFeatureFlagCatalog>(),
            sp.GetRequiredService<IFeatureFlagService>());
        var snapshot = (await session.HandleAsync(new SessionRequest(), ct)).Value!;

        snapshot.Flags.Should().Contain("algo", true);
        snapshot.Variants.Should().Contain("algo", "linear");
    }

    [Fact]
    public async Task VariantServiceProvider_SelectsTheImplementationAllocatedByTheFlagOwner() {
        var ct = TestContext.Current.CancellationToken;
        using var provider = Build(services => {
            services.AddElarionFeatureFlag<AlgoVariantFlag>("algo", "Billing");
            services.AddElarionVariantService<IAlgo>("algo");
            services.AddKeyedScoped<IAlgo, LinearAlgo>("linear");
            services.AddKeyedScoped<IAlgo, DefaultAlgo>(VariantServiceKeys.Default);
        });
        using var scope = provider.CreateScope();

        var algo = await scope.ServiceProvider.GetRequiredService<IVariantServiceProvider<IAlgo>>().GetAsync(ct);

        algo.Should().BeOfType<LinearAlgo>();
    }

    private sealed class AuthenticatedOnlyFlag : IFeatureFlagResolver {
        public ValueTask<bool> IsEnabledAsync(FeatureEvaluationContext context, CancellationToken ct) {
            return ValueTask.FromResult(context.IsAuthenticated);
        }
    }

    private sealed class VipOnlyFlag : IFeatureFlagResolver {
        public ValueTask<bool> IsEnabledAsync(FeatureEvaluationContext context, CancellationToken ct) {
            return ValueTask.FromResult(context.UserId == "u-vip");
        }
    }

    private sealed class RosterService {
        public HashSet<string> Members { get; } = [];
    }

    private sealed class RosterFlag : IFeatureFlagResolver {
        public ValueTask<bool> IsEnabledAsync(FeatureEvaluationContext context, CancellationToken ct) {
            var roster = context.Services.GetRequiredService<RosterService>();
            return ValueTask.FromResult(context.UserId is { } id && roster.Members.Contains(id));
        }
    }

    private sealed class AlgoVariantFlag : IFeatureFlagResolver {
        public ValueTask<bool> IsEnabledAsync(FeatureEvaluationContext context, CancellationToken ct) {
            return ValueTask.FromResult(true);
        }

        public ValueTask<string?> GetVariantAsync(FeatureEvaluationContext context, CancellationToken ct) {
            return ValueTask.FromResult<string?>("linear");
        }
    }

    private interface IAlgo;

    private sealed class LinearAlgo : IAlgo;

    private sealed class DefaultAlgo : IAlgo;

    private sealed class RecordingBackend : IBackendFeatureFlagEvaluator {
        public Dictionary<string, bool> Enabled { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> Variants { get; } = new(StringComparer.Ordinal);
        public List<(string Flag, FeatureEvaluationContext Context)> Calls { get; } = [];

        public ValueTask<bool> IsEnabledAsync(string flag, FeatureEvaluationContext context, CancellationToken ct) {
            Calls.Add((flag, context));
            return ValueTask.FromResult(Enabled.TryGetValue(flag, out var enabled) && enabled);
        }

        public ValueTask<string?> GetVariantAsync(string flag, FeatureEvaluationContext context, CancellationToken ct) {
            Calls.Add((flag, context));
            return ValueTask.FromResult(Variants.TryGetValue(flag, out var variant) ? variant : null);
        }
    }

    private sealed class StubTenantContext(string? tenantId, bool systemScope = false) : ITenantContext {
        public string? TenantId { get; } = tenantId;
        public bool IsSystemScope { get; } = systemScope;

        public IDisposable SystemScope() {
            throw new NotSupportedException();
        }

        public IDisposable Scope(string tenantId) {
            throw new NotSupportedException();
        }
    }

    [FeatureGate("vip-only")]
    private sealed class VipGatedHandler;

    private sealed record GatedCommand;

    private sealed class EchoHandler : IHandler<GatedCommand, Result<string>> {
        public ValueTask<Result<string>> HandleAsync(GatedCommand request, CancellationToken ct) {
            return ValueTask.FromResult(Result<string>.Success("ok"));
        }
    }

    private sealed class CapturingLoggerProvider(List<(LogLevel Level, string Message)> entries) : ILoggerProvider {
        public ILogger CreateLogger(string categoryName) {
            return new CapturingLogger(entries);
        }

        public void Dispose() {
        }

        private sealed class CapturingLogger(List<(LogLevel Level, string Message)> entries) : ILogger {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull {
                return null;
            }

            public bool IsEnabled(LogLevel logLevel) {
                return true;
            }

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) {
                lock (entries) {
                    entries.Add((logLevel, formatter(state, exception)));
                }
            }
        }
    }
}
