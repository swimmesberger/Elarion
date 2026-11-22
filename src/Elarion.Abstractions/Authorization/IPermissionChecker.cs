namespace Elarion.Abstractions.Authorization;

/// <summary>
/// Answers, synchronously, whether the current caller holds a permission: the one decision the
/// <see cref="RequirePermissionAttribute"/> gate, <see cref="VisibleWithAttribute"/> members and handler code share.
/// </summary>
/// <remarks>
/// <para>
/// A permission is the composed <c>{resource}.{verb}</c> string (<see cref="RequirePermissionAttribute.Permission"/>);
/// pass the generated <c>ElarionPermissions.{Resource}.{Verb}</c> constants rather than literals, so a renamed
/// permission fails the build instead of silently denying. The default implementation (<c>ClaimsPermissionChecker</c>,
/// registered by <c>AddElarionAuthorization()</c>) answers from the principal's claims of
/// <see cref="AuthorizationOptions.PermissionClaimType"/>; an unauthenticated caller holds nothing.
/// </para>
/// <para>
/// The default authorizer evaluates every <c>[RequirePermission]</c> through this service, so replacing it changes the
/// gate and every in-handler check together: a host whose administrators hold every permission registers a checker
/// that says so, once, instead of adding <c>IsInRole("admin")</c> next to each check. Register the replacement
/// <b>before</b> <c>AddElarionAuthorization()</c> (which uses <c>TryAdd</c>), as a scoped service.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// [Handler]
/// [RequirePermission("banking", Verbs.Write)]
/// public sealed class PreviewImport(IPermissionChecker permissions, AppDbContext db) : IHandler&lt;…&gt; {
///     public async ValueTask&lt;Result&lt;ImportPreview&gt;&gt; HandleAsync(…) {
///         var showBookings = permissions.Holds(ElarionPermissions.Banking.Read);
///         …
///     }
/// }
///
/// // Administrators hold everything — for the gate and for every check like the one above.
/// public sealed class AdminHoldsEverything(ClaimsPermissionChecker claims, ICurrentUser user) : IPermissionChecker {
///     public bool Holds(string permission) =&gt; user.IsInRole("admin") || claims.Holds(permission);
/// }
/// builder.Services.AddScoped&lt;IPermissionChecker, AdminHoldsEverything&gt;();
/// builder.Services.AddElarionAuthorization();
/// </code>
/// </example>
public interface IPermissionChecker {
    /// <summary>Returns whether the current caller holds <paramref name="permission"/> (<c>{resource}.{verb}</c>).</summary>
    /// <param name="permission">The composed permission, for example <c>ElarionPermissions.Banking.Read</c>.</param>
    bool Holds(string permission);
}

/// <summary>Conveniences over <see cref="IPermissionChecker"/>.</summary>
public static class PermissionCheckerExtensions {
    /// <summary>
    /// Returns whether the current caller holds the permission to perform <paramref name="verb"/> on
    /// <paramref name="resource"/>, composed exactly as <see cref="RequirePermissionAttribute"/> composes it.
    /// </summary>
    public static bool Holds(this IPermissionChecker checker, string resource, string verb) {
        ArgumentNullException.ThrowIfNull(checker);
        return checker.Holds(resource + RequirePermissionAttribute.Separator + verb);
    }

    /// <summary>Returns whether the current caller holds at least one of <paramref name="permissions"/>.</summary>
    public static bool HoldsAny(this IPermissionChecker checker, params ReadOnlySpan<string> permissions) {
        ArgumentNullException.ThrowIfNull(checker);
        foreach (var permission in permissions)
            if (checker.Holds(permission))
                return true;

        return false;
    }

    /// <summary>Returns whether the current caller holds every one of <paramref name="permissions"/>.</summary>
    public static bool HoldsAll(this IPermissionChecker checker, params ReadOnlySpan<string> permissions) {
        ArgumentNullException.ThrowIfNull(checker);
        foreach (var permission in permissions)
            if (!checker.Holds(permission))
                return false;

        return true;
    }
}
