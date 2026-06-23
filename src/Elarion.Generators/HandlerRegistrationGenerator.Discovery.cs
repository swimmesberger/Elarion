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
        var builder = ImmutableArray.CreateBuilder<HandlerInfo>();
        foreach (var node in nodes) {
            ct.ThrowIfCancellationRequested();
            var semanticModel = compilation.GetSemanticModel(node.SyntaxTree);
            var info = GetHandlerInfo(node, semanticModel, moduleDecoratorLists, ct);
            if (info is not null)
                builder.Add(info);
        }

        return builder.ToImmutable();
    }

    private static HandlerInfo? GetHandlerInfo(
        ClassDeclarationSyntax classDecl,
        SemanticModel semanticModel,
        IReadOnlyList<(string Namespace, AttributeData DecoratorList)> moduleDecoratorLists,
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
}
