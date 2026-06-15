using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.Abstractions.Caching;
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
}
