using Microsoft.CodeAnalysis;

namespace Elarion.Generators;

/// <summary>
/// Shared module model and longest-prefix namespace association, used by generators that group
/// transport- or runtime-registrations by owning module. Discovery is an incremental provider:
/// see <see cref="ModuleProviders.CollectModules"/>.
/// </summary>
internal static class ModuleScanner
{
    /// <summary>
    /// Two <c>[AppModule]</c> declarations share one module name. Module names key the generated hint names,
    /// the per-module extension methods, the bootstrapper's switch cases, and configuration gating — a duplicate
    /// either crashes a generator (duplicate AddSource hint) or emits uncompilable code (CS0111/CS0152), so it
    /// is reported and only one deterministic winner is generated.
    /// </summary>
    public static readonly DiagnosticDescriptor DuplicateModuleName = new(
        id: "ELMOD006",
        title: "Duplicate [AppModule] name",
        messageFormat:
        "Modules '{0}' and '{1}' both declare the [AppModule] name '{2}'; module names must be unique across the "
        + "application — only '{0}' (ordinal-first by type name) is generated",
        category: "Elarion.Modules",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>
    /// Collapses same-named module entries to one deterministic winner per name (ordinal-first by the entry's
    /// type identity), optionally reporting one <see cref="DuplicateModuleName"/> per discarded loser. Winner
    /// selection is independent of input order, so every generator that aggregates modules picks the same one.
    /// </summary>
    public static List<T> DeduplicateByName<T>(
        IEnumerable<T> entries,
        Func<T, string> nameSelector,
        Func<T, string> typeIdSelector,
        List<DiagnosticInfo>? diagnostics = null)
    {
        var order = new List<string>();
        var groups = new Dictionary<string, List<T>>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var name = nameSelector(entry);
            if (!groups.TryGetValue(name, out var group))
            {
                group = [];
                groups[name] = group;
                order.Add(name);
            }

            group.Add(entry);
        }

        var result = new List<T>(order.Count);
        foreach (var name in order)
        {
            var group = groups[name];
            group.Sort((left, right) =>
                string.Compare(typeIdSelector(left), typeIdSelector(right), StringComparison.Ordinal));
            result.Add(group[0]);

            if (diagnostics is null)
                continue;

            for (var i = 1; i < group.Count; i++)
            {
                diagnostics.Add(DiagnosticInfo.Create(
                    DuplicateModuleName,
                    (Location?)null,
                    typeIdSelector(group[0]),
                    typeIdSelector(group[i]),
                    name));
            }
        }

        return result;
    }

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
