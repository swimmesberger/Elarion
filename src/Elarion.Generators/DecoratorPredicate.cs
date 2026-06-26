using Microsoft.CodeAnalysis;

namespace Elarion.Generators;

/// <summary>
/// Detects a decorator's optional <c>public static bool AppliesTo(HandlerMetadata handler)</c> attachment
/// predicate. When present, the generated factory <em>calls</em> it once at pipeline-build time to decide whether
/// the decorator attaches to a handler (see
/// <see href="../../docs/decisions/0003-decorator-attachment-predicates.md">ADR-0003</see>).
/// </summary>
/// <remarks>
/// The predicate receives <c>HandlerMetadata</c> — the concrete handler type, its request/response types, and its
/// attributes — so a custom decorator has the same handler-attribute-driven attachment the framework's built-in
/// decorators use. It is plain, runnable C# evaluated at run time, so it may use any logic (including reflection)
/// and works for decorators in referenced assemblies. It must be <c>public</c> so the generated registration —
/// emitted into the consuming assembly — can call it. There is a single supported signature on purpose: a
/// differently-shaped <c>AppliesTo</c> (e.g. the older <c>AppliesTo(System.Type)</c>) is reported rather than
/// silently ignored, which would attach the decorator unconditionally.
/// </remarks>
internal static class DecoratorPredicate
{
    public enum Result
    {
        /// <summary>No <c>AppliesTo</c> predicate; attach unconditionally.</summary>
        None,

        /// <summary>A usable <c>public static bool AppliesTo(HandlerMetadata)</c> predicate exists; attach conditionally.</summary>
        Conditional,

        /// <summary>An <c>AppliesTo(HandlerMetadata)</c> method exists but is not <c>public</c>, so the generated code cannot call it.</summary>
        NotPublic,

        /// <summary>A <c>static bool AppliesTo(…)</c> method exists with a parameter other than <c>HandlerMetadata</c>.</summary>
        UnsupportedSignature,
    }

    private const string AppliesToMethodName = "AppliesTo";
    private const string HandlerMetadataMetadataName = "Elarion.Abstractions.Pipeline.HandlerMetadata";

    public static Result Detect(INamedTypeSymbol decoratorDefinition, Compilation compilation, out Location? location)
    {
        location = null;

        var handlerMetadata = compilation.GetTypeByMetadataName(HandlerMetadataMetadataName);
        if (handlerMetadata is null)
            return Result.None;

        foreach (var member in decoratorDefinition.GetMembers(AppliesToMethodName))
        {
            if (member is not IMethodSymbol
                {
                    IsStatic: true,
                    ReturnType.SpecialType: SpecialType.System_Boolean,
                    Parameters.Length: 1,
                } method)
            {
                continue;
            }

            location = method.Locations.FirstOrDefault();

            if (!SymbolEqualityComparer.Default.Equals(method.Parameters[0].Type, handlerMetadata))
                return Result.UnsupportedSignature;

            return method.DeclaredAccessibility == Accessibility.Public ? Result.Conditional : Result.NotPublic;
        }

        return Result.None;
    }
}
