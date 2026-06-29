namespace Elarion.Abstractions.Authorization;

/// <summary>
/// The runtime view of the compile-time-discovered catalog of authorization requirement strings declared
/// across the application's handlers — every <c>[RequirePermission("…")]</c> and <c>[RequireRole("…")]</c> —
/// grouped by owning <c>[AppModule]</c> and by <see cref="PermissionKind"/>. It lets startup code enumerate the
/// full set instead of hand-maintaining a central list, so adding a guarded handler contributes its permission
/// to seeding and policy automatically, with no central edit.
/// </summary>
/// <remarks>
/// Populated by the Elarion source generators: each module registers one <see cref="PermissionCatalogModule"/>
/// (gated by module enablement) through its generated <c>ConfigureDefaultServices</c>, and this catalog
/// aggregates every registered instance — so it spans referenced module assemblies, not just the host. Resolve
/// it from DI. For compile-time use (e.g. static role-policy declarations) reference the generated
/// <c>ElarionPermissions</c> static instead.
/// </remarks>
/// <example>
/// <code>
/// // Seed permission claims onto roles without a hand-kept Permissions.All list:
/// foreach (var permission in catalog.Permissions)
///     await EnsureRoleClaimAsync(adminRole, "permission", permission);
/// </code>
/// </example>
public interface IPermissionCatalog {
    /// <summary>All distinct permission strings across enabled modules, ordinally sorted.</summary>
    IReadOnlyList<string> Permissions { get; }

    /// <summary>All distinct role names across enabled modules, ordinally sorted.</summary>
    IReadOnlyList<string> Roles { get; }

    /// <summary>Distinct permission strings grouped by their declared <see cref="PermissionKind"/>.</summary>
    IReadOnlyDictionary<PermissionKind, IReadOnlyList<string>> ByKind { get; }

    /// <summary>The per-module breakdown, ordered by module name.</summary>
    IReadOnlyList<PermissionCatalogModule> Modules { get; }
}

/// <summary>
/// A single module's contribution to the <see cref="IPermissionCatalog"/>: the permissions (with their
/// <see cref="PermissionKind"/>) and role names its handlers declare. The Elarion generator registers one
/// instance per module (gated by module enablement); <see cref="IPermissionCatalog"/> aggregates every
/// registered instance.
/// </summary>
public sealed record PermissionCatalogModule {
    /// <summary>The owning module name (the <c>[AppModule]</c> name).</summary>
    public required string Module { get; init; }

    /// <summary>The distinct permissions declared by this module's handlers, ordered by name.</summary>
    public required IReadOnlyList<PermissionCatalogEntry> Permissions { get; init; }

    /// <summary>The distinct role names declared by this module's handlers, ordinally sorted.</summary>
    public required IReadOnlyList<string> Roles { get; init; }
}

/// <summary>A permission string together with its declared <see cref="PermissionKind"/>.</summary>
public sealed record PermissionCatalogEntry {
    /// <summary>The permission string (e.g. <c>"properties.read"</c>).</summary>
    public required string Name { get; init; }

    /// <summary>The declared classification (<see cref="PermissionKind.Unspecified"/> when none was given).</summary>
    public required PermissionKind Kind { get; init; }
}
