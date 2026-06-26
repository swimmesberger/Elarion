namespace Elarion.Abstractions.Authorization;

/// <summary>
/// Requires the current principal to carry a claim of <see cref="ClaimType"/>. When
/// <see cref="AllowedValues"/> is non-empty the principal must have at least one matching value (OR);
/// when empty, the presence of any claim of that type satisfies the requirement.
/// </summary>
/// <remarks>
/// The general claim requirement. <see cref="RequirePermissionAttribute"/> is sugar over this for the
/// configured permission claim type. Multiple authorization attributes on a handler are combined with AND;
/// OR lives inside a single attribute's <see cref="AllowedValues"/> list.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = true, AllowMultiple = true)]
public sealed class RequireClaimAttribute(string claimType, params string[] allowedValues) : Attribute {
    /// <summary>The claim type the principal must carry.</summary>
    public string ClaimType { get; } = claimType;

    /// <summary>The accepted claim values (OR). Empty means presence-only.</summary>
    public IReadOnlyList<string> AllowedValues { get; } = allowedValues;
}
