namespace Elarion.Generators;

/// <summary>
/// Shared module model and longest-prefix namespace association, used by generators that group
/// transport- or runtime-registrations by owning module. Discovery is an incremental provider:
/// see <see cref="ModuleProviders.CollectModules"/>.
/// </summary>
internal static class ModuleScanner
{
    public sealed record Module(string Name, string Namespace, string TypeName);

    public static Module? FindBest(string handlerNamespace, IReadOnlyList<Module> modules)
    {
        Module? best = null;
        foreach (var module in modules)
        {
            if (!IsInScope(handlerNamespace, module.Namespace))
                continue;
            if (best is null || module.Namespace.Length > best.Namespace.Length)
                best = module;
        }

        return best;
    }

    public static bool IsInScope(string candidateNamespace, string moduleNamespace) =>
        moduleNamespace.Length == 0 ||
        candidateNamespace == moduleNamespace ||
        candidateNamespace.StartsWith(moduleNamespace + ".", StringComparison.Ordinal);
}
