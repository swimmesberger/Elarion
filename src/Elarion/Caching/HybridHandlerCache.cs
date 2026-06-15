using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Elarion.Abstractions;
using Elarion.Abstractions.Caching;
using Elarion.Abstractions.Identity;

namespace Elarion.Caching;

/// <summary>
/// Default <see cref="IHandlerCache"/> implementation backed by .NET <see cref="HybridCache"/>.
/// </summary>
public sealed class HybridHandlerCache(
    HybridCache cache,
    IServiceProvider services,
    JsonSerializerOptions jsonOptions
) : IHandlerCache {
    /// <inheritdoc />
    public async ValueTask<TResponse> GetOrCreateAsync<TRequest, TResponse>(
        IHandlerCachePolicy<TRequest> policy,
        TRequest request,
        Func<CancellationToken, ValueTask<TResponse>> factory,
        CancellationToken ct) {
        // Note 9: Policies own the logical cache key; this type turns it into a physical key with scope information.
        var key = CreatePhysicalKey(policy, request);
        var options = new HybridCacheEntryOptions {
            Expiration = policy.Expiration,
            LocalCacheExpiration = policy.Expiration,
        };
        // Note 10: Tags are how HybridCache supports group invalidation without knowing every individual key.
        var tags = CreatePhysicalTags(policy.Scope, policy.Tags);

        if (policy is IHandlerCachePayloadPolicy<TRequest, TResponse> payloadPolicy) {
            // Note 11: Payload policies store a serialized representation, useful when response types should not be cached directly.
            return await GetOrCreatePayloadAsync(payloadPolicy, factory, key, options, tags, ct);
        }

        try {
            return await cache.GetOrCreateAsync(
                key,
                factory,
                static async (state, token) => {
                    var response = await state(token);
                    if (response is IResultLike { IsSuccess: false }) {
                        // Note 12: Throwing here is a control-flow adapter for HybridCache: it prevents failed Result<T> values from being cached.
                        throw new NonCacheableResultException<TResponse>(response);
                    }

                    return response;
                },
                options,
                tags,
                cancellationToken: ct);
        } catch (NonCacheableResultException<TResponse> ex) {
            return ex.Response;
        }
    }

    /// <inheritdoc />
    public async ValueTask InvalidateAsync(IHandlerCacheInvalidationPolicy policy, CancellationToken ct) {
        // Note 13: Invalidating by tag keeps command handlers independent from the exact read keys they invalidate.
        var tags = CreatePhysicalTags(policy.Scope, policy.Tags);
        foreach (var tag in tags) {
            await cache.RemoveByTagAsync(tag, ct);
        }
    }

    private async ValueTask<TResponse> GetOrCreatePayloadAsync<TRequest, TResponse>(
        IHandlerCachePayloadPolicy<TRequest, TResponse> policy,
        Func<CancellationToken, ValueTask<TResponse>> factory,
        string key,
        HybridCacheEntryOptions options,
        IReadOnlyList<string> tags,
        CancellationToken ct) {
        try {
            var payload = await cache.GetOrCreateAsync(
                key,
                (factory, policy, jsonOptions),
                static async (state, token) => {
                    var response = await state.factory(token);
                    if (response is IResultLike { IsSuccess: false }) {
                        throw new NonCacheableResultException<TResponse>(response);
                    }

                    return state.policy.Serialize(response, state.jsonOptions);
                },
                options,
                tags,
                cancellationToken: ct);

            return policy.Deserialize(payload, jsonOptions);
        } catch (NonCacheableResultException<TResponse> ex) {
            return ex.Response;
        }
    }

    private string CreatePhysicalKey<TRequest>(IHandlerCachePolicy<TRequest> policy, TRequest request) {
        var scope = CreatePhysicalScope(policy.Scope);
        var requestKey = policy.CreateKey(request);

        // Note 14: Prefixing each part makes the key readable and avoids accidental collisions between scopes and request keys.
        return $"{policy.KeyPrefix}:scope:{scope}:request:{requestKey}";
    }

    private IReadOnlyList<string> CreatePhysicalTags(HandlerCacheScope scope, IReadOnlyList<string> tags) {
        if (tags.Count == 0) {
            throw new InvalidOperationException("Handler cache policies must define at least one tag.");
        }

        var physicalScope = CreatePhysicalScope(scope);
        var physicalTags = new string[tags.Count];

        for (var i = 0; i < tags.Count; i++) {
            var tag = tags[i];
            if (string.IsNullOrWhiteSpace(tag) || tag == "*") {
                throw new InvalidOperationException($"Invalid handler cache tag '{tag}'.");
            }

            physicalTags[i] = $"handler-cache:{physicalScope}:tag:{tag}";
        }

        return physicalTags;
    }

    private string CreatePhysicalScope(HandlerCacheScope scope) =>
        scope switch {
            HandlerCacheScope.Global => "global",
            // Note 15: User ids are hashed before entering cache keys so infrastructure logs do not expose raw identifiers.
            HandlerCacheScope.CurrentUser => $"user:{Hash(GetCurrentUserId())}",
            _ => throw new InvalidOperationException($"Unsupported handler cache scope '{scope}'."),
        };

    private string GetCurrentUserId() {
        var currentUser = services.GetRequiredService<ICurrentUser>();
        if (!currentUser.IsAuthenticated || string.IsNullOrWhiteSpace(currentUser.UserId)) {
            throw new InvalidOperationException("Current-user scoped handler caching requires an authenticated current user with a user id.");
        }

        return currentUser.UserId;
    }

    private static string Hash(string value) {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));

        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private sealed class NonCacheableResultException<TResponse>(TResponse response) : Exception {
        public TResponse Response { get; } = response;
    }
}
