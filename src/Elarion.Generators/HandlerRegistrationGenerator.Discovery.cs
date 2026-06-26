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
        CancellationToken ct) {
        if (nodes.IsDefaultOrEmpty)
            return EquatableArray<HandlerInfo>.Empty;

        var moduleDecoratorLists = BuildModuleDecoratorMap(compilation, ct);
        var moduleAuthDefaults = BuildModuleAuthorizationDefaultsMap(compilation, ct);
        var assemblyRequireAuthenticated = ResolveAssemblyAuthorizationDefault(compilation);
        var builder = ImmutableArray.CreateBuilder<HandlerInfo>();
        foreach (var node in nodes) {
            ct.ThrowIfCancellationRequested();
            var semanticModel = compilation.GetSemanticModel(node.SyntaxTree);
            var info = GetHandlerInfo(
                node, semanticModel, moduleDecoratorLists, moduleAuthDefaults, assemblyRequireAuthenticated, ct);
            if (info is not null)
                builder.Add(info);
        }

        return builder.ToImmutable();
    }

    private static HandlerInfo? GetHandlerInfo(
        ClassDeclarationSyntax classDecl,
        SemanticModel semanticModel,
        IReadOnlyList<(string Namespace, AttributeData DecoratorList)> moduleDecoratorLists,
        IReadOnlyList<(string Namespace, bool RequireAuthenticated)> moduleAuthDefaults,
        bool assemblyRequireAuthenticated,
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

        var (hasAuthorization, requireAuthenticatedByDefault) = ParseAuthorization(
            classDecl, classSymbol, responseType, compilation, moduleAuthDefaults, assemblyRequireAuthenticated, diagnostics);

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
            diagnostics.ToImmutable());
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
    private static (bool HasAuthorization, bool RequireAuthenticatedByDefault) ParseAuthorization(
        ClassDeclarationSyntax classDecl,
        INamedTypeSymbol classSymbol,
        ITypeSymbol responseType,
        Compilation compilation,
        IReadOnlyList<(string Namespace, bool RequireAuthenticated)> moduleAuthDefaults,
        bool assemblyRequireAuthenticated,
        ImmutableArray<DiagnosticInfo>.Builder diagnostics) {
        var isAllowAnonymous = false;
        var hasExplicit = false;
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
            }
        }

        // AllowAnonymous wins: the handler is public, so no decorator is attached.
        if (isAllowAnonymous)
            return (false, false);

        var defaultRequireAuthenticated =
            ResolveAuthorizationDefault(classSymbol, moduleAuthDefaults, assemblyRequireAuthenticated);
        if (!hasExplicit && !defaultRequireAuthenticated)
            return (false, false);

        // Denial returns TResponse.Failure(...), which needs IResultFailureFactory<TResponse>. Without it the
        // check could not short-circuit and would be silently skipped — report ELAUTH001 instead of failing open.
        if (!ResponseSupportsFailure(responseType, compilation)) {
            diagnostics.Add(DiagnosticInfo.Create(
                AuthorizationResponseNotFailureCapable,
                classDecl.Identifier.GetLocation(),
                classSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                responseType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)));
            return (false, false);
        }

        return (true, defaultRequireAuthenticated);
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
}
