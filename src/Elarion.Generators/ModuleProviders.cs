using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Elarion.Generators;

/// <summary>
/// Shared incremental providers for module discovery and assembly-trigger gating, so each generator
/// stops re-scanning the whole compilation on every edit.
/// </summary>
internal static class ModuleProviders
{
    private const string AppModuleAttributeMetadataName = "Elarion.Abstractions.Modules.AppModuleAttribute";

    /// <summary>
    /// All <c>[AppModule]</c> declarations as a value-equatable array, recomputed only when an
    /// <c>[AppModule]</c> declaration changes. Replaces the full-compilation <c>ModuleScanner.Collect</c>.
    /// </summary>
    public static IncrementalValueProvider<EquatableArray<ModuleScanner.Module>> CollectModules(
        IncrementalGeneratorInitializationContext context) =>
        context.SyntaxProvider
            .ForAttributeWithMetadataName(
                AppModuleAttributeMetadataName,
                static (node, _) => node is TypeDeclarationSyntax,
                static (ctx, _) => CreateModule(ctx))
            .Where(static module => module is not null)
            .Select(static (module, _) => module!)
            .Collect()
            .Select(static (modules, _) => modules.ToEquatableArray());

    /// <summary>
    /// Whether the assembly opts into the given generator, projected to a <see cref="bool"/> so
    /// downstream nodes only re-run when the trigger attribute appears or disappears (not on every edit).
    /// </summary>
    public static IncrementalValueProvider<bool> HasTrigger(
        IncrementalGeneratorInitializationContext context,
        string triggerAttributeMetadataName) =>
        context.CompilationProvider.Select(
            (compilation, _) => FrameworkFeatureTriggers.HasAssemblyTrigger(compilation, triggerAttributeMetadataName));

    private static ModuleScanner.Module? CreateModule(GeneratorAttributeSyntaxContext ctx)
    {
        if (ctx.TargetSymbol is not INamedTypeSymbol type)
        {
            return null;
        }

        foreach (var attribute in ctx.Attributes)
        {
            if (attribute.ConstructorArguments.Length > 0 &&
                attribute.ConstructorArguments[0].Value is string name)
            {
                var ns = type.ContainingNamespace is { IsGlobalNamespace: false } containing
                    ? containing.ToDisplayString()
                    : string.Empty;
                return new ModuleScanner.Module(name, ns, type.Name, ModuleScanner.BuildMetadataName(type));
            }
        }

        return null;
    }
}
