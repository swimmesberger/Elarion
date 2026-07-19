namespace Elarion.Abstractions.Authorization;

/// <summary>
/// A subject a resource can be shared with: a specific user, or a role (so every user in that role gains
/// access). Modeled as an <b>open value</b> — a <see cref="Kind"/> discriminator plus an <see cref="Id"/> —
/// so further principal kinds (a group, a tenant) can be added without a contract change, mirroring the open
/// <see cref="ResourceOperation"/>.
/// </summary>
/// <param name="Kind">The principal kind, e.g. <see cref="UserKind"/> or <see cref="RoleKind"/>.</param>
/// <param name="Id">The principal identifier: a user id for a user, or a role name for a role.</param>
public readonly record struct ResourcePrincipal(string Kind, string Id) {
    /// <summary>The <see cref="Kind"/> value for a user principal (<c>"user"</c>).</summary>
    public const string UserKind = "user";

    /// <summary>The <see cref="Kind"/> value for a role principal (<c>"role"</c>).</summary>
    public const string RoleKind = "role";

    /// <summary>Creates a user principal for the given user id.</summary>
    public static ResourcePrincipal User(string userId) {
        return new ResourcePrincipal(UserKind, userId);
    }

    /// <summary>Creates a role principal for the given role name.</summary>
    public static ResourcePrincipal Role(string roleName) {
        return new ResourcePrincipal(RoleKind, roleName);
    }
}
