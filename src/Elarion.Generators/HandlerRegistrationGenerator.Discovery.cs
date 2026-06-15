using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Elarion.Generators;

public sealed partial class HandlerRegistrationGenerator {
    private static HandlerInfo? GetHandlerInfo(
        ClassDeclarationSyntax classDecl,
        Compilation compilation,
        CancellationToken ct) {
        var semanticModel = compilation.GetSemanticModel(classDecl.SyntaxTree);
        if (semanticModel.GetDeclaredSymbol(classDecl, ct) is not INamedTypeSymbol classSymbol)
            return null;

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

        var decoratorListAttr = ResolveDecoratorListFromPipelineAttributes(classSymbol, compilation, ct);
        var decorators = decoratorListAttr is not null
            ? ParseDecorators(decoratorListAttr, requestType, responseType, fmt)
            : ImmutableArray<DecoratorInfo>.Empty;

        var cacheable = ParseCacheable(classSymbol, requestType, responseType, compilation, fmt);
        var cacheInvalidation = ParseCacheInvalidation(classSymbol, compilation);
        var resiliencePolicyName = ParseResilient(classSymbol);
        var diagnostics = ValidateCacheMetadata(classSymbol, cacheable, cacheInvalidation);

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
            diagnostics);
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
