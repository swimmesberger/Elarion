using Elarion.Abstractions.Authorization;

namespace Elarion.ClientEvents;

/// <summary>
/// Subscribe-time requirements for one topic. Every topic requires an authenticated user; permissions and
/// roles AND on top, mirroring the handler-side <c>[RequirePermission]</c>/<c>[RequireRole]</c> semantics.
/// </summary>
public sealed class ClientEventTopicOptions {
    private readonly List<string> _permissions = [];
    private readonly List<string> _roles = [];

    /// <summary>Requires the <c>{resource}.{verb}</c> permission to subscribe (the handler-side
    /// <c>[RequirePermission(resource, verb)]</c> shape).</summary>
    public ClientEventTopicOptions RequirePermission(string resource, string verb) {
        ArgumentException.ThrowIfNullOrEmpty(resource);
        ArgumentException.ThrowIfNullOrEmpty(verb);
        _permissions.Add(resource + RequirePermissionAttribute.Separator + verb);
        return this;
    }

    /// <summary>Requires the role to subscribe.</summary>
    public ClientEventTopicOptions RequireRole(string role) {
        ArgumentException.ThrowIfNullOrEmpty(role);
        _roles.Add(role);
        return this;
    }

    /// <summary>
    /// Declares the resource segment of this topic a routing key, not an entitlement: any caller passing the
    /// topic's requirements may subscribe to any resource, without consulting the
    /// <c>IClientEventSubscriptionAuthorizer</c> seam. Without it, resource-scoped subscriptions stay
    /// fail-closed. Declarative form: <c>[AllowAnyResource]</c> on the contract.
    /// </summary>
    public ClientEventTopicOptions AllowAnyResource() {
        AllowsAnyResource = true;
        return this;
    }

    internal bool AllowsAnyResource { get; private set; }

    internal AuthorizationRequirements BuildRequirements() =>
        new(AllowAnonymous: false,
            RequireAuthenticated: true,
            Permissions: [.. _permissions],
            Roles: [.. _roles],
            Claims: [],
            Policies: [],
            Resources: []);
}
