namespace Elarion.Abstractions.Authorization;

/// <summary>
/// The runtime view of the compile-time-discovered catalog of authorization requirements declared across the
/// application's handlers — every <c>[RequirePermission(resource, verb)]</c> and <c>[RequireRole("…")]</c> —
/// grouped by owning <c>[AppModule]</c>, by <c>resource</c>, and by <c>verb</c> (the Kubernetes-RBAC axes). It lets
/// startup code enumerate the full set instead of hand-maintaining a central list, so adding a guarded handler
/// contributes its permission to seeding and policy automatically, with no central edit.
/// </summary>
/// <remarks>
/// Populated by the Elarion source generators: each module registers one <see cref="PermissionCatalogModule"/>
/// (gated by module enablement) through its generated <c>ConfigureDefaultServices</c>, and this catalog aggregates
/// every registered instance — so it spans referenced module assemblies, not just the host. Resolve it from DI.
/// For compile-time use (e.g. static role-policy declarations) reference the generated <c>ElarionPermissions</c>
/// static instead.
/// </remarks>
/// <example>
/// <code>
/// // Seed permission claims onto roles without a hand-kept Permissions.All list:
/// foreach (var permission in catalog.Permissions)
///     await EnsureRoleClaimAsync(adminRole, "permission", permission);
/// </code>
/// </example>
public interface IPermissionCatalog {
    /// <summary>All distinct composed permission strings (<c>{resource}.{verb}</c>), ordinally sorted.</summary>
    IReadOnlyList<string> Permissions { get; }

    /// <summary>All distinct role names across enabled modules, ordinally sorted.</summary>
    IReadOnlyList<string> Roles { get; }

    /// <summary>Permission strings grouped by their <c>resource</c> (Kubernetes "all verbs on a resource").</summary>
    IReadOnlyDictionary<string, IReadOnlyList<string>> ByResource { get; }

    /// <summary>Permission strings grouped by their <c>verb</c> (Kubernetes "this verb across all resources").</summary>
    IReadOnlyDictionary<string, IReadOnlyList<string>> ByVerb { get; }

    /// <summary>The per-module breakdown, ordered by module name.</summary>
    IReadOnlyList<PermissionCatalogModule> Modules { get; }
}

/// <summary>
/// A single module's contribution to the <see cref="IPermissionCatalog"/>: the permissions (each with its
/// <c>resource</c>/<c>verb</c>) and role names its handlers declare. The Elarion generator registers one instance
/// per module (gated by module enablement); <see cref="IPermissionCatalog"/> aggregates every registered instance.
/// </summary>
public sealed record PermissionCatalogModule {
    /// <summary>The owning module name (the <c>[AppModule]</c> name).</summary>
    public required string Module { get; init; }

    /// <summary>The distinct permissions declared by this module's handlers, ordered by composed value.</summary>
    public required IReadOnlyList<PermissionCatalogEntry> Permissions { get; init; }

    /// <summary>The distinct role names declared by this module's handlers, ordinally sorted.</summary>
    public required IReadOnlyList<string> Roles { get; init; }
}

/// <summary>A permission decomposed into its <c>resource</c> and <c>verb</c>, with the composed claim string.</summary>
public sealed record PermissionCatalogEntry {
    /// <summary>The composed permission string (e.g. <c>"properties.read"</c>) — the value seeded as a claim.</summary>
    public required string Permission { get; init; }

    /// <summary>The resource part (e.g. <c>"properties"</c>).</summary>
    public required string Resource { get; init; }

    /// <summary>The verb part (e.g. <c>"read"</c>).</summary>
    public required string Verb { get; init; }
}
