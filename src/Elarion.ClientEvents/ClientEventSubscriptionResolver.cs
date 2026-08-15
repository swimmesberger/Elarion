using Elarion.Abstractions.Authorization;
using Elarion.Abstractions.ClientEvents;
using Elarion.Abstractions.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Elarion.ClientEvents;

/// <summary>
/// The transport-neutral, fail-closed subscribe pipeline: catalog lookup, topic-requirement authorization,
/// resource-scope authorization, and scope expansion — the one place subscribe-time policy lives, shared by
/// the SSE endpoint and every connection adapter so a second transport can never fork the authorization
/// rules. Scoped: resolves the caller from the current scope's <see cref="ICurrentUser"/>, so a
/// non-request transport must resolve it from a dispatch scope seeded with the connection's principal.
/// </summary>
/// <remarks>
/// Failure semantics (mirrored onto HTTP by the SSE endpoint): no authenticated caller →
/// <see cref="ClientEventSubscriptionStatus.Unauthenticated"/>; an empty set or a blank topic →
/// <see cref="ClientEventSubscriptionStatus.InvalidRequest"/>; an unknown topic, a failed topic requirement,
/// or a resource scope without a passing <see cref="IClientEventSubscriptionAuthorizer"/> →
/// <see cref="ClientEventSubscriptionStatus.NotFound"/>, deliberately indistinguishable. Any topic that
/// declares a requirement — including one that only requires authentication — is decided by the registered
/// <see cref="IAuthorizer"/>, so cross-cutting <see cref="IGlobalAuthorizationRule"/> denials apply to
/// subscriptions exactly as they do to request/reply. A topic-only
/// request expands to the topic's global scope plus the caller's own user scope; user scope is always the
/// caller's own. A topic declaring <c>AllowAnyResource</c> has said "the resource is a routing key, not an
/// entitlement" — its requirements still apply, but the per-resource authorizer seam is skipped.
/// </remarks>
public sealed class ClientEventSubscriptionResolver(ClientEventTopicCatalog catalog, IServiceProvider services) {
    /// <summary>Resolves <paramref name="requests"/> into authorized subscriptions, all-or-nothing.</summary>
    /// <param name="requests">The requested subscriptions, as parsed by the transport.</param>
    /// <param name="ct">A cancellation token observed during authorization.</param>
    public async ValueTask<ClientEventSubscriptionResolution> ResolveAsync(
        IReadOnlyList<ClientEventSubscriptionRequest> requests, CancellationToken ct = default) {
        ArgumentNullException.ThrowIfNull(requests);

        var currentUser = services.GetService<ICurrentUser>();
        if (currentUser is null || !currentUser.IsAuthenticated)
            return ClientEventSubscriptionResolution.Unauthenticated;

        if (requests.Count == 0) return ClientEventSubscriptionResolution.InvalidRequest;

        var resolved = new List<ClientEventSubscription>(requests.Count + 1);
        var authorizedTopics = new HashSet<string>(StringComparer.Ordinal);

        foreach (var request in requests) {
            if (string.IsNullOrEmpty(request.Topic)) return ClientEventSubscriptionResolution.InvalidRequest;

            // Unknown, disabled, and denied topics are indistinguishable from the outside: not found.
            var topic = catalog.FindByName(request.Topic);
            if (topic is null) return ClientEventSubscriptionResolution.NotFound;

            if (authorizedTopics.Add(topic.Name) && !await AuthorizeTopicAsync(topic.Requirements, ct))
                return ClientEventSubscriptionResolution.NotFound;

            if (request.Resource is null) {
                resolved.Add(new ClientEventSubscription { Topic = topic.Name, Scope = ClientEventScope.Global });
                if (currentUser.UserId is { Length: > 0 } userId)
                    resolved.Add(new ClientEventSubscription
                        { Topic = topic.Name, Scope = ClientEventScope.User(userId) });
                continue;
            }

            var subscription = new ClientEventSubscription {
                Topic = topic.Name,
                Scope = ClientEventScope.Resource(request.Resource)
            };
            // Resource scopes are fail-closed: no registered authorizer denies, and denial reads as not
            // found. A topic declaring AllowAnyResource has said "the resource is a routing key, not an
            // entitlement" — its requirements already passed above, so the seam is skipped.
            if (!topic.AllowAnyResource) {
                var authorizer = services.GetService<IClientEventSubscriptionAuthorizer>();
                if (authorizer is null || !await authorizer.AuthorizeAsync(subscription, ct))
                    return ClientEventSubscriptionResolution.NotFound;
            }

            resolved.Add(subscription);
        }

        return ClientEventSubscriptionResolution.Resolved(resolved);
    }

    private async ValueTask<bool> AuthorizeTopicAsync(AuthorizationRequirements requirements, CancellationToken ct) {
        // A topic with no requirements at all needs no decision.
        if (!requirements.HasAny) return true;

        var authorizer = services.GetService<IAuthorizer>();
        if (authorizer is not null) {
            // Every requirement-bearing topic is decided by the authorizer, including one that requires nothing
            // beyond authentication. The authorizer is more than a requirement evaluator: it also runs the
            // registered IGlobalAuthorizationRule set (a suspended tenant, a maintenance lockdown). Deciding
            // "authenticated" locally would let a subscription outlive a gate every request/reply handler honors.
            return await authorizer.AuthorizeAsync(requirements, null, ct) is null;
        }

        // No authorizer registered. Anything beyond "authenticated" has no evaluator and fails closed;
        // "authenticated" itself was already established for the whole resolution, so a host that never opted
        // into the authorization package keeps its subscriptions. That is not a hole: a global rule can only
        // take effect *through* an IAuthorizer, so with none registered there is no rule set being bypassed.
        return !(requirements.Permissions.Count > 0 || requirements.Roles.Count > 0
                 || requirements.Claims.Count > 0 || requirements.Policies.Count > 0
                 || requirements.Resources.Count > 0);
    }
}
