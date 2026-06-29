using Elarion.Abstractions.Authorization;

namespace Elarion.Authorization;

/// <summary>
/// The default <see cref="IPermissionCatalog"/>: aggregates every <see cref="PermissionCatalogModule"/> the
/// generators register (one per enabled module, across all assemblies) into the deduplicated, ordinally sorted
/// permission and role sets plus the per-resource and per-verb groupings. Computed once from the injected
/// contributions, so it reflects exactly the enabled modules wired into the container.
/// </summary>
internal sealed class PermissionCatalog : IPermissionCatalog {
    public PermissionCatalog(IEnumerable<PermissionCatalogModule> modules) {
        ArgumentNullException.ThrowIfNull(modules);

        Modules = modules
            .OrderBy(module => module.Module, StringComparer.Ordinal)
            .ToArray();

        var entries = Modules.SelectMany(module => module.Permissions).ToArray();
        Permissions = Distinct(entries.Select(entry => entry.Permission));
        Roles = Distinct(Modules.SelectMany(module => module.Roles));
        ByResource = Group(entries, entry => entry.Resource);
        ByVerb = Group(entries, entry => entry.Verb);
    }

    public IReadOnlyList<string> Permissions { get; }

    public IReadOnlyList<string> Roles { get; }

    public IReadOnlyDictionary<string, IReadOnlyList<string>> ByResource { get; }

    public IReadOnlyDictionary<string, IReadOnlyList<string>> ByVerb { get; }

    public IReadOnlyList<PermissionCatalogModule> Modules { get; }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> Group(
        IEnumerable<PermissionCatalogEntry> entries, Func<PermissionCatalogEntry, string> key) =>
        entries
            .GroupBy(key, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<string>)Distinct(group.Select(entry => entry.Permission)),
                StringComparer.Ordinal);

    private static string[] Distinct(IEnumerable<string> values) =>
        values
            .Where(value => !string.IsNullOrEmpty(value))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
}
