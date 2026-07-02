using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Elarion.Generators;

/// <summary>
/// Shared stage-one handler-candidate discovery (ADR-0006): identifies a concrete, non-generic
/// <c>IHandler&lt;TRequest, TResponse&gt;</c> implementation per class node and carries only its CLR metadata
/// name, so the per-tree stage stays cached per syntax tree and a later compilation-combined stage re-resolves
/// the symbol fresh. Used by <see cref="HandlerRegistrationGenerator"/> and
/// <see cref="ValidationResolverGenerator"/> so the two can never disagree about what counts as a handler.
/// </summary>
internal static class HandlerCandidates
{
    /// <summary>
    /// Identifies a concrete handler class and returns its CLR metadata name, or <see langword="null"/> for
    /// every other class. Only the metadata name is carried — the caller's stage two re-resolves the symbol
    /// from the current compilation, so no attribute or pipeline state is read against a potentially stale
    /// tree here.
    /// </summary>
    public static string? Identify(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        var classDecl = (ClassDeclarationSyntax)ctx.Node;
        if (ctx.SemanticModel.GetDeclaredSymbol(classDecl, ct) is not INamedTypeSymbol classSymbol)
            return null;

        if (classSymbol.IsAbstract)
            return null;

        // An open-generic handler (e.g. a generic test double `Handler<TRequest, TResponse> : IHandler<TRequest,
        // TResponse>`) cannot be registered as a concrete service — emitting its registration would reference the
        // type's own type parameters as if they were concrete types and fail to compile. Skip it here, the sibling
        // of the IsAbstract guard. This matters because the bundled analyzer flows transitively into consumer
        // projects, so a downstream test assembly's generic handler helper would otherwise break its own build.
        if (classSymbol.IsGenericType)
            return null;

        if (HandlerShape.FindHandlerInterface(classSymbol) is null)
            return null;

        if (classSymbol.ContainingNamespace?.ToDisplayString().Contains("Decorators") == true)
            return null;

        return ModuleScanner.BuildMetadataName(classSymbol);
    }

    /// <summary>
    /// Normalizes collected candidates into a sorted, distinct, value-equatable array. Distinct also collapses
    /// a partial handler class (one candidate per declaration node) into a single resolution, so a partial
    /// handler is never processed twice.
    /// </summary>
    public static EquatableArray<string> FlattenSortedDistinct(ImmutableArray<string?> candidates)
    {
        if (candidates.IsDefaultOrEmpty)
            return EquatableArray<string>.Empty;

        return candidates
            .Where(static candidate => candidate is not null)
            .Select(static candidate => candidate!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToImmutableArray();
    }
}
