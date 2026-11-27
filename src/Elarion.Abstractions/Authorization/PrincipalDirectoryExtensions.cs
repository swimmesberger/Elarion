namespace Elarion.Abstractions.Authorization;

/// <summary>
/// The questions an application asks its <see cref="IPrincipalDirectory{TKey}"/> directly: who holds a permission, who
/// is in a role — mention candidates, assignees, the readers of a notification. They read the same effective roles and
/// claims the resource rules invert, so the answer agrees with the principal <c>ICurrentUser</c> sees.
/// </summary>
/// <remarks>
/// Each returns a composable query of principal ids, so it joins into the caller's statement: filter the candidates the
/// application cares about (active accounts, staff) with it rather than materializing it.
/// </remarks>
/// <example>
/// <code>
/// var assignees = await db.Users
///     .Where(u =&gt; u.IsActive &amp;&amp; directory.HoldersOf("requests.handle").Contains(u.Id))
///     .ToListAsync(ct);
/// </code>
/// </example>
public static class PrincipalDirectoryExtensions {
    /// <summary>The principals holding <paramref name="permission"/>, directly or through a role, each once.</summary>
    /// <param name="directory">The principal directory.</param>
    /// <param name="permission">The permission, as <c>[RequirePermission]</c> spells it (<c>"{resource}.{verb}"</c>).</param>
    /// <param name="claimType">
    /// The claim type permissions are stored under. Defaults to
    /// <see cref="AuthorizationOptions.DefaultPermissionClaimType"/>; pass the host's
    /// <see cref="AuthorizationOptions.PermissionClaimType"/> when it changes it.
    /// </param>
    /// <typeparam name="TKey">The principal id type.</typeparam>
    public static IQueryable<TKey> HoldersOf<TKey>(
        this IPrincipalDirectory<TKey> directory,
        string permission,
        string claimType = AuthorizationOptions.DefaultPermissionClaimType)
        where TKey : notnull {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentException.ThrowIfNullOrEmpty(permission);
        ArgumentException.ThrowIfNullOrEmpty(claimType);
        return directory.Claims
            .Where(claim => claim.Type == claimType && claim.Value == permission)
            .Select(claim => claim.PrincipalId)
            .Distinct();
    }

    /// <summary>The principals in <paramref name="role"/>, each once. Role names compare ordinally, as <c>IsInRole</c> does.</summary>
    /// <param name="directory">The principal directory.</param>
    /// <param name="role">The role name.</param>
    /// <typeparam name="TKey">The principal id type.</typeparam>
    public static IQueryable<TKey> MembersOf<TKey>(this IPrincipalDirectory<TKey> directory, string role)
        where TKey : notnull {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentException.ThrowIfNullOrEmpty(role);
        return directory.Roles
            .Where(membership => membership.Role == role)
            .Select(membership => membership.PrincipalId)
            .Distinct();
    }
}
