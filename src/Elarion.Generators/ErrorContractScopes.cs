using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Elarion.Generators;

/// <summary>
/// Adds the error declarations an operation inherits from outside its handler class (ADR-0080): <c>[ProducesError]</c>
/// on the pipeline decorators its resolved <c>[DecoratorList]</c> attaches, on its <c>[AppModule]</c> class and on its
/// assembly. Those declarations live in other files, so this is a second stage over the discovered operations,
/// combined with the shared module provider and the current compilation: each handler is re-resolved by metadata
/// name (a symbol-table lookup, never a tree scan), and its decorator list is resolved by the same code the handler
/// registration generator uses, so the declared contract and the generated pipeline cannot disagree.
/// </summary>
/// <remarks>
/// A code declared closer to the handler wins: the handler's own declarations and the framework's implied failures
/// (already in <see cref="RpcMethodEmission.Model.Errors"/>), then the decorators in pipeline order (outermost
/// first), then the module, then the assembly. A decorator excluded by its generic constraints contributes nothing;
/// an <c>AppliesTo</c> predicate runs at runtime, so a conditional decorator contributes to every operation it can
/// wrap. Invalid or conflicting declarations on a decorator, module or assembly are reported once per pass.
/// </remarks>
internal sealed class ErrorContractScopes {
    private readonly Compilation _compilation;
    private readonly EquatableArray<ModuleScanner.Module> _modules;
    private readonly List<(string Namespace, AttributeData DecoratorList)> _moduleDecoratorLists;
    private readonly Dictionary<string, List<ErrorContractDiscovery.ErrorDeclaration>> _moduleDeclarations;
    private readonly List<ErrorContractDiscovery.ErrorDeclaration> _assemblyDeclarations;
    private readonly Dictionary<INamedTypeSymbol, List<ErrorContractDiscovery.ErrorDeclaration>> _decoratorDeclarations =
        new(SymbolEqualityComparer.Default);
    private readonly Action<DiagnosticInfo>? _report;

    private ErrorContractScopes(
        Compilation compilation,
        EquatableArray<ModuleScanner.Module> modules,
        Action<DiagnosticInfo>? report,
        CancellationToken ct) {
        _compilation = compilation;
        _modules = modules;
        _report = report;
        _moduleDecoratorLists = HandlerRegistrationGenerator.BuildModuleMaps(compilation, modules, ct).DecoratorLists;

        _assemblyDeclarations = Read(
            compilation.Assembly.GetAttributes(),
            compilation.Assembly.Name,
            attribute => attribute.ApplicationSyntaxReference?.GetSyntax(ct).GetLocation());

        _moduleDeclarations = new Dictionary<string, List<ErrorContractDiscovery.ErrorDeclaration>>(
            StringComparer.Ordinal);
        foreach (var module in modules) {
            ct.ThrowIfCancellationRequested();
            if (compilation.Assembly.GetTypeByMetadataName(module.MetadataName) is not { } moduleSymbol)
                continue;

            var location = moduleSymbol.Locations.FirstOrDefault();
            _moduleDeclarations[module.MetadataName] =
                Read(moduleSymbol.GetAttributes(), moduleSymbol.ToDisplayString(), _ => location);
        }
    }

    /// <summary>
    /// Returns <paramref name="models"/> with every current-compilation operation's inherited declarations merged
    /// into its contract. <paramref name="report"/> receives the decorator, module and assembly diagnostics; pass
    /// <see langword="null"/> from a generator that does not own them.
    /// </summary>
    public static EquatableArray<RpcMethodEmission.Model> Apply(
        EquatableArray<RpcMethodEmission.Model> models,
        EquatableArray<ModuleScanner.Module> modules,
        Compilation compilation,
        Action<DiagnosticInfo>? report,
        CancellationToken ct) {
        // Without the attribute in the compilation's references nothing can be declared anywhere.
        if (compilation.GetTypeByMetadataName(ErrorContractDiscovery.ProducesErrorAttributeMetadataName) is null)
            return models;

        var scopes = new ErrorContractScopes(compilation, modules, report, ct);
        var result = new List<RpcMethodEmission.Model>(models.Length);
        foreach (var model in models) {
            ct.ThrowIfCancellationRequested();
            result.Add(scopes.Resolve(model));
        }

        return result.ToEquatableArray();
    }

    private RpcMethodEmission.Model Resolve(RpcMethodEmission.Model model) {
        if (model.HandlerMetadataName is null
            || _compilation.Assembly.GetTypeByMetadataName(model.HandlerMetadataName) is not { } handler)
            return model;

        var inherited = new List<ErrorContractDiscovery.ErrorDeclaration>();
        AddDecoratorDeclarations(handler, inherited);

        if (ModuleScanner.FindBest(model.HandlerNamespace, _modules) is { } module
            && _moduleDeclarations.TryGetValue(module.MetadataName, out var moduleDeclarations))
            inherited.AddRange(moduleDeclarations);

        inherited.AddRange(_assemblyDeclarations);
        if (inherited.Count == 0)
            return model;

        var byCode = new Dictionary<string, ErrorContractDiscovery.ErrorDeclaration>(StringComparer.Ordinal);
        foreach (var declaration in model.Errors)
            byCode[declaration.Code] = declaration;

        var added = false;
        foreach (var declaration in inherited)
            if (!byCode.ContainsKey(declaration.Code)) {
                byCode[declaration.Code] = declaration;
                added = true;
            }

        return added ? model with { Errors = ErrorContractDiscovery.Sorted(byCode.Values) } : model;
    }

    // The decorators the handler's resolved [DecoratorList] attaches, in list order, filtered by their generic
    // constraints exactly like HandlerRegistrationGenerator.ParseDecorators.
    private void AddDecoratorDeclarations(
        INamedTypeSymbol handler, List<ErrorContractDiscovery.ErrorDeclaration> inherited) {
        var decoratorList = HandlerRegistrationGenerator.ResolveDecoratorListFromPipelineAttributes(
            handler, _compilation, _moduleDecoratorLists);
        if (decoratorList is null || decoratorList.ConstructorArguments.Length == 0
            || decoratorList.ConstructorArguments[0].Kind != TypedConstantKind.Array
            || HandlerShape.FindHandlerInterface(handler) is not { } handlerInterface)
            return;

        var requestType = handlerInterface.TypeArguments[0];
        var responseType = handlerInterface.TypeArguments[1];
        foreach (var entry in decoratorList.ConstructorArguments[0].Values) {
            if (entry.Value is not INamedTypeSymbol decorator)
                continue;

            var definition = decorator.OriginalDefinition;
            if (!HandlerRegistrationGenerator.SatisfiesConstraints(definition, requestType, responseType))
                continue;

            inherited.AddRange(DecoratorDeclarations(definition));
        }
    }

    private List<ErrorContractDiscovery.ErrorDeclaration> DecoratorDeclarations(INamedTypeSymbol definition) {
        if (_decoratorDeclarations.TryGetValue(definition, out var cached))
            return cached;

        var byCode = new Dictionary<string, ErrorContractDiscovery.ErrorDeclaration>(StringComparer.Ordinal);
        var owner = definition.ToDisplayString();
        var location = definition.Locations.FirstOrDefault(static l => l.IsInSource);
        for (var current = definition; current is not null && current.SpecialType != SpecialType.System_Object;
             current = current.BaseType)
            ErrorContractDiscovery.ReadDeclarations(current.GetAttributes(), owner, _ => location, byCode, _report);

        var declarations = byCode.Values.ToList();
        _decoratorDeclarations[definition] = declarations;
        return declarations;
    }

    private List<ErrorContractDiscovery.ErrorDeclaration> Read(
        IEnumerable<AttributeData> attributes, string owner, Func<AttributeData, Location?> locate) {
        var byCode = new Dictionary<string, ErrorContractDiscovery.ErrorDeclaration>(StringComparer.Ordinal);
        ErrorContractDiscovery.ReadDeclarations(attributes, owner, locate, byCode, _report);
        return byCode.Values.ToList();
    }
}
