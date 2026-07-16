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
        => Detect(decoratorDefinition, compilation, HandlerMetadataMetadataName, out location);

    /// <summary>Detects a predicate for the supplied metadata contract.</summary>
    public static Result Detect(INamedTypeSymbol decoratorDefinition, Compilation compilation, string metadataName, out Location? location)
    {
        location = null;

        var handlerMetadata = compilation.GetTypeByMetadataName(metadataName);
        if (handlerMetadata is null)
            return Result.None;

        var members = decoratorDefinition.GetMembers(AppliesToMethodName);
        if (members.Length == 0)
            return Result.None;

        // A member named AppliesTo is an intended predicate. Silently ignoring a malformed overload would
        // attach the decorator unconditionally, which is a fail-open pipeline configuration error.
        foreach (var member in members)
        {
            location = member.Locations.FirstOrDefault();
            if (member is not IMethodSymbol method)
                return Result.UnsupportedSignature;

            if (method.DeclaredAccessibility != Accessibility.Public)
                return Result.NotPublic;

            if (!method.IsStatic || method.ReturnType.SpecialType != SpecialType.System_Boolean ||
                method.Parameters.Length != 1 ||
                !SymbolEqualityComparer.Default.Equals(method.Parameters[0].Type, handlerMetadata))
                return Result.UnsupportedSignature;
        }

        location = members[0].Locations.FirstOrDefault();
        return Result.Conditional;
    }
}
