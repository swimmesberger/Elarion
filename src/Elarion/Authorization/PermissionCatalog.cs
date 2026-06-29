using Elarion.Abstractions.Authorization;

namespace Elarion.Authorization;

/// <summary>
/// The default <see cref="IPermissionCatalog"/>: aggregates every <see cref="PermissionCatalogModule"/> the
/// generators register (one per enabled module, across all assemblies) into the deduplicated, ordinally sorted
/// permission and role sets. Computed once from the injected contributions, so it reflects exactly the enabled
/// modules wired into the container.
/// </summary>
internal sealed class PermissionCatalog : IPermissionCatalog {
    public PermissionCatalog(IEnumerable<PermissionCatalogModule> modules) {
        ArgumentNullException.ThrowIfNull(modules);

        Modules = modules
            .OrderBy(module => module.Module, StringComparer.Ordinal)
            .ToArray();
        Permissions = Distinct(Modules.SelectMany(module => module.Permissions));
        Roles = Distinct(Modules.SelectMany(module => module.Roles));
    }

    public IReadOnlyList<string> Permissions { get; }

    public IReadOnlyList<string> Roles { get; }

    public IReadOnlyList<PermissionCatalogModule> Modules { get; }

    private static string[] Distinct(IEnumerable<string> values) =>
        values
            .Where(value => !string.IsNullOrEmpty(value))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
}
