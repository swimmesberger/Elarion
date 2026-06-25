using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Elarion.Generators;

/// <summary>
/// Reports when a type in one <c>[AppModule]</c> depends on a type owned by <em>another</em> module that
/// is not a published <c>[ModuleContract]</c>.
/// </summary>
/// <remarks>
/// <para>
/// The rule is purely <em>location</em>-based: everything under an <c>[AppModule]</c> namespace is
/// module-internal and not shareable across modules; everything <em>outside</em> every module is
/// shareable. So a cross-module dependency is allowed only when the referenced type either lives under no
/// module (the shared kernel — entities and value objects — and platform-capability ports such as
/// <c>IEmailSender</c>) or is marked <c>[ModuleContract]</c> (a module's deliberately published,
/// used-sparingly cross-module surface). Everything else owned by another module — entities, DTOs,
/// <c>[Service]</c>s, handlers, <c>[EntityConfiguration]</c>s — is internal and flagged.
/// </para>
/// <para>
/// It inspects the <em>dependency surface</em> of each type (constructor parameters, fields, properties) —
/// the place foreign internals leak in via DI — rather than every reference, to stay precise and low-noise.
/// A shared-kernel entity is shareable because it lives <em>outside</em> a module, not because entities are
/// special: a module that deliberately owns its data by placing entities in its own namespace makes those
/// entities module-internal, which is how a mini bounded context earns data ownership (see ADR-0008).
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ModuleBoundaryAnalyzer : DiagnosticAnalyzer
{
    private const string AppModuleAttributeMetadataName = "Elarion.Abstractions.Modules.AppModuleAttribute";
    private const string ModuleContractAttributeMetadataName = "Elarion.Abstractions.Modules.ModuleContractAttribute";

    private static readonly DiagnosticDescriptor CrossModuleInternalReference = new(
        "ELMOD002",
        "Cross-module reference to a module-internal type",
        "Type '{0}' belongs to module '{1}'; module '{2}' must not depend on another module's internals — reach it through a [ModuleContract], or move the shared type out of the module",
        "Elarion.Modules",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description:
        "Everything under an [AppModule] is module-internal and not shareable across modules except a " +
        "published [ModuleContract]; everything outside every module is shareable. Resolve a cross-module " +
        "dependency by (a) using a [ModuleContract] for a genuine, sparingly-used cross-module domain call, " +
        "(b) depending on a platform-capability port that lives outside the modules (the port/adapter " +
        "pattern, like IEmailSender), or (c) moving shared data and value types to the shared kernel " +
        "(under no module). The analyzer inspects only the dependency surface — constructor parameters, " +
        "fields, and properties.");

    private static readonly ImmutableArray<DiagnosticDescriptor> SupportedDiagnosticsArray =
        ImmutableArray.Create(CrossModuleInternalReference);

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => SupportedDiagnosticsArray;

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationStartAction(start =>
        {
            var appModule = start.Compilation.GetTypeByMetadataName(AppModuleAttributeMetadataName);
            if (appModule is null)
                return;

            var modules = CollectModules(start.Compilation, appModule, start.CancellationToken);
            if (modules.Count < 2)
                return; // A single module (or none) has no cross-module boundary to enforce.

            var contractAttr = start.Compilation.GetTypeByMetadataName(ModuleContractAttributeMetadataName);

            var state = new BoundaryState(modules, contractAttr);
            start.RegisterSymbolAction(symbolContext => AnalyzeType(symbolContext, state), SymbolKind.NamedType);
        });
    }

    private static void AnalyzeType(SymbolAnalysisContext context, BoundaryState state)
    {
        if (context.Symbol is not INamedTypeSymbol type || type.IsImplicitlyDeclared)
            return;

        var ownerNamespace = type.ContainingNamespace?.ToDisplayString() ?? string.Empty;
        var owner = FindBest(ownerNamespace, state.Modules);
        if (owner is null)
            return; // The referencing type is not in any module: nothing to enforce.

        foreach (var member in type.GetMembers())
        {
            switch (member)
            {
                case IMethodSymbol { MethodKind: MethodKind.Constructor } ctor:
                    foreach (var parameter in ctor.Parameters)
                        Check(context, state, owner, parameter.Type, parameter.Locations);
                    break;
                case IFieldSymbol { IsImplicitlyDeclared: false } field:
                    Check(context, state, owner, field.Type, field.Locations);
                    break;
                case IPropertySymbol property:
                    Check(context, state, owner, property.Type, property.Locations);
                    break;
            }
        }
    }

    private static void Check(
        SymbolAnalysisContext context,
        BoundaryState state,
        ModuleInfo owner,
        ITypeSymbol referenced,
        ImmutableArray<Location> locations)
    {
        foreach (var candidate in Flatten(referenced))
        {
            if (candidate is not INamedTypeSymbol named)
                continue;

            var referencedNamespace = named.ContainingNamespace?.ToDisplayString() ?? string.Empty;
            var referencedModule = FindBest(referencedNamespace, state.Modules);
            if (referencedModule is null || referencedModule.Equals(owner))
                continue;

            if (HasAttribute(named, state.ContractAttribute))
                continue; // A published contract is the allowed cross-module surface.

            // Everything else owned by another module — entities, DTOs, [Service]s, handlers,
            // [EntityConfiguration]s — is module-internal. Shared types must live outside every module.
            var location = locations.FirstOrDefault() ?? Location.None;
            context.ReportDiagnostic(Diagnostic.Create(
                CrossModuleInternalReference,
                location,
                named.Name,
                referencedModule.Name,
                owner.Name));
            return;
        }
    }

    private static IEnumerable<ITypeSymbol> Flatten(ITypeSymbol type)
    {
        yield return type;
        if (type is INamedTypeSymbol { IsGenericType: true } named)
        {
            foreach (var argument in named.TypeArguments)
            {
                foreach (var nested in Flatten(argument))
                    yield return nested;
            }
        }
    }

    private static bool HasAttribute(ISymbol symbol, INamedTypeSymbol? attribute)
    {
        if (attribute is null)
            return false;

        foreach (var data in symbol.GetAttributes())
        {
            if (SymbolEqualityComparer.Default.Equals(data.AttributeClass, attribute))
                return true;
        }

        return false;
    }

    private static List<ModuleInfo> CollectModules(Compilation compilation, INamedTypeSymbol appModule, CancellationToken ct)
    {
        var modules = new List<ModuleInfo>();
        Walk(compilation.Assembly.GlobalNamespace, appModule, modules, ct);
        return modules;
    }

    private static void Walk(INamespaceSymbol namespaceSymbol, INamedTypeSymbol appModule, List<ModuleInfo> modules, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        foreach (var type in namespaceSymbol.GetTypeMembers())
            Inspect(type, appModule, modules, ct);

        foreach (var nested in namespaceSymbol.GetNamespaceMembers())
            Walk(nested, appModule, modules, ct);
    }

    private static void Inspect(INamedTypeSymbol type, INamedTypeSymbol appModule, List<ModuleInfo> modules, CancellationToken ct)
    {
        foreach (var data in type.GetAttributes())
        {
            if (!SymbolEqualityComparer.Default.Equals(data.AttributeClass, appModule))
                continue;

            if (data.ConstructorArguments.Length > 0 && data.ConstructorArguments[0].Value is string name)
            {
                var ns = type.ContainingNamespace is { IsGlobalNamespace: false } containing
                    ? containing.ToDisplayString()
                    : string.Empty;
                modules.Add(new ModuleInfo(name, ns));
            }

            break;
        }

        foreach (var nested in type.GetTypeMembers())
            Inspect(nested, appModule, modules, ct);
    }

    private static ModuleInfo? FindBest(string candidateNamespace, IReadOnlyList<ModuleInfo> modules)
    {
        ModuleInfo? best = null;
        foreach (var module in modules)
        {
            if (!IsInScope(candidateNamespace, module.Namespace))
                continue;
            if (best is null || module.Namespace.Length > best.Namespace.Length)
                best = module;
        }

        return best;
    }

    private static bool IsInScope(string candidateNamespace, string moduleNamespace) =>
        moduleNamespace.Length == 0 ||
        candidateNamespace == moduleNamespace ||
        candidateNamespace.StartsWith(moduleNamespace + ".", StringComparison.Ordinal);

    private sealed record ModuleInfo(string Name, string Namespace);

    private sealed record BoundaryState(
        IReadOnlyList<ModuleInfo> Modules,
        INamedTypeSymbol? ContractAttribute);
}
