using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Elarion.Generators;

/// <summary>
/// Reports when a type in one <c>[AppModule]</c> depends on another module's <em>internal</em> type
/// (a <c>[Service]</c>, a handler, an <c>[EntityConfiguration]</c>, or a configured entity — the entity
/// behind an <c>[EntityConfiguration]</c>, since entities carry no marker) instead of a published
/// <c>[ModuleContract]</c>.
/// </summary>
/// <remarks>
/// <para>
/// The rule keeps modules honest: cross-module collaboration must go through an intentional, published
/// surface, not a module's internals. It inspects the <em>dependency surface</em> of each type
/// (constructor parameters, fields, properties) — the place foreign internals leak in via DI — rather
/// than every reference, to stay precise and low-noise. Framework and shared-kernel types are exempt
/// automatically: a type whose namespace is under no <c>[AppModule]</c> has no owning module and is
/// never flagged.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ModuleBoundaryAnalyzer : DiagnosticAnalyzer
{
    private const string AppModuleAttributeMetadataName = "Elarion.Abstractions.Modules.AppModuleAttribute";
    private const string ServiceAttributeMetadataName = "Elarion.Abstractions.ServiceAttribute";
    private const string ModuleContractAttributeMetadataName = "Elarion.Abstractions.Modules.ModuleContractAttribute";
    private const string EntityConfigurationAttributeMetadataName = "Elarion.EntityFrameworkCore.EntityConfigurationAttribute";
    private const string EntityTypeConfigurationMetadataName = "Microsoft.EntityFrameworkCore.IEntityTypeConfiguration`1";
    private const string HandlerInterfaceMetadataName = "Elarion.Abstractions.IHandler`2";

    private static readonly DiagnosticDescriptor CrossModuleInternalReference = new(
        "ELMOD002",
        "Cross-module reference to a module-internal type",
        "Type '{0}' is internal to module '{1}'; reference it from module '{2}' through a [ModuleContract] interface instead of depending on the module's internals",
        "Elarion.Modules",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description:
        "A module should collaborate with another module through a published [ModuleContract], not by " +
        "injecting or depending on the other module's internal [Service], handler, [EntityConfiguration], " +
        "or configured entity types.");

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

            var serviceAttr = start.Compilation.GetTypeByMetadataName(ServiceAttributeMetadataName);
            var contractAttr = start.Compilation.GetTypeByMetadataName(ModuleContractAttributeMetadataName);
            var entityConfigAttr = start.Compilation.GetTypeByMetadataName(EntityConfigurationAttributeMetadataName);
            var configInterface = start.Compilation.GetTypeByMetadataName(EntityTypeConfigurationMetadataName);
            var handlerInterface = start.Compilation.GetTypeByMetadataName(HandlerInterfaceMetadataName);

            // Entities carry no marker attribute anymore — a configured entity is the entity type behind an
            // [EntityConfiguration] class. Collect them once so a cross-module reference to one is still
            // flagged. Same-compilation collection is sufficient: a module in a separate assembly is not in
            // this compilation's module set and is never matched anyway.
            var configuredEntities = CollectConfiguredEntities(
                start.Compilation, entityConfigAttr, configInterface, start.CancellationToken);

            var state = new BoundaryState(
                modules, serviceAttr, contractAttr, entityConfigAttr, configuredEntities, handlerInterface);
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

            if (!IsModuleInternal(named, state))
                continue; // Plain DTOs and shared types are not gated.

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

    private static bool IsModuleInternal(INamedTypeSymbol type, BoundaryState state)
    {
        if (HasAttribute(type, state.ServiceAttribute) ||
            HasAttribute(type, state.EntityConfigurationAttribute) ||
            state.ConfiguredEntities.Contains(type))
            return true;

        if (state.HandlerInterface is null)
            return false;

        foreach (var iface in type.AllInterfaces)
        {
            if (SymbolEqualityComparer.Default.Equals(iface.OriginalDefinition, state.HandlerInterface))
                return true;
        }

        return false;
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

    private static HashSet<INamedTypeSymbol> CollectConfiguredEntities(
        Compilation compilation,
        INamedTypeSymbol? entityConfigAttr,
        INamedTypeSymbol? configInterface,
        CancellationToken ct)
    {
        var entities = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        if (entityConfigAttr is null || configInterface is null)
            return entities;

        WalkConfigs(compilation.Assembly.GlobalNamespace, entityConfigAttr, configInterface, entities, ct);
        return entities;
    }

    private static void WalkConfigs(
        INamespaceSymbol namespaceSymbol,
        INamedTypeSymbol entityConfigAttr,
        INamedTypeSymbol configInterface,
        HashSet<INamedTypeSymbol> entities,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        foreach (var type in namespaceSymbol.GetTypeMembers())
            InspectConfig(type, entityConfigAttr, configInterface, entities, ct);

        foreach (var nested in namespaceSymbol.GetNamespaceMembers())
            WalkConfigs(nested, entityConfigAttr, configInterface, entities, ct);
    }

    private static void InspectConfig(
        INamedTypeSymbol type,
        INamedTypeSymbol entityConfigAttr,
        INamedTypeSymbol configInterface,
        HashSet<INamedTypeSymbol> entities,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (HasAttribute(type, entityConfigAttr))
        {
            foreach (var iface in type.AllInterfaces)
            {
                if (SymbolEqualityComparer.Default.Equals(iface.OriginalDefinition, configInterface) &&
                    iface.TypeArguments.Length == 1 &&
                    iface.TypeArguments[0] is INamedTypeSymbol entity)
                    entities.Add(entity);
            }
        }

        foreach (var nested in type.GetTypeMembers())
            InspectConfig(nested, entityConfigAttr, configInterface, entities, ct);
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
        INamedTypeSymbol? ServiceAttribute,
        INamedTypeSymbol? ContractAttribute,
        INamedTypeSymbol? EntityConfigurationAttribute,
        HashSet<INamedTypeSymbol> ConfiguredEntities,
        INamedTypeSymbol? HandlerInterface);
}
