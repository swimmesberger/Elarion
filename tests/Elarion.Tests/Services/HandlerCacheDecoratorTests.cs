using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.Abstractions.Caching;
using Elarion.Caching;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elarion.Tests.Services;

public sealed class HandlerCacheDecoratorTests {
    [Fact]
    public async Task CacheInvalidationDecorator_WhenResultSucceeds_InvalidatesTags() {
        var cache = new RecordingHandlerCache();
        var inner = new StaticHandler<Request, Result<Response>>(new Response { Value = "ok" });
        var decorator = new CacheInvalidationDecorator<Request, Result<Response>>(
            inner,
            cache,
            new InvalidationPolicy());

        await decorator.HandleAsync(new Request(), CancellationToken.None);

        cache.InvalidatedTags.Should().Equal("items");
    }

    [Fact]
    public async Task CacheInvalidationDecorator_WhenResultFails_DoesNotInvalidateTags() {
        var cache = new RecordingHandlerCache();
        var inner = new StaticHandler<Request, Result<Response>>(AppError.Validation("invalid"));
        var decorator = new CacheInvalidationDecorator<Request, Result<Response>>(
            inner,
            cache,
            new InvalidationPolicy());

        await decorator.HandleAsync(new Request(), CancellationToken.None);

        cache.InvalidatedTags.Should().BeEmpty();
    }

    [Fact]
    public async Task CacheDecorator_WhenInvoked_DelegatesToHandlerCache() {
        var cache = new RecordingHandlerCache();
        var inner = new StaticHandler<Request, Result<Response>>(new Response { Value = "ok" });
        var decorator = new CacheDecorator<Request, Result<Response>>(
            inner,
            cache,
            new CachePolicy());

        var result = await decorator.HandleAsync(new Request { Id = 42 }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Value.Should().Be("ok");
        cache.CreatedKeys.Should().Equal("42");
    }

    [Fact]
    public async Task HybridHandlerCache_GetOrCreate_EmitsHitAndMissTraceSpans() {
        using var activities = new ActivityCollector(HandlerCacheTelemetry.ActivitySourceName);
        using var meters = new MeterCollector(HandlerCacheTelemetry.MeterName);
        await using var provider = CreateHybridCacheProvider();
        await using var scope = provider.CreateAsyncScope();
        var cache = scope.ServiceProvider.GetRequiredService<IHandlerCache>();
        var factoryCalls = 0;

        var first = await cache.GetOrCreateAsync(
            new CachePolicy(),
            new Request { Id = 7 },
            _ => {
                factoryCalls++;
                return ValueTask.FromResult("cached");
            },
            TestContext.Current.CancellationToken);
        var second = await cache.GetOrCreateAsync(
            new CachePolicy(),
            new Request { Id = 7 },
            _ => {
                factoryCalls++;
                return ValueTask.FromResult("not-used");
            },
            TestContext.Current.CancellationToken);

        first.Should().Be("cached");
        second.Should().Be("cached");
        factoryCalls.Should().Be(1);
        activities.Activities.Should().Contain(activity =>
            activity.DisplayName == "handler cache get" &&
            Equals(activity.GetTag("handler.cache.outcome"), "miss-factory-executed") &&
            Equals(activity.GetTag("handler.cache.factory_executed"), true));
        activities.Activities.Should().Contain(activity =>
            activity.DisplayName == "handler cache get" &&
            Equals(activity.GetTag("handler.cache.outcome"), "cached-or-coalesced") &&
            Equals(activity.GetTag("handler.cache.factory_executed"), false));
        meters.Measurements.Should().Contain(measurement =>
            measurement.InstrumentName == "handler.cache.operation.count" &&
            measurement.HasTag("handler.cache.operation", "get") &&
            measurement.HasTag("handler.cache.outcome", "cached-or-coalesced"));
    }

    [Fact]
    public async Task HybridHandlerCache_Invalidate_EmitsTraceSpan() {
        using var activities = new ActivityCollector(HandlerCacheTelemetry.ActivitySourceName);
        await using var provider = CreateHybridCacheProvider();
        await using var scope = provider.CreateAsyncScope();
        var cache = scope.ServiceProvider.GetRequiredService<IHandlerCache>();

        await cache.InvalidateAsync(new InvalidationPolicy(), TestContext.Current.CancellationToken);

        activities.Activities.Should().Contain(activity =>
            activity.DisplayName == "handler cache invalidate" &&
            Equals(activity.GetTag("handler.cache.operation"), "invalidate") &&
            Equals(activity.GetTag("handler.cache.outcome"), "success"));
    }

    private sealed record Request {
        public int Id { get; init; }
    }

    private sealed record Response {
        public required string Value { get; init; }
    }

    private sealed class StaticHandler<TRequest, TResponse>(TResponse response) : IHandler<TRequest, TResponse> {
        public ValueTask<TResponse> HandleAsync(TRequest request, CancellationToken ct) => ValueTask.FromResult(response);
    }

    private sealed class CachePolicy : IHandlerCachePolicy<Request> {
        public string KeyPrefix => "test";
        public TimeSpan Expiration => TimeSpan.FromMinutes(1);
        public HandlerCacheScope Scope => HandlerCacheScope.Global;
        public IReadOnlyList<string> Tags { get; } = ["items"];
        public string CreateKey(Request request) => request.Id.ToString();
    }

    private sealed class InvalidationPolicy : IHandlerCacheInvalidationPolicy {
        public HandlerCacheScope Scope => HandlerCacheScope.Global;
        public IReadOnlyList<string> Tags { get; } = ["items"];
    }

    private sealed class RecordingHandlerCache : IHandlerCache {
        public List<string> CreatedKeys { get; } = [];
        public List<string> InvalidatedTags { get; } = [];

        public async ValueTask<TResponse> GetOrCreateAsync<TRequest, TResponse>(
            IHandlerCachePolicy<TRequest> policy,
            TRequest request,
            Func<CancellationToken, ValueTask<TResponse>> factory,
            CancellationToken ct) {
            CreatedKeys.Add(policy.CreateKey(request));

            return await factory(ct);
        }

        public ValueTask InvalidateAsync(IHandlerCacheInvalidationPolicy policy, CancellationToken ct) {
            InvalidatedTags.AddRange(policy.Tags);

            return ValueTask.CompletedTask;
        }
    }

    private static ServiceProvider CreateHybridCacheProvider() {
        var services = new ServiceCollection();
        services.AddSingleton(new JsonSerializerOptions {
            TypeInfoResolver = new DefaultJsonTypeInfoResolver()
        });
        services.AddElarionHandlerCaching();
        return services.BuildServiceProvider();
    }
}
