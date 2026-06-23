using Microsoft.CodeAnalysis;

namespace Elarion.Generators;

/// <summary>
/// Detects a decorator's optional <c>public static bool AppliesTo(System.Type request)</c> predicate. When
/// present, the generated factory <em>calls</em> it once at pipeline-build time to decide whether the decorator
/// attaches to a handler with a given request type (see
/// <see href="../../docs/decisions/0003-decorator-attachment-predicates.md">ADR-0003</see>).
/// </summary>
/// <remarks>
/// The predicate is plain, runnable C# evaluated at run time, so it may use any logic (including reflection over
/// the request type's attributes) and works for decorators in referenced assemblies. It must be <c>public</c> so
/// the generated registration — emitted into the consuming assembly — can call it.
/// </remarks>
internal static class DecoratorPredicate
{
    public enum Result
    {
        /// <summary>No <c>AppliesTo</c> predicate; attach unconditionally.</summary>
        None,

        /// <summary>A usable <c>public static bool AppliesTo(System.Type)</c> predicate exists; attach conditionally.</summary>
        Conditional,

        /// <summary>An <c>AppliesTo</c> method exists but is not <c>public</c>, so the generated code cannot call it.</summary>
        NotPublic,
    }

    private const string AppliesToMethodName = "AppliesTo";
    private const string SystemTypeMetadataName = "System.Type";

    public static Result Detect(INamedTypeSymbol decoratorDefinition, Compilation compilation, out Location? location)
    {
        location = null;

        var systemType = compilation.GetTypeByMetadataName(SystemTypeMetadataName);
        if (systemType is null)
            return Result.None;

        foreach (var member in decoratorDefinition.GetMembers(AppliesToMethodName))
        {
            if (member is not IMethodSymbol
                {
                    IsStatic: true,
                    ReturnType.SpecialType: SpecialType.System_Boolean,
                    Parameters.Length: 1,
                } method ||
                !SymbolEqualityComparer.Default.Equals(method.Parameters[0].Type, systemType))
            {
                continue;
            }

            location = method.Locations.FirstOrDefault();
            return method.DeclaredAccessibility == Accessibility.Public ? Result.Conditional : Result.NotPublic;
        }

        return Result.None;
    }
}
