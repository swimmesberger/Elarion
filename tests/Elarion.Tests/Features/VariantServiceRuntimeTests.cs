using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.Abstractions.Features;
using Elarion.Abstractions.Identity;
using Elarion.Abstractions.Pipeline;
using Elarion.Tests.Authorization;
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

    private sealed class CountingHandler : IHandler<ForecastCommand, Result<string>> {
        public int Calls { get; private set; }

        public ValueTask<Result<string>> HandleAsync(ForecastCommand request, CancellationToken ct) {
            Calls++;
            return new ValueTask<Result<string>>(Result<string>.Success("ok"));
        }
    }
}
