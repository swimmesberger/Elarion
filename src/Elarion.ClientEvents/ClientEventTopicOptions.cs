using System.Diagnostics.CodeAnalysis;
using Elarion.Abstractions.Authorization;
using Elarion.Abstractions.ClientEvents;

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

    /// <summary>
    /// Declares the topic's <see cref="IClientEventSubscriptionObserver"/>: called with a per-subscriber sink
    /// when a client subscribes (producer-controlled initial value) and on debounced first/last interest
    /// transitions (lazy compute). Resolved from a fresh DI scope per callback. Declarative form:
    /// <c>[SubscriptionObserver&lt;TObserver&gt;]</c> on the contract.
    /// </summary>
    /// <typeparam name="TObserver">The observer implementation.</typeparam>
    public ClientEventTopicOptions ObserveSubscriptions<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
        TObserver>()
        where TObserver : class, IClientEventSubscriptionObserver {
        ObserverType = typeof(TObserver);
        return this;
    }

    /// <summary>
    /// How long the last subscriber's departure lingers before the observer sees interest go inactive — the
    /// reload-debounce (default 5 seconds). A resubscribe within the linger keeps interest active.
    /// </summary>
    /// <param name="linger">The linger duration; must not be negative.</param>
    public ClientEventTopicOptions WithInterestLinger(TimeSpan linger) {
        ArgumentOutOfRangeException.ThrowIfLessThan(linger, TimeSpan.Zero);
        InterestLinger = linger;
        return this;
    }

    internal bool AllowsAnyResource { get; private set; }

    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
    internal Type? ObserverType { get; private set; }

    internal TimeSpan? InterestLinger { get; private set; }

    internal AuthorizationRequirements BuildRequirements() {
        return new AuthorizationRequirements(false,
            true,
            [.. _permissions],
            [.. _roles],
            [],
            [],
            []);
    }
}
