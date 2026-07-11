namespace Elarion.Abstractions.ClientEvents;

/// <summary>
/// Declares that the resource segment of this <see cref="IClientEvent"/> contract's topic is a
/// <b>routing key, not an entitlement</b>: any caller who passes the topic's subscribe-time requirements
/// (authenticated, plus any <c>[RequirePermission]</c>/<c>[RequireRole]</c> on the contract) may subscribe
/// to any resource of this topic, without consulting the <see cref="IClientEventSubscriptionAuthorizer"/>
/// seam. Use it when the resource merely selects which events to receive (a stock symbol, a public room id)
/// rather than gating who may see them.
/// </summary>
/// <remarks>
/// Without this attribute a resource-scoped subscription stays <b>fail-closed</b>: it is denied unless a
/// registered <see cref="IClientEventSubscriptionAuthorizer"/> approves it. Declaring openness per topic —
/// next to the contract's other subscribe-time requirements — keeps a future entitlement-scoped topic from
/// being silently opened by a global "allow everything" authorizer. The imperative equivalent is
/// <c>AllowAnyResource()</c> on the topic's options in <c>AddElarionClientEvents</c>.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class AllowAnyResourceAttribute : Attribute;
