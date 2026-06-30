using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Elarion.Generators;

public sealed partial class HandlerRegistrationGenerator {
    /// <summary>
    /// Resolves every handler in one pass. The compilation is combined in for correctness (a handler's
    /// decorator pipeline depends on cross-file [DecoratorList] state), but the module [DecoratorList] map is
    /// built once here rather than rescanned per handler. The result is value-equatable, so emission is
    /// skipped when no handler model changed.
    /// </summary>
    private static EquatableArray<HandlerInfo> ResolveHandlers(
        ImmutableArray<ClassDeclarationSyntax> nodes,
        Compilation compilation,
        EquatableArray<string> variantContracts,
        CancellationToken ct) {
        if (nodes.IsDefaultOrEmpty)
            return EquatableArray<HandlerInfo>.Empty;

        var moduleDecoratorLists = BuildModuleDecoratorMap(compilation, ct);
        var moduleAuthDefaults = BuildModuleAuthorizationDefaultsMap(compilation, ct);
        var assemblyRequireAuthenticated = ResolveAssemblyAuthorizationDefault(compilation);
        var variantContractSet = variantContracts.IsEmpty
            ? null
            : new HashSet<string>(variantContracts.AsImmutableArray, StringComparer.Ordinal);
        var builder = ImmutableArray.CreateBuilder<HandlerInfo>();
        foreach (var node in nodes) {
            ct.ThrowIfCancellationRequested();
            var semanticModel = compilation.GetSemanticModel(node.SyntaxTree);
            var info = GetHandlerInfo(
                node, semanticModel, moduleDecoratorLists, moduleAuthDefaults, assemblyRequireAuthenticated,
                variantContractSet, ct);
            if (info is not null)
                builder.Add(info);
        }

        return builder.ToImmutable();
    }

    // The TContract of a [FeatureVariant<TContract>] attribute, fully qualified — the contract a variant implements.
    private static string? GetVariantContractFqn(GeneratorAttributeSyntaxContext ctx) {
        if (ctx.Attributes.Length == 0)
            return null;

        var attributeClass = ctx.Attributes[0].AttributeClass;
        if (attributeClass is null || attributeClass.TypeArguments.Length != 1)
            return null;

        return attributeClass.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    }

    private static EquatableArray<string> ToSortedDistinct(ImmutableArray<string> values) {
        if (values.IsDefaultOrEmpty)
            return EquatableArray<string>.Empty;

        return values.Distinct(StringComparer.Ordinal).OrderBy(static value => value, StringComparer.Ordinal).ToImmutableArray();
    }

    private static HandlerInfo? GetHandlerInfo(
        ClassDeclarationSyntax classDecl,
        SemanticModel semanticModel,
        IReadOnlyList<(string Namespace, AttributeData DecoratorList)> moduleDecoratorLists,
        IReadOnlyList<(string Namespace, bool RequireAuthenticated)> moduleAuthDefaults,
        bool assemblyRequireAuthenticated,
        HashSet<string>? variantContracts,
        CancellationToken ct) {
        if (semanticModel.GetDeclaredSymbol(classDecl, ct) is not INamedTypeSymbol classSymbol)
            return null;

        var compilation = semanticModel.Compilation;

        if (classSymbol.IsAbstract)
            return null;

        var handlerInterface = FindHandlerInterface(classSymbol);
        if (handlerInterface is null)
            return null;

        if (classSymbol.ContainingNamespace?.ToDisplayString().Contains("Decorators") == true)
            return null;

        var fmt = SymbolDisplayFormat.FullyQualifiedFormat;
        var requestType = handlerInterface.TypeArguments[0];
        var responseType = handlerInterface.TypeArguments[1];
        var requestFqn = requestType.ToDisplayString(fmt);
        var responseFqn = responseType.ToDisplayString(fmt);
        var handlerFqn = classSymbol.ToDisplayString(fmt);
        var handlerName = classSymbol.Name;
        var ns = classSymbol.ContainingNamespace?.ToDisplayString() ?? "";

        var diagnostics = ImmutableArray.CreateBuilder<DiagnosticInfo>();

        var decoratorListAttr = ResolveDecoratorListFromPipelineAttributes(classSymbol, compilation, moduleDecoratorLists);
        var decorators = decoratorListAttr is not null
            ? ParseDecorators(decoratorListAttr, requestType, responseType, compilation, diagnostics, fmt)
            : ImmutableArray<DecoratorInfo>.Empty;

        var cacheable = ParseCacheable(classSymbol, requestType, responseType, compilation, fmt);
        var cacheInvalidation = ParseCacheInvalidation(classSymbol, compilation);
        var resiliencePolicyName = ParseResilient(classSymbol);
        diagnostics.AddRange(ValidateCacheMetadata(classSymbol, cacheable, cacheInvalidation));

        var (hasAuthorization, requireAuthenticatedByDefault, resourceBindings) = ParseAuthorization(
            classDecl, classSymbol, requestType, responseType, compilation, moduleAuthDefaults, assemblyRequireAuthenticated, diagnostics);

        var hasFeatureGates = ParseFeatureGates(classDecl, classSymbol, responseType, compilation, diagnostics);

        var variantContractDeps = GetVariantContractDeps(classSymbol, variantContracts, fmt);

        return new HandlerInfo(
            handlerFqn,
            handlerName,
            requestFqn,
            responseFqn,
            ns,
            decorators,
            resiliencePolicyName,
            cacheable,
            cacheInvalidation,
            hasAuthorization,
            requireAuthenticatedByDefault,
            resourceBindings,
            hasFeatureGates,
            variantContractDeps,
            diagnostics.ToImmutable());
    }

    // A handler whose constructor injects a variant contract is registered behind the async-resolving proxy so the
    // contract's variant is awaited (per user) before the handler is built. Returns the distinct variant contracts
    // the handler depends on, in constructor-parameter order; empty for the common (no-variant) case.
    private static EquatableArray<string> GetVariantContractDeps(
        INamedTypeSymbol classSymbol,
        HashSet<string>? variantContracts,
        SymbolDisplayFormat fmt) {
        if (variantContracts is null)
            return EquatableArray<string>.Empty;

        var constructor = SelectConstructor(classSymbol);
        if (constructor is null)
            return EquatableArray<string>.Empty;

        ImmutableArray<string>.Builder? builder = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var parameter in constructor.Parameters) {
            var parameterFqn = parameter.Type.ToDisplayString(fmt);
            if (variantContracts.Contains(parameterFqn) && seen.Add(parameterFqn)) {
                builder ??= ImmutableArray.CreateBuilder<string>();
                builder.Add(parameterFqn);
            }
        }

        return builder is null ? EquatableArray<string>.Empty : builder.ToImmutable();
    }

    // The constructor DI resolves: the public instance constructor with the most parameters (greedy selection).
    private static IMethodSymbol? SelectConstructor(INamedTypeSymbol classSymbol) {
        IMethodSymbol? best = null;
        foreach (var constructor in classSymbol.InstanceConstructors) {
            if (constructor.DeclaredAccessibility != Accessibility.Public)
                continue;

            if (best is null || constructor.Parameters.Length > best.Parameters.Length)
                best = constructor;
        }

        return best;
    }

    private static INamedTypeSymbol? FindHandlerInterface(INamedTypeSymbol classSymbol) {
        foreach (var iface in classSymbol.AllInterfaces) {
            if (iface.OriginalDefinition.ToDisplayString() == "Elarion.Abstractions.IHandler<TRequest, TResponse>") {
                return iface;
            }
        }

        return null;
    }

    private static string? ParseResilient(INamedTypeSymbol classSymbol) {
        var attribute = classSymbol.GetAttributes()
            .FirstOrDefault(candidate => candidate.AttributeClass?.ToDisplayString() == ResilientAttributeMetadataName);
        if (attribute is null ||
            attribute.ConstructorArguments.Length == 0 ||
            attribute.ConstructorArguments[0].Value is not string policyName ||
            string.IsNullOrWhiteSpace(policyName)) {
            return null;
        }

        return policyName;
    }

    // Decides whether the authorization decorator attaches to this handler, and whether it enforces an
    // authenticated principal by default. The decorator is auto-appended (not listed in a [DecoratorList]), so
    // attachment is a compile-time presence decision the generator makes by inspecting the handler symbol — an
    // AppliesTo predicate is a runtime gate that always emits the decorator. A default policy
    // ([ElarionAuthorizationDefaults] at module/assembly scope) attaches to every non-anonymous handler.
    private static (bool HasAuthorization, bool RequireAuthenticatedByDefault, EquatableArray<ResourceBindingInfo> ResourceBindings) ParseAuthorization(
        ClassDeclarationSyntax classDecl,
        INamedTypeSymbol classSymbol,
        ITypeSymbol requestType,
        ITypeSymbol responseType,
        Compilation compilation,
        IReadOnlyList<(string Namespace, bool RequireAuthenticated)> moduleAuthDefaults,
        bool assemblyRequireAuthenticated,
        ImmutableArray<DiagnosticInfo>.Builder diagnostics) {
        EquatableArray<ResourceBindingInfo> noBindings = ImmutableArray<ResourceBindingInfo>.Empty;

        var isAllowAnonymous = false;
        var hasExplicit = false;
        var resourceAttributes = new List<AttributeData>();
        foreach (var attribute in classSymbol.GetAttributes()) {
            switch (attribute.AttributeClass?.ToDisplayString()) {
                case AllowAnonymousAttributeMetadataName:
                    isAllowAnonymous = true;
                    break;
                case RequireClaimAttributeMetadataName:
                case RequirePermissionAttributeMetadataName:
                case RequireRoleAttributeMetadataName:
                case RequirePolicyAttributeMetadataName:
                    hasExplicit = true;
                    break;
                case RequireResourceAttributeMetadataName:
                    hasExplicit = true;
                    resourceAttributes.Add(attribute);
                    break;
            }
        }

        // AllowAnonymous wins: the handler is public, so no decorator is attached.
        if (isAllowAnonymous)
            return (false, false, noBindings);

        var defaultRequireAuthenticated =
            ResolveAuthorizationDefault(classSymbol, moduleAuthDefaults, assemblyRequireAuthenticated);
        if (!hasExplicit && !defaultRequireAuthenticated)
            return (false, false, noBindings);

        // Denial returns TResponse.Failure(...), which needs IResultFailureFactory<TResponse>. Without it the
        // check could not short-circuit and would be silently skipped — report ELAUTH001 instead of failing open.
        if (!ResponseSupportsFailure(responseType, compilation)) {
            diagnostics.Add(DiagnosticInfo.Create(
                AuthorizationResponseNotFailureCapable,
                classDecl.Identifier.GetLocation(),
                classSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                responseType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)));
            return (false, false, noBindings);
        }

        var bindings = BuildResourceBindings(classDecl, classSymbol, requestType, resourceAttributes, diagnostics);
        return (true, defaultRequireAuthenticated, bindings);
    }

    // ADR-0012 Tier 1: a [RequireResource] references the resource id by a compile-checked path on the request.
    // The path is validated against the request type and emitted as a typed accessor; an unresolvable path is
    // ELAUTH002 (never a runtime surprise).
    private static EquatableArray<ResourceBindingInfo> BuildResourceBindings(
        ClassDeclarationSyntax classDecl,
        INamedTypeSymbol classSymbol,
        ITypeSymbol requestType,
        List<AttributeData> resourceAttributes,
        ImmutableArray<DiagnosticInfo>.Builder diagnostics) {
        if (resourceAttributes.Count == 0)
            return ImmutableArray<ResourceBindingInfo>.Empty;

        var fmt = SymbolDisplayFormat.FullyQualifiedFormat;
        var builder = ImmutableArray.CreateBuilder<ResourceBindingInfo>();
        foreach (var attribute in resourceAttributes) {
            if (attribute.ConstructorArguments.Length == 0 ||
                attribute.ConstructorArguments[0].Value is not INamedTypeSymbol resourceType)
                continue;

            var operation = "read";
            var idPath = "Id";
            foreach (var named in attribute.NamedArguments) {
                if (named.Key == "Operation" && named.Value.Value is string op && op.Length > 0)
                    operation = op;
                else if (named.Key == "Id" && named.Value.Value is string id && id.Length > 0)
                    idPath = id;
            }

            if (!ResourcePathResolves(requestType, idPath)) {
                diagnostics.Add(DiagnosticInfo.Create(
                    ResourceIdPathNotFound,
                    classDecl.Identifier.GetLocation(),
                    classSymbol.ToDisplayString(fmt),
                    idPath,
                    requestType.ToDisplayString(fmt)));
                continue;
            }

            builder.Add(new ResourceBindingInfo(resourceType.ToDisplayString(fmt), operation, idPath));
        }

        return builder.ToImmutable();
    }

    private static bool ResourcePathResolves(ITypeSymbol requestType, string path) {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var current = requestType;
        foreach (var rawSegment in path.Split('.')) {
            var segment = rawSegment.Trim();
            if (segment.Length == 0)
                return false;

            var property = FindPublicInstanceProperty(current, segment);
            if (property is null)
                return false;

            current = property.Type;
        }

        return true;
    }

    private static IPropertySymbol? FindPublicInstanceProperty(ITypeSymbol type, string name) {
        for (ITypeSymbol? current = type; current is not null; current = current.BaseType) {
            foreach (var member in current.GetMembers(name)) {
                if (member is IPropertySymbol { IsStatic: false, GetMethod: not null } property &&
                    property.DeclaredAccessibility == Accessibility.Public) {
                    return property;
                }
            }
        }

        return null;
    }

    private static bool ResolveAuthorizationDefault(
        INamedTypeSymbol classSymbol,
        IReadOnlyList<(string Namespace, bool RequireAuthenticated)> moduleAuthDefaults,
        bool assemblyRequireAuthenticated) {
        var handlerNamespace = classSymbol.ContainingNamespace?.ToDisplayString() ?? "";
        var hasModule = false;
        var bestValue = false;
        var bestNamespaceLength = -1;

        // Most-specific-wins: the handler's nearest module overrides the assembly default.
        foreach (var (moduleNamespace, requireAuthenticated) in moduleAuthDefaults) {
            if (!IsNamespaceInScope(handlerNamespace, moduleNamespace) ||
                moduleNamespace.Length <= bestNamespaceLength) {
                continue;
            }

            hasModule = true;
            bestValue = requireAuthenticated;
            bestNamespaceLength = moduleNamespace.Length;
        }

        return hasModule ? bestValue : assemblyRequireAuthenticated;
    }

    private static List<(string Namespace, bool RequireAuthenticated)> BuildModuleAuthorizationDefaultsMap(
        Compilation compilation,
        CancellationToken ct) {
        var result = new List<(string, bool)>();
        var defaultsAttr = compilation.GetTypeByMetadataName(AuthorizationDefaultsAttributeMetadataName);
        var moduleAttrSymbol = compilation.GetTypeByMetadataName(AppModuleAttributeMetadataName);
        if (defaultsAttr is null || moduleAttrSymbol is null)
            return result;

        foreach (var syntaxTree in compilation.SyntaxTrees) {
            var semanticModel = compilation.GetSemanticModel(syntaxTree);
            var root = syntaxTree.GetRoot(ct);

            foreach (var typeDecl in root.DescendantNodes().OfType<TypeDeclarationSyntax>()) {
                if (semanticModel.GetDeclaredSymbol(typeDecl, ct) is not INamedTypeSymbol typeSymbol)
                    continue;

                var hasAppModule = typeSymbol.GetAttributes()
                    .Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, moduleAttrSymbol));
                if (!hasAppModule)
                    continue;

                var defaults = typeSymbol.GetAttributes()
                    .FirstOrDefault(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, defaultsAttr));
                if (defaults is null)
                    continue;

                var moduleNamespace = typeSymbol.ContainingNamespace is { IsGlobalNamespace: false } containing
                    ? containing.ToDisplayString()
                    : "";
                result.Add((moduleNamespace, ReadRequireAuthenticated(defaults)));
            }
        }

        return result;
    }

    private static bool ResolveAssemblyAuthorizationDefault(Compilation compilation) {
        var defaultsAttr = compilation.GetTypeByMetadataName(AuthorizationDefaultsAttributeMetadataName);
        if (defaultsAttr is null)
            return false;

        var attribute = compilation.Assembly.GetAttributes()
            .FirstOrDefault(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, defaultsAttr));
        return attribute is not null && ReadRequireAuthenticated(attribute);
    }

    private static bool ReadRequireAuthenticated(AttributeData attribute) {
        foreach (var namedArgument in attribute.NamedArguments) {
            if (namedArgument.Key == "RequireAuthenticated" && namedArgument.Value.Value is bool value)
                return value;
        }

        // The attribute's RequireAuthenticated property defaults to true when not explicitly set.
        return true;
    }

    private static bool ResponseSupportsFailure(ITypeSymbol responseType, Compilation compilation) {
        var failureFactory = compilation.GetTypeByMetadataName(ResultFailureFactoryMetadataName);
        if (failureFactory is null)
            return false;

        var constructed = failureFactory.Construct(responseType);
        return SatisfiesType(responseType, constructed);
    }

    // Decides whether the feature-gate decorator attaches to this handler. Like authorization, attachment is a
    // compile-time presence decision (any [FeatureGate] on the handler), and a closed gate returns
    // TResponse.Failure(AppError.NotFound(...)) — so the response must implement IResultFailureFactory<TResponse>
    // (ELFEAT001) or the gate would be silently skipped. A [FeatureGate] with no/blank feature name is reported
    // (ELFEAT002) but is otherwise inert.
    private static bool ParseFeatureGates(
        ClassDeclarationSyntax classDecl,
        INamedTypeSymbol classSymbol,
        ITypeSymbol responseType,
        Compilation compilation,
        ImmutableArray<DiagnosticInfo>.Builder diagnostics) {
        var gateAttributes = classSymbol.GetAttributes()
            .Where(candidate => candidate.AttributeClass?.ToDisplayString() == FeatureGateAttributeMetadataName)
            .ToList();
        if (gateAttributes.Count == 0)
            return false;

        var fmt = SymbolDisplayFormat.FullyQualifiedFormat;

        if (!ResponseSupportsFailure(responseType, compilation)) {
            diagnostics.Add(DiagnosticInfo.Create(
                FeatureGateResponseNotFailureCapable,
                classDecl.Identifier.GetLocation(),
                classSymbol.ToDisplayString(fmt),
                responseType.ToDisplayString(fmt)));
            return false;
        }

        foreach (var attribute in gateAttributes) {
            if (HasBlankFeatureName(attribute)) {
                diagnostics.Add(DiagnosticInfo.Create(
                    EmptyFeatureGateDescriptor,
                    classDecl.Identifier.GetLocation(),
                    classSymbol.ToDisplayString(fmt)));
            }
        }

        return true;
    }

    // The feature names are the trailing `params string[]` constructor argument, which Roslyn surfaces as a single
    // array-kind TypedConstant regardless of which [FeatureGate] constructor was used.
    private static bool HasBlankFeatureName(AttributeData attribute) {
        if (attribute.ConstructorArguments.Length == 0)
            return true;

        var featuresArg = attribute.ConstructorArguments[attribute.ConstructorArguments.Length - 1];
        if (featuresArg.Kind != TypedConstantKind.Array || featuresArg.Values.Length == 0)
            return true;

        foreach (var value in featuresArg.Values) {
            if (value.Value is not string feature || string.IsNullOrWhiteSpace(feature))
                return true;
        }

        return false;
    }
}
