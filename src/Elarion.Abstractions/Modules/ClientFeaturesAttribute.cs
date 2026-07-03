namespace Elarion.Abstractions.Modules;

/// <summary>
/// Declares the feature flag and variant names a module exposes to the <b>client</b> (the frontend), placed on the
/// module's <c>[AppModule]</c> type alongside its other declarations.
/// </summary>
/// <remarks>
/// <para>
/// The names listed here are the <em>only</em> flags/variants the client-capability bootstrap evaluates and returns
/// for the current user — exposure is <b>opt-in by enumeration</b>, so an internal flag never reaches the wire unless
/// a module names it here, and a disabled module exposes nothing. See <c>ADR-0030</c> and the
/// <c>client-capabilities</c> concept doc.
/// </para>
/// <para>
/// A listed name needs <b>no</b> server-side <c>[FeatureGate]</c>/<c>[FeatureVariant]</c> behind it — a pure UI flag
/// is first-class, evaluated by the same provider and the user's context. Exposure is therefore decoupled from gate
/// discovery: the client-feature manifest is exactly the union of the <c>[ClientFeatures]</c> lists, not a scan of the
/// feature gates.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// [AppModule("Billing")]
/// [ClientFeatures("new-checkout", "dashboard-v2")]   // exposed to the frontend
/// public static class BillingModule { }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class ClientFeaturesAttribute(params string[] features) : Attribute {
    /// <summary>The feature flag / variant names this module exposes to the client.</summary>
    public IReadOnlyList<string> Features { get; } = features;
}
