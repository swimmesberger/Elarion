namespace Elarion.AppPasswords;

/// <summary>
/// The claims the app-password authentication scheme adds to the principal it signs in, so a DAV dispatcher or a
/// handler can tell an app-password request apart and check a feed token's calendar.
/// </summary>
public static class AppPasswordClaimTypes {
    /// <summary>The id of the app password the request authenticated with: <c>elarion:app_password_id</c>.</summary>
    public const string Id = "elarion:app_password_id";

    /// <summary>The <see cref="AppPasswordScope.Value"/> of that app password: <c>elarion:app_password_scope</c>.</summary>
    public const string Scope = "elarion:app_password_scope";
}
