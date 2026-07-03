using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.Abstractions.Features;
using Elarion.Abstractions.Identity;
using Elarion.Abstractions.Pipeline;
using Elarion.Tests.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elarion.Tests.Features;

public sealed class VariantServiceRuntimeTests {
    [Fact]
    public async Task AsyncResolvedHandler_BuildsOnce_Caches_AndDelegates() {
        var ct = TestContext.Current.CancellationToken;
        var builds = 0;
        var inner = new CountingHandler();
        var proxy = new AsyncResolvedHandler<ForecastCommand, Result<string>>(
            scope: null!,
            build: async (_, _) => {
                builds++;
                await Task.Yield();
                return inner;
            });

        await proxy.HandleAsync(new ForecastCommand(), ct);
        await proxy.HandleAsync(new ForecastCommand(), ct);

        builds.Should().Be(1);
        inner.Calls.Should().Be(2);
    }

    [Fact]
    public async Task AsyncResolvedHandler_ConcurrentFirstCalls_BuildInnerExactlyOnce() {
        var ct = TestContext.Current.CancellationToken;
        var builds = 0;
        var gate = new TaskCompletionSource();
        var inner = new CountingHandler();
        var proxy = new AsyncResolvedHandler<ForecastCommand, Result<string>>(
            scope: null!,
            build: async (_, c) => {
                Interlocked.Increment(ref builds);
                // Hold both concurrent builders inside build() so they overlap on the first call.
                await gate.Task.WaitAsync(c);
                return inner;
            });

        var t1 = proxy.HandleAsync(new ForecastCommand(), ct).AsTask();
        var t2 = proxy.HandleAsync(new ForecastCommand(), ct).AsTask();
        gate.SetResult();
        await Task.WhenAll(t1, t2);

        builds.Should().Be(1);
        inner.Calls.Should().Be(2);
    }

    [Fact]
    public async Task Cache_ConcurrentWarm_ResolvesVariantExactlyOnce() {
        var ct = TestContext.Current.CancellationToken;
        var services = new ServiceCollection();
        var provider = new CountingVariantProvider();
        services.AddSingleton<IVariantServiceProvider<IAlgorithm>>(provider);
        using var sp = services.BuildServiceProvider();
        var cache = new VariantResolutionCache();

        var tasks = Enumerable.Range(0, 16)
            .Select(_ => cache.WarmAsync<IAlgorithm>(sp, ct).AsTask())
            .ToArray();
        await Task.WhenAll(tasks);

        provider.Resolutions.Should().Be(1);
        cache.Get<IAlgorithm>().Should().BeSameAs(provider.Instance);
    }

    [Fact]
    public async Task Provider_ResolvesAllocatedVariant_AndFallsBackToDefault() {
        var ct = TestContext.Current.CancellationToken;

        (await ResolveProvider("u-A").GetAsync(ct)).Name.Should().Be("neural");
        // Unallocated user → the provider returns null for the variant → falls back to the default impl.
        (await ResolveProvider("u-B").GetAsync(ct)).Name.Should().Be("linear");
    }

    [Fact]
    public void Cache_Get_Throws_WhenNotWarmed() {
        var cache = new VariantResolutionCache();

        var act = () => cache.Get<IAlgorithm>();

        act.Should().Throw<InvalidOperationException>().WithMessage("*IVariantServiceProvider*");
    }

    [Fact]
    public async Task TransparentInjection_HandlerGetsTheUsersVariant() {
        var ct = TestContext.Current.CancellationToken;

        (await RunForecastFor("u-A", ct)).Should().Be("neural");
        (await RunForecastFor("u-B", ct)).Should().Be("linear");
    }

    [Fact]
    public void ConfigurationVariant_TransparentInjection_SelectsConfiguredImplementation() {
        using var provider = BuildConfigurationVariantServices(BuildConfiguration("neural")).BuildServiceProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<IAlgorithm>().Should().BeOfType<NeuralAlgorithm>();
    }

    [Fact]
    public void ConfigurationVariant_FallsBackToDefault_WhenKeyAbsentOrValueUnknown() {
        using var absent = BuildConfigurationVariantServices(BuildConfiguration(null)).BuildServiceProvider();
        using (var scope = absent.CreateScope()) {
            scope.ServiceProvider.GetRequiredService<IAlgorithm>().Should().BeOfType<LinearAlgorithm>();
        }

        using var unknown = BuildConfigurationVariantServices(BuildConfiguration("does-not-exist")).BuildServiceProvider();
        using (var scope = unknown.CreateScope()) {
            scope.ServiceProvider.GetRequiredService<IAlgorithm>().Should().BeOfType<LinearAlgorithm>();
        }
    }

    [Fact]
    public void ConfigurationVariant_MatchesConfiguredValueCaseInsensitively() {
        // Variant keys are registered lower-case; a human-typed "NeUrAl" still selects the "neural" variant.
        using var provider = BuildConfigurationVariantServices(BuildConfiguration("NeUrAl")).BuildServiceProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<IAlgorithm>().Should().BeOfType<NeuralAlgorithm>();
    }

    [Fact]
    public void ConfigurationVariant_NewScopeObservesChangedValue_WhileOpenScopeStaysPinned() {
        var configuration = BuildConfiguration(null);
        using var provider = BuildConfigurationVariantServices(configuration).BuildServiceProvider();

        using var firstScope = provider.CreateScope();
        var initial = firstScope.ServiceProvider.GetRequiredService<IAlgorithm>();
        initial.Should().BeOfType<LinearAlgorithm>();

        configuration["Forecast:Algorithm"] = "neural";

        // The open scope keeps the implementation it started with (scoped resolution is cached per scope)...
        firstScope.ServiceProvider.GetRequiredService<IAlgorithm>().Should().BeSameAs(initial);

        // ...while the next scope observes the changed value — no restart, no re-registration.
        using var secondScope = provider.CreateScope();
        secondScope.ServiceProvider.GetRequiredService<IAlgorithm>().Should().BeOfType<NeuralAlgorithm>();
    }

    [Fact]
    public async Task ConfigurationVariantProvider_ImperativeAccess_ResolvesLikeTransparentInjection() {
        var ct = TestContext.Current.CancellationToken;
        using var provider = BuildConfigurationVariantServices(BuildConfiguration("neural")).BuildServiceProvider();
        using var scope = provider.CreateScope();
        var variants = scope.ServiceProvider.GetRequiredService<IVariantServiceProvider<IAlgorithm>>();

        (await variants.GetAsync(ct)).Should().BeOfType<NeuralAlgorithm>();
    }

    [Fact]
    public async Task ConfigurationVariant_Throws_WhenNothingMatchesAndNoDefaultRegistered() {
        var ct = TestContext.Current.CancellationToken;
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(BuildConfiguration(null));
        services.AddElarionConfigurationVariantService<IAlgorithm>("Forecast:Algorithm", defaultKey: null);
        services.AddKeyedScoped<IAlgorithm, NeuralAlgorithm>("neural");
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var transparent = () => scope.ServiceProvider.GetRequiredService<IAlgorithm>();
        transparent.Should().Throw<InvalidOperationException>().WithMessage("*Forecast:Algorithm*");

        var variants = scope.ServiceProvider.GetRequiredService<IVariantServiceProvider<IAlgorithm>>();
        (await variants.GetOrDefaultAsync(ct)).Should().BeNull();
    }

    private static IVariantServiceProvider<IAlgorithm> ResolveProvider(string userId) {
        var provider = BuildVariantServices(userId).BuildServiceProvider();
        return provider.CreateScope().ServiceProvider.GetRequiredService<IVariantServiceProvider<IAlgorithm>>();
    }

    private static async Task<string> RunForecastFor(string userId, CancellationToken ct) {
        var services = BuildVariantServices(userId);
        services.AddScoped<RunForecast>();
        // Mimics the generated registration for a variant-dependent handler: the async-resolving proxy warms the
        // variant before the (synchronous) DI construction reads the transparent IAlgorithm registration.
        services.AddScoped<IHandler<ForecastCommand, Result<string>>>(sp =>
            new AsyncResolvedHandler<ForecastCommand, Result<string>>(sp, static async (s, c) => {
                await s.GetRequiredService<VariantResolutionCache>().WarmAsync<IAlgorithm>(s, c);
                return s.GetRequiredService<RunForecast>();
            }));

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<IHandler<ForecastCommand, Result<string>>>();

        var result = await handler.HandleAsync(new ForecastCommand(), ct);
        return result.Value;
    }

    private static ServiceCollection BuildVariantServices(string userId) {
        var services = new ServiceCollection();
        services.AddSingleton<ICurrentUser>(new FakeCurrentUser { IsAuthenticated = true, UserId = userId });
        services.AddScoped<IFeatureVariantService, FakeVariantService>();
        services.AddElarionVariantService<IAlgorithm>("ForecastAlgorithm");
        services.AddKeyedScoped<IAlgorithm, NeuralAlgorithm>("neural");
        services.AddKeyedScoped<IAlgorithm, LinearAlgorithm>(VariantServiceKeys.Default);
        return services;
    }

    private static ServiceCollection BuildConfigurationVariantServices(IConfiguration configuration) {
        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        services.AddElarionConfigurationVariantService<IAlgorithm>("Forecast:Algorithm");
        services.AddKeyedScoped<IAlgorithm, NeuralAlgorithm>("neural");
        services.AddKeyedScoped<IAlgorithm, LinearAlgorithm>(VariantServiceKeys.Default);
        return services;
    }

    private static IConfiguration BuildConfiguration(string? algorithm) {
        var values = new Dictionary<string, string?>();
        if (algorithm is not null) {
            values["Forecast:Algorithm"] = algorithm;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    // Allocates "neural" only to user u-A; everyone else gets no variant (→ default fallback).
    private sealed class FakeVariantService(ICurrentUser currentUser) : IFeatureVariantService {
        public ValueTask<string?> GetVariantAsync(string feature, CancellationToken ct = default) =>
            new(currentUser.UserId == "u-A" ? "neural" : null);
    }

    private interface IAlgorithm {
        string Name { get; }
    }

    private sealed class NeuralAlgorithm : IAlgorithm {
        public string Name => "neural";
    }

    private sealed class LinearAlgorithm : IAlgorithm {
        public string Name => "linear";
    }

    private sealed record ForecastCommand;

    private sealed class RunForecast(IAlgorithm algorithm) : IHandler<ForecastCommand, Result<string>> {
        public ValueTask<Result<string>> HandleAsync(ForecastCommand request, CancellationToken ct) =>
            new(Result<string>.Success(algorithm.Name));
    }

    private sealed class CountingVariantProvider : IVariantServiceProvider<IAlgorithm> {
        private int _resolutions;

        public IAlgorithm Instance { get; } = new NeuralAlgorithm();

        public int Resolutions => Volatile.Read(ref _resolutions);

        public async ValueTask<IAlgorithm> GetAsync(CancellationToken ct = default) {
            Interlocked.Increment(ref _resolutions);
            await Task.Yield();
            return Instance;
        }

        public async ValueTask<IAlgorithm?> GetOrDefaultAsync(CancellationToken ct = default) =>
            await GetAsync(ct);
    }

    private sealed class CountingHandler : IHandler<ForecastCommand, Result<string>> {
        private int _calls;

        // Incremented from concurrently resumed continuations, so the count must be atomic.
        public int Calls => Volatile.Read(ref _calls);

        public ValueTask<Result<string>> HandleAsync(ForecastCommand request, CancellationToken ct) {
            Interlocked.Increment(ref _calls);
            return new ValueTask<Result<string>>(Result<string>.Success("ok"));
        }
    }
}
