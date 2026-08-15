namespace Elarion.Abstractions.Authorization;

/// <summary>
/// A cross-cutting authorization rule evaluated for <b>every</b> authorized handler invocation, independently of
/// the requirements a handler declares. It is the seam for deployment-wide conditions that are not expressible as
/// a per-handler <c>[Require*]</c>: a suspended tenant, an expired subscription, a maintenance lockdown, a
/// device/session posture check.
/// </summary>
/// <remarks>
/// <para>
/// Registered rules run <b>in registration order</b>, after the authenticated gate and <b>before</b> the declared
/// permission, role, claim, policy, and resource checks. The first rule returning a non-null
/// <see cref="AppError"/> denies, and that error is returned to the caller <b>unchanged</b> — a rule that must not
/// disclose the existence of a resource returns <see cref="AppError.NotFound(string)"/>, one that states the
/// denial plainly returns <see cref="AppError.Forbidden(string)"/>. Returning <see langword="null"/> passes.
/// </para>
/// <para>
/// A handler marked <c>[AllowAnonymous]</c> short-circuits the whole authorizer, so rules are <b>not</b> evaluated
/// for it — that attribute means "this operation is deliberately outside authorization", and a global rule must
/// not resurrect a gate the handler opted out of. Put a check that must also cover anonymous traffic in the
/// handler or a host middleware instead.
/// </para>
/// <para>
/// <b>A rule only runs where the authorization decorator is attached.</b> The decorator is attached at compile
/// time by the handler-registration generator, and only for handlers that carry a <c>[Require*]</c> attribute or
/// that are in scope of <c>[ElarionAuthorizationDefaults]</c>. A host that wants broad coverage therefore pairs
/// the rule with an assembly-level default:
/// </para>
/// <code>
/// [assembly: ElarionAuthorizationDefaults]
/// </code>
/// <para>
/// That covers every handler <b>except event-consumer handlers</b> (a handler whose request implements
/// <c>IDomainEvent</c>/<c>IIntegrationEvent</c>): the generator deliberately skips <i>implicit</i>
/// default-driven attachment for those, because consumers are dispatched on a delivery scope with no
/// authenticated user. A consumer that carries an <b>explicit</b> <c>[Require*]</c> does get the decorator, and
/// then rules run for it too — so a rule reached from a consumer must tolerate an unauthenticated principal.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// public sealed class ActiveSubscriptionRule(ITenantContext tenants) : IGlobalAuthorizationRule {
///     public async ValueTask&lt;AppError?&gt; EvaluateAsync(AuthorizationContext context, CancellationToken ct) {
///         var tenant = await tenants.GetCurrentAsync(ct);
///         return tenant.IsSuspended ? AppError.Forbidden("This workspace is suspended.") : null;
///     }
/// }
///
/// // Program.cs
/// builder.Services.AddElarionGlobalAuthorizationRule&lt;ActiveSubscriptionRule&gt;();
/// </code>
/// </example>
public interface IGlobalAuthorizationRule {
    /// <summary>
    /// Evaluates the rule for the current principal and handler request. Returns <see langword="null"/> to pass,
    /// or the <see cref="AppError"/> the caller should receive — returned as-is, so the rule chooses the outcome
    /// kind (forbidden, not-found, conflict, …) and the message.
    /// </summary>
    ValueTask<AppError?> EvaluateAsync(AuthorizationContext context, CancellationToken ct);
}
