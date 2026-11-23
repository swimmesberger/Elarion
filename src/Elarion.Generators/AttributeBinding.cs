using Microsoft.CodeAnalysis;

namespace Elarion.Generators;

/// <summary>
/// Whether an attribute's arguments bound in the compilation a generator sees. A generator never sees what another
/// generator emits into the same compilation, so an argument naming such a constant or type
/// (<c>ElarionPermissions.Payments.Read</c>, <c>ElarionFeatureFlags.Names.Beta</c>) does not bind for it, although the
/// final compilation compiles. Roslyn then leaves the constructor unresolved and the constructor arguments empty, marks
/// a named argument as an error, or gives a <c>typeof</c> an error type. A generator that reads such an attribute must
/// not take the missing value for an absent one: that would drop the check the attribute declares.
/// </summary>
internal static class AttributeBinding {
    /// <summary>True when an argument of <paramref name="attribute"/> did not bind.</summary>
    public static bool HasUnboundArgument(AttributeData attribute) {
        return attribute.AttributeConstructor is null
               || attribute.ConstructorArguments.Any(IsUnbound)
               || attribute.NamedArguments.Any(static named => IsUnbound(named.Value));
    }

    private static bool IsUnbound(TypedConstant constant) {
        return constant.Kind switch {
            TypedConstantKind.Error => true,
            TypedConstantKind.Type => constant.Value is ITypeSymbol { TypeKind: TypeKind.Error },
            TypedConstantKind.Array => !constant.IsNull && constant.Values.Any(IsUnbound),
            _ => false
        };
    }
}
