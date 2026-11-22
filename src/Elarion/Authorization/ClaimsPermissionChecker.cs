using Elarion.Abstractions.Authorization;
using Elarion.Abstractions.Identity;

namespace Elarion.Authorization;

/// <summary>
/// The default <see cref="IPermissionChecker"/>: a caller holds a permission when it is authenticated and carries a
/// claim of <see cref="AuthorizationOptions.PermissionClaimType"/> with that exact value — the rule the
/// <c>[RequirePermission]</c> gate of <see cref="ClaimsAuthorizer"/> applies.
/// </summary>
/// <remarks>
/// Registered as its own scoped service by <c>AddElarionAuthorization()</c>, so a replacement checker can take it as
/// its inner check (see the example on <see cref="IPermissionChecker"/>).
/// </remarks>
/// <param name="user">The current principal.</param>
/// <param name="options">The configured permission claim type.</param>
public sealed class ClaimsPermissionChecker(ICurrentUser user, AuthorizationOptions options) : IPermissionChecker {
    /// <inheritdoc />
    public bool Holds(string permission) {
        ArgumentNullException.ThrowIfNull(permission);
        return user.IsAuthenticated && user.HasClaim(options.PermissionClaimType, permission);
    }
}
