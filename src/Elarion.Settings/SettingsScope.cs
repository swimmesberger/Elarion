namespace Elarion.Settings;

/// <summary>
/// Identifies the scope a setting belongs to. A scope is an open <c>(Kind, Owner)</c> value rather than a
/// closed enum, so additional scopes (for example <c>tenant</c> or <c>environment</c>) can be introduced
/// without changing this contract or the store schema. The two scopes shipped today are
/// <see cref="Global"/> (system-wide) and the per-user scope (<see cref="User"/> / <see cref="CurrentUser"/>).
/// </summary>
/// <param name="Kind">The scope discriminator, for example <c>"global"</c> or <c>"user"</c>.</param>
/// <param name="Owner">
/// The owner the scope is bound to (for example a user id), or <see langword="null"/> for a non-owned scope
/// such as <see cref="Global"/>. A <c>user</c>-kind scope with a <see langword="null"/> owner is the
/// <see cref="CurrentUser"/> placeholder, resolved against the ambient <c>ICurrentUser</c> by the accessor.
/// </param>
public readonly record struct SettingsScope(string Kind, string? Owner) {
    /// <summary>The discriminator for the system-wide scope.</summary>
    public const string GlobalKind = "global";

    /// <summary>The discriminator for the per-user scope.</summary>
    public const string UserKind = "user";

    /// <summary>The system-wide scope, shared by all users.</summary>
    public static SettingsScope Global { get; } = new(GlobalKind, Owner: null);

    /// <summary>
    /// A placeholder for "the ambient current user". The accessor resolves the owner from <c>ICurrentUser</c>
    /// at call time; it never reaches a store. Use <see cref="User"/> to target a specific user explicitly.
    /// </summary>
    public static SettingsScope CurrentUser { get; } = new(UserKind, Owner: null);

    /// <summary>Creates a scope bound to a specific user.</summary>
    /// <param name="userId">The user id; must not be <see langword="null"/>.</param>
    public static SettingsScope User(string userId) =>
        new(UserKind, userId ?? throw new ArgumentNullException(nameof(userId)));

    /// <summary>
    /// Whether this is the <see cref="CurrentUser"/> placeholder — a <c>user</c>-kind scope with no resolved
    /// owner. Such a scope must be resolved to a concrete <see cref="User"/> before it reaches a store.
    /// </summary>
    public bool IsCurrentUserPlaceholder => Kind == UserKind && Owner is null;
}
