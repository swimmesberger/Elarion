using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Elarion.Generators;

/// <summary>
/// Shared discovery of <c>[AppModule]</c> declarations and longest-prefix namespace association,
/// used by generators that group transport- or runtime-registrations by owning module.
/// </summary>
internal static class ModuleScanner
{
    private const string AppModuleAttributeMetadataName = "Elarion.Abstractions.Modules.AppModuleAttribute";

    public sealed record Module(string Name, string Namespace, string TypeName);

    public static IReadOnlyList<Module> Collect(Compilation compilation, CancellationToken ct)
    {
        var attribute = compilation.GetTypeByMetadataName(AppModuleAttributeMetadataName);
        if (attribute is null)
            return [];

        var modules = new List<Module>();
        var seen = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        foreach (var syntaxTree in compilation.SyntaxTrees)
        {
            ct.ThrowIfCancellationRequested();
            var semanticModel = compilation.GetSemanticModel(syntaxTree);
            foreach (var typeDeclaration in syntaxTree.GetRoot(ct).DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                if (semanticModel.GetDeclaredSymbol(typeDeclaration, ct) is not INamedTypeSymbol type || !seen.Add(type))
                    continue;

                var moduleAttribute = type.GetAttributes()
                    .FirstOrDefault(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, attribute));
                if (moduleAttribute is null ||
                    moduleAttribute.ConstructorArguments.Length == 0 ||
                    moduleAttribute.ConstructorArguments[0].Value is not string name)
                {
                    continue;
                }

                var ns = type.ContainingNamespace is { IsGlobalNamespace: false } containing
                    ? containing.ToDisplayString()
                    : string.Empty;
                modules.Add(new Module(name, ns, type.Name));
            }
        }

        return modules;
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
