using Microsoft.CodeAnalysis;

namespace Elarion.Generators;

/// <summary>
/// Shared module model and longest-prefix namespace association, used by generators that group
/// transport- or runtime-registrations by owning module. Discovery is an incremental provider:
/// see <see cref="ModuleProviders.CollectModules"/>.
/// </summary>
internal static class ModuleScanner
{
    /// <param name="Name">The module name from the <c>[AppModule]</c> attribute.</param>
    /// <param name="Namespace">The module type's containing namespace (empty for the global namespace).</param>
    /// <param name="TypeName">The module type's simple name.</param>
    /// <param name="MetadataName">
    /// The CLR metadata name (namespace-qualified, nested types joined with <c>+</c>, arity suffixes kept), so a
    /// later pipeline stage can re-resolve the module type from the current compilation via
    /// <c>IAssemblySymbol.GetTypeByMetadataName</c> without carrying the symbol through the (value-equatable)
    /// incremental model.
    /// </param>
    public sealed record Module(string Name, string Namespace, string TypeName, string MetadataName);

    /// <summary>
    /// Builds the <c>GetTypeByMetadataName</c>-compatible name for a source-declared type: namespace-qualified,
    /// nested types joined with <c>+</c>, generic arity suffixes preserved.
    /// </summary>
    public static string BuildMetadataName(INamedTypeSymbol type)
    {
        var parts = new List<string>();
        for (INamedTypeSymbol? current = type; current is not null; current = current.ContainingType)
            parts.Insert(0, current.MetadataName);

        var ns = type.ContainingNamespace is { IsGlobalNamespace: false } containing
            ? containing.ToDisplayString() + "."
            : string.Empty;
        return ns + string.Join("+", parts);
    }

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
