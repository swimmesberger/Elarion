namespace Elarion.Abstractions.Authorization;

/// <summary>
/// Requires the current principal to hold permission to perform <paramref name="verb"/> on
/// <paramref name="resource"/> — the Kubernetes-RBAC <c>(resource, verb)</c> shape. This is sugar for
/// <c>[RequireClaim(permissionClaimType, "{resource}.{verb}")]</c> over the configured permission claim type
/// (see <see cref="AuthorizationOptions.PermissionClaimType"/>, default <c>"permission"</c>).
/// </summary>
/// <remarks>
/// The enforced permission string is composed as <c>{resource}{<see cref="Separator"/>}{verb}</c> (e.g.
/// <c>properties.read</c>), so the principal's <c>permission</c> claims use that same format. The two axes drive
/// the generated <c>ElarionPermissions.ByResource</c>/<c>ByVerb</c> groupings, so role policy can grant "every
/// read" or "every verb on properties" without a string-suffix convention. The verb vocabulary is open — use the
/// <see cref="Verbs"/> constants for the common set, or any string (Kubernetes allows custom verbs too).
/// </remarks>
/// <example>
/// <code>
/// [RequirePermission("properties", Verbs.Read)]   // requires the "properties.read" permission
/// public sealed class ListProperties : IHandler&lt;ListProperties.Query, Result&lt;…&gt;&gt; { … }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, Inherited = true, AllowMultiple = true)]
public sealed class RequirePermissionAttribute(string resource, string verb) : Attribute {
    /// <summary>The separator between resource and verb in the composed permission string.</summary>
    public const string Separator = ".";

    /// <summary>The resource the permission applies to (e.g. <c>"properties"</c>).</summary>
    public string Resource { get; } = resource;

    /// <summary>The verb/action the permission grants (e.g. <c>"read"</c>); see <see cref="Verbs"/>.</summary>
    public string Verb { get; } = verb;

    /// <summary>The composed permission string checked against the principal's claims (<c>{Resource}.{Verb}</c>).</summary>
    public string Permission { get; } = resource + Separator + verb;
}
