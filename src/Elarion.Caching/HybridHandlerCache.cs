using System.Security.Cryptography;
using System.Diagnostics;
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
        using var activity = HandlerCacheTelemetry.Source.StartActivity("handler cache get", ActivityKind.Internal);
        var started = Stopwatch.GetTimestamp();
        var outcome = "unknown";
        SetCacheTags(activity, "get", policy.KeyPrefix, policy.Scope, policy.Tags.Count);

        try {
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
                var payloadState = new PayloadFactoryState<TRequest, TResponse>(payloadPolicy, factory, jsonOptions, activity);
                try {
                    var response = await GetOrCreatePayloadAsync(payloadState, key, options, tags, ct);
                    outcome = payloadState.FactoryExecuted ? "miss-factory-executed" : "cached-or-coalesced";
                    return response;
                } catch (NonCacheableResultException<TResponse> ex) {
                    outcome = "miss-non-cacheable";
                    return ex.Response;
                }
            }

            var state = new FactoryState<TResponse>(factory, activity);
            try {
                var response = await cache.GetOrCreateAsync(
                    key,
                    state,
                    static async (state, token) => {
                        state.MarkFactoryExecuted();
                        var response = await state.Factory(token);
                        if (response is IResultLike { IsSuccess: false }) {
                            // Note 12: Throwing here is a control-flow adapter for HybridCache: it prevents failed Result<T> values from being cached.
                            throw new NonCacheableResultException<TResponse>(response);
                        }

                        return response;
                    },
                    options,
                    tags,
                    cancellationToken: ct);
                outcome = state.FactoryExecuted ? "miss-factory-executed" : "cached-or-coalesced";
                return response;
            } catch (NonCacheableResultException<TResponse> ex) {
                outcome = "miss-non-cacheable";
                return ex.Response;
            }
        } catch (Exception ex) {
            outcome = "error";
            RecordException(activity, ex);
            throw;
        } finally {
            RecordCacheOutcome(activity, "get", outcome, started);
        }
    }

    /// <inheritdoc />
    public async ValueTask InvalidateAsync(IHandlerCacheInvalidationPolicy policy, CancellationToken ct) {
        using var activity = HandlerCacheTelemetry.Source.StartActivity("handler cache invalidate", ActivityKind.Internal);
        var started = Stopwatch.GetTimestamp();
        var outcome = "success";
        SetCacheTags(activity, "invalidate", null, policy.Scope, policy.Tags.Count);

        try {
            // Note 13: Invalidating by tag keeps command handlers independent from the exact read keys they invalidate.
            var tags = CreatePhysicalTags(policy.Scope, policy.Tags);
            foreach (var tag in tags) {
                await cache.RemoveByTagAsync(tag, ct);
            }
        } catch (Exception ex) {
            outcome = "error";
            RecordException(activity, ex);
            throw;
        } finally {
            RecordCacheOutcome(activity, "invalidate", outcome, started);
        }
    }

    private async ValueTask<TResponse> GetOrCreatePayloadAsync<TRequest, TResponse>(
        PayloadFactoryState<TRequest, TResponse> payloadState,
        string key,
        HybridCacheEntryOptions options,
        IReadOnlyList<string> tags,
        CancellationToken ct) {
        var payload = await cache.GetOrCreateAsync(
            key,
            payloadState,
            static async (state, token) => {
                state.MarkFactoryExecuted();
                var response = await state.Factory(token);
                if (response is IResultLike { IsSuccess: false }) {
                    throw new NonCacheableResultException<TResponse>(response);
                }

                return state.Policy.Serialize(response, state.JsonOptions);
            },
            options,
            tags,
            cancellationToken: ct);

        return payloadState.Policy.Deserialize(payload, jsonOptions);
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

    private static void SetCacheTags(
        Activity? activity,
        string operation,
        string? keyPrefix,
        HandlerCacheScope scope,
        int tagCount) {
        if (activity?.IsAllDataRequested != true) {
            return;
        }

        activity.SetTag("handler.cache.operation", operation);
        if (!string.IsNullOrWhiteSpace(keyPrefix)) {
            activity.SetTag("handler.cache.policy_prefix", keyPrefix);
        }

        activity.SetTag("handler.cache.scope", scope.ToString());
        activity.SetTag("handler.cache.tag_count", tagCount);
    }

    private static void RecordCacheOutcome(Activity? activity, string operation, string outcome, long started) {
        if (activity?.IsAllDataRequested == true) {
            activity.SetTag("handler.cache.outcome", outcome);
            activity.SetTag("handler.cache.factory_executed", outcome == "miss-factory-executed" || outcome == "miss-non-cacheable");

            if (outcome == "error") {
                activity.SetStatus(ActivityStatusCode.Error);
            }
        }

        HandlerCacheTelemetry.RecordOperation(
            operation,
            outcome,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    private static void RecordException(Activity? activity, Exception exception) {
        activity?.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection {
            { "exception.type", exception.GetType().FullName },
            { "exception.message", exception.Message }
        }));
        activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
    }

    private sealed class NonCacheableResultException<TResponse>(TResponse response) : Exception {
        public TResponse Response { get; } = response;
    }

    private class FactoryState<TResponse>(
        Func<CancellationToken, ValueTask<TResponse>> factory,
        Activity? activity) {
        public Func<CancellationToken, ValueTask<TResponse>> Factory { get; } = factory;

        public bool FactoryExecuted { get; private set; }

        public void MarkFactoryExecuted() {
            FactoryExecuted = true;
            activity?.AddEvent(new ActivityEvent("cache factory executed"));
        }
    }

    private sealed class PayloadFactoryState<TRequest, TResponse>(
        IHandlerCachePayloadPolicy<TRequest, TResponse> policy,
        Func<CancellationToken, ValueTask<TResponse>> factory,
        JsonSerializerOptions jsonOptions,
        Activity? activity) : FactoryState<TResponse>(factory, activity) {
        public IHandlerCachePayloadPolicy<TRequest, TResponse> Policy { get; } = policy;

        public JsonSerializerOptions JsonOptions { get; } = jsonOptions;
    }
}
