using System.Security.Cryptography;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Elarion.Abstractions;
using Elarion.Abstractions.Caching;
using Elarion.Abstractions.Identity;
using Elarion.Abstractions.MultiTenancy;
using Elarion.Abstractions.Serialization;

namespace Elarion.Caching;

/// <summary>
/// Default <see cref="IHandlerCache"/> implementation backed by .NET <see cref="HybridCache"/>.
/// </summary>
/// <remarks>
/// When ambient tenant scoping is registered (an <see cref="ITenantContext"/> resolves), the tenant in scope is
/// part of every physical key and tag. A cache hit never runs the handler, so it never runs the tenant read filter
/// either: only the key keeps one tenant's result from answering another. A
/// <see cref="ITenantContext.SystemScope">system scope</see> gets a namespace of its own, and a scope with no
/// tenant resolved bypasses the cache. Without tenant scoping, keys and tags are exactly what they were before it
/// existed.
/// </remarks>
public sealed class HybridHandlerCache(
    HybridCache cache,
    IServiceProvider services,
    IElarionJsonSerialization jsonSerialization
) : IHandlerCache {
    private const string GlobalScope = "global";
    private const string SystemTenantSegment = "tenant:system";

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
            var physicalScope = CreatePhysicalScope(policy.Scope);
            ValidateTags(policy.Tags);
            var tenant = ResolveTenant();
            if (tenant.Kind == TenantKind.Unresolved) {
                // Fails closed, like the tenant read filter: with no tenant there is no namespace that names whose
                // result this is. A shared "no tenant" namespace would be poisoned by a handler that enters a
                // tenant itself, so the cache stays out of the way and the handler answers every such call.
                var bypassed = await factory(ct);
                outcome = "bypass-no-tenant";
                return bypassed;
            }

            // Policies own the logical cache key; this type turns it into a physical key with tenant and scope
            // information.
            var key = CreatePhysicalKey(policy.KeyPrefix, tenant, physicalScope, policy.CreateKey(request));
            var options = new HybridCacheEntryOptions {
                Expiration = policy.Expiration,
                LocalCacheExpiration = policy.Expiration
            };
            // Tags are how HybridCache supports group invalidation without knowing every individual key.
            var tags = CreateEntryTags(tenant, policy.Scope, physicalScope, policy.Tags);

            if (policy is IHandlerCachePayloadPolicy<TRequest, TResponse> payloadPolicy) {
                // Payload policies store a serialized representation, useful when response types should not be cached directly.
                var payloadState =
                    new PayloadFactoryState<TRequest, TResponse>(payloadPolicy, factory, jsonSerialization.Options,
                        activity);
                try {
                    var response = await GetOrCreatePayloadAsync(payloadState, key, options, tags, ct);
                    outcome = payloadState.FactoryExecuted ? "miss-factory-executed" : "cached-or-coalesced";
                    return response;
                }
                catch (NonCacheableResultException<TResponse> ex) {
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
                        if (response is IResultLike { IsSuccess: false })
                            // Throwing here is a control-flow adapter for HybridCache: it prevents failed Result<T> values from being cached.
                            throw new NonCacheableResultException<TResponse>(response);

                        return response;
                    },
                    options,
                    tags,
                    ct);
                outcome = state.FactoryExecuted ? "miss-factory-executed" : "cached-or-coalesced";
                return response;
            }
            catch (NonCacheableResultException<TResponse> ex) {
                outcome = "miss-non-cacheable";
                return ex.Response;
            }
        }
        catch (Exception ex) {
            outcome = "error";
            RecordException(activity, ex);
            throw;
        }
        finally {
            RecordCacheOutcome(activity, "get", outcome, started);
        }
    }

    /// <inheritdoc />
    public async ValueTask InvalidateAsync(IHandlerCacheInvalidationPolicy policy, CancellationToken ct) {
        using var activity =
            HandlerCacheTelemetry.Source.StartActivity("handler cache invalidate", ActivityKind.Internal);
        var started = Stopwatch.GetTimestamp();
        var outcome = "success";
        SetCacheTags(activity, "invalidate", null, policy.Scope, policy.Tags.Count);

        try {
            // Invalidating by tag keeps command handlers independent from the exact read keys they invalidate.
            var physicalScope = CreatePhysicalScope(policy.Scope);
            ValidateTags(policy.Tags);
            var tags = CreateInvalidationTags(ResolveTenant(), physicalScope, policy.Tags);
            foreach (var tag in tags) await cache.RemoveByTagAsync(tag, ct);
        }
        catch (Exception ex) {
            outcome = "error";
            RecordException(activity, ex);
            throw;
        }
        finally {
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
                if (response is IResultLike { IsSuccess: false })
                    throw new NonCacheableResultException<TResponse>(response);

                return state.Policy.Serialize(response, state.JsonOptions);
            },
            options,
            tags,
            ct);

        return payloadState.Policy.Deserialize(payload, jsonSerialization.Options);
    }

    private static string CreatePhysicalKey(
        string keyPrefix,
        TenantNamespace tenant,
        string physicalScope,
        string requestKey) {
        // Prefixing each part makes the key readable and avoids accidental collisions between scopes and request keys.
        return tenant.Segment is null
            ? $"{keyPrefix}:scope:{physicalScope}:request:{requestKey}"
            : $"{keyPrefix}:{tenant.Segment}:scope:{physicalScope}:request:{requestKey}";
    }

    /// <summary>
    /// Tags stamped onto a cache <b>entry</b>. A <see cref="HandlerCacheScope.CurrentUser"/>-scoped entry carries
    /// its user-namespaced tags <b>and</b> the global-namespaced tags, so a
    /// <see cref="HandlerCacheScope.Global"/>-scoped invalidation — the <c>[CacheInvalidate]</c> default — evicts
    /// every user's entries for those tags (the safe default is over-invalidation). A user-scoped invalidation
    /// still clears only the calling user's namespace.
    /// </summary>
    /// <remarks>
    /// Under tenant scoping an entry carries that same set twice: once cross-tenant (byte-identical to a host
    /// without tenant scoping) and once inside its tenant's namespace. An invalidation inside a tenant clears the
    /// tenant's copy; a system-scope or tenant-less invalidation clears the cross-tenant copy, which reaches every
    /// tenant (see <see cref="CreateInvalidationTags"/>).
    /// </remarks>
    private static string[] CreateEntryTags(
        TenantNamespace tenant,
        HandlerCacheScope scope,
        string physicalScope,
        IReadOnlyList<string> tags) {
        var perUser = scope != HandlerCacheScope.Global;
        var namespaceCount = (perUser ? 2 : 1) * (tenant.Segment is null ? 1 : 2);
        var physicalTags = new string[namespaceCount * tags.Count];
        var offset = 0;

        AppendTags(physicalTags, ref offset, physicalScope, tags);
        if (perUser) AppendTags(physicalTags, ref offset, GlobalScope, tags);
        if (tenant.Segment is null) return physicalTags;

        AppendTags(physicalTags, ref offset, $"{tenant.Segment}:{physicalScope}", tags);
        if (perUser) AppendTags(physicalTags, ref offset, $"{tenant.Segment}:{GlobalScope}", tags);
        return physicalTags;
    }

    /// <summary>
    /// Tags an <b>invalidation</b> removes: exactly the requested scope's namespace. Global clears the global
    /// namespace (which every entry carries, see <see cref="CreateEntryTags"/>); CurrentUser clears only the
    /// calling user's namespace.
    /// </summary>
    /// <remarks>
    /// Inside a tenant, that namespace is the tenant's own plus the system scope's: tenant-guarded writes can only
    /// change the tenant's rows, so other tenants keep their entries, while a cross-tenant read cached under
    /// <see cref="ITenantContext.SystemScope"/> spans the rows that changed. A system scope, or a scope with no tenant
    /// resolved, clears the cross-tenant namespace every entry carries: its writes are not tenant-guarded (or, with
    /// no tenant, can only reach data that is not tenant-scoped), so any tenant's entries may be stale.
    /// </remarks>
    private static string[] CreateInvalidationTags(
        TenantNamespace tenant,
        string physicalScope,
        IReadOnlyList<string> tags) {
        var offset = 0;
        if (tenant.Kind != TenantKind.Tenant) {
            var crossTenant = new string[tags.Count];
            AppendTags(crossTenant, ref offset, physicalScope, tags);
            return crossTenant;
        }

        var physicalTags = new string[tags.Count * 2];
        AppendTags(physicalTags, ref offset, $"{tenant.Segment}:{physicalScope}", tags);
        AppendTags(physicalTags, ref offset, $"{SystemTenantSegment}:{physicalScope}", tags);
        return physicalTags;
    }

    private static void AppendTags(
        string[] physicalTags,
        ref int offset,
        string tagNamespace,
        IReadOnlyList<string> tags) {
        foreach (var tag in tags) physicalTags[offset++] = $"handler-cache:{tagNamespace}:tag:{tag}";
    }

    private static void ValidateTags(IReadOnlyList<string> tags) {
        if (tags.Count == 0)
            throw new InvalidOperationException("Handler cache policies must define at least one tag.");

        foreach (var tag in tags)
            if (string.IsNullOrWhiteSpace(tag) || tag == "*")
                throw new InvalidOperationException($"Invalid handler cache tag '{tag}'.");
    }

    private TenantNamespace ResolveTenant() {
        // Tenant scoping is opt-in: without an ITenantContext, keys and tags keep their pre-tenancy shape.
        var tenantContext = services.GetService<ITenantContext>();
        if (tenantContext is null) return new TenantNamespace(TenantKind.NotScoped, null);

        // Checked first, as the tenant read filter does: a system scope spans every tenant whatever is resolved.
        if (tenantContext.IsSystemScope) return new TenantNamespace(TenantKind.System, SystemTenantSegment);

        var tenantId = tenantContext.TenantId;
        if (string.IsNullOrWhiteSpace(tenantId)) return new TenantNamespace(TenantKind.Unresolved, null);

        // Hashed like user ids, so infrastructure logs do not expose raw identifiers. The fixed-length hex segment
        // also can never read as the system segment, whatever a tenant id looks like.
        return new TenantNamespace(TenantKind.Tenant, $"tenant:{Hash(tenantId)}");
    }

    private string CreatePhysicalScope(HandlerCacheScope scope) {
        return scope switch {
            HandlerCacheScope.Global => GlobalScope,
            // User ids are hashed before entering cache keys so infrastructure logs do not expose raw identifiers.
            HandlerCacheScope.CurrentUser => $"user:{Hash(GetCurrentUserId())}",
            _ => throw new InvalidOperationException($"Unsupported handler cache scope '{scope}'.")
        };
    }

    private string GetCurrentUserId() {
        var currentUser = services.GetRequiredService<ICurrentUser>();
        if (!currentUser.IsAuthenticated || string.IsNullOrWhiteSpace(currentUser.UserId))
            throw new InvalidOperationException(
                "Current-user scoped handler caching requires an authenticated current user with a user id.");

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
        if (activity?.IsAllDataRequested != true) return;

        activity.SetTag("handler.cache.operation", operation);
        if (!string.IsNullOrWhiteSpace(keyPrefix)) activity.SetTag("handler.cache.policy_prefix", keyPrefix);

        activity.SetTag("handler.cache.scope", scope.ToString());
        activity.SetTag("handler.cache.tag_count", tagCount);
    }

    private static void RecordCacheOutcome(Activity? activity, string operation, string outcome, long started) {
        if (activity?.IsAllDataRequested == true) {
            activity.SetTag("handler.cache.outcome", outcome);
            activity.SetTag("handler.cache.factory_executed",
                outcome is "miss-factory-executed" or "miss-non-cacheable" or "bypass-no-tenant");

            if (outcome == "error") activity.SetStatus(ActivityStatusCode.Error);
        }

        HandlerCacheTelemetry.RecordOperation(
            operation,
            outcome,
            Stopwatch.GetElapsedTime(started));
    }

    private static void RecordException(Activity? activity, Exception exception) {
        activity?.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection {
            { "exception.type", exception.GetType().FullName },
            { "exception.message", exception.Message }
        }));
        activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
    }

    private enum TenantKind {
        NotScoped,
        Unresolved,
        System,
        Tenant
    }

    /// <summary>
    /// The tenant dimension of a key or tag. <see cref="Segment"/> is the namespace text for a tenant or the system
    /// scope, and <see langword="null"/> when there is none to add.
    /// </summary>
    private readonly record struct TenantNamespace(TenantKind Kind, string? Segment);

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
