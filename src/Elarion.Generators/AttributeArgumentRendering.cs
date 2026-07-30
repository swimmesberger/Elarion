using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Elarion.Generators;

/// <summary>
/// Renders compile-time <see cref="AttributeData"/> arguments back into C# source text — the literal
/// round-tripping shared by <see cref="ValidatableTypeWalker"/> (constant-construction expressions for the
/// generated validation resolvers) and <see cref="HttpEndpointEmission"/> (attribute applications copied onto
/// the OpenAPI shape parameters). Attribute arguments are constants by construction, so a render failure is a
/// guard for unrepresentable values, not a policy decision.
/// </summary>
internal static class AttributeArgumentRendering {
    private const string ValidationAttributeDisplay = "System.ComponentModel.DataAnnotations.ValidationAttribute";

    public static bool DerivesFromValidationAttribute(INamedTypeSymbol attributeClass) {
        for (var current = attributeClass.BaseType; current is not null; current = current.BaseType)
            if (current.ToDisplayString() == ValidationAttributeDisplay)
                return true;

        return false;
    }

    /// <summary>
    /// Renders one attribute argument as source text. <paramref name="attributeArgument"/> selects the
    /// attribute-application dialect: an empty array renders as an array-creation expression (a
    /// <c>global::System.Array.Empty&lt;T&gt;()</c> call is not a legal attribute argument), while the
    /// construction dialect keeps the allocation-free call. <paramref name="currentAssembly"/> is the assembly
    /// the rendered text compiles in; pass <see langword="null"/> when the text may travel to any referencing
    /// assembly (via the Elarion manifest), which restricts every referenced type to fully-public.
    /// </summary>
    public static bool TryRenderTypedConstant(
        TypedConstant constant, IAssemblySymbol? currentAssembly, SymbolDisplayFormat fmt, bool attributeArgument,
        out string rendered) {
        rendered = string.Empty;
        if (constant.IsNull) {
            // Cast the null so overloaded attribute constructors stay unambiguous.
            rendered = constant.Type is { TypeKind: not TypeKind.Error } type &&
                       IsAccessibleFromGeneratedCode(type, currentAssembly)
                ? $"({type.ToDisplayString(fmt)})null"
                : "null";
            return true;
        }

        switch (constant.Kind) {
            case TypedConstantKind.Primitive:
                return TryRenderPrimitive(constant.Value!, out rendered);

            case TypedConstantKind.Enum:
                if (constant.Type is not { } enumType ||
                    !IsAccessibleFromGeneratedCode(enumType, currentAssembly) ||
                    !TryRenderPrimitive(constant.Value!, out var underlying))
                    return false;

                // Cast the underlying constant back to the enum type — exact for undeclared/flags combinations.
                rendered = $"({enumType.ToDisplayString(fmt)})({underlying})";
                return true;

            case TypedConstantKind.Type:
                if (constant.Value is not ITypeSymbol typeValue ||
                    typeValue.TypeKind == TypeKind.Error ||
                    !IsAccessibleFromGeneratedCode(typeValue, currentAssembly))
                    return false;

                rendered = $"typeof({typeValue.ToDisplayString(fmt)})";
                return true;

            case TypedConstantKind.Array:
                if (constant.Type is not IArrayTypeSymbol arrayType ||
                    !IsAccessibleFromGeneratedCode(arrayType.ElementType, currentAssembly))
                    return false;

                var elements = new List<string>(constant.Values.Length);
                foreach (var value in constant.Values) {
                    if (!TryRenderTypedConstant(value, currentAssembly, fmt, attributeArgument, out var element))
                        return false;

                    elements.Add(element);
                }

                var elementTypeFqn = arrayType.ElementType.ToDisplayString(fmt);
                rendered = elements.Count == 0
                    ? attributeArgument
                        ? $"new {elementTypeFqn}[] {{ }}"
                        : $"global::System.Array.Empty<{elementTypeFqn}>()"
                    : $"new {elementTypeFqn}[] {{ {string.Join(", ", elements)} }}";
                return true;

            default:
                return false;
        }
    }

    public static bool TryRenderPrimitive(object value, out string rendered) {
        switch (value) {
            case string s:
                rendered = SymbolDisplay.FormatLiteral(s, true);
                return true;
            case char c:
                rendered = SymbolDisplay.FormatLiteral(c, true);
                return true;
            case bool b:
                rendered = b ? "true" : "false";
                return true;
            case int i:
                // int.MinValue has no plain literal form (the '-' is an operator applied to 2147483648).
                rendered = i == int.MinValue ? "int.MinValue" : i.ToString(CultureInfo.InvariantCulture);
                return true;
            case long l:
                rendered = l == long.MinValue ? "long.MinValue" : l.ToString(CultureInfo.InvariantCulture) + "L";
                return true;
            case uint ui:
                rendered = ui.ToString(CultureInfo.InvariantCulture) + "U";
                return true;
            case ulong ul:
                rendered = ul.ToString(CultureInfo.InvariantCulture) + "UL";
                return true;
            case short sh:
                rendered = "(short)" + sh.ToString(CultureInfo.InvariantCulture);
                return true;
            case ushort us:
                rendered = "(ushort)" + us.ToString(CultureInfo.InvariantCulture);
                return true;
            case byte by:
                rendered = "(byte)" + by.ToString(CultureInfo.InvariantCulture);
                return true;
            case sbyte sb:
                rendered = "(sbyte)" + sb.ToString(CultureInfo.InvariantCulture);
                return true;
            case double d:
                rendered = double.IsNaN(d) ? "double.NaN"
                    : double.IsPositiveInfinity(d) ? "double.PositiveInfinity"
                    : double.IsNegativeInfinity(d) ? "double.NegativeInfinity"
                    : d.ToString("G17", CultureInfo.InvariantCulture) + "D";
                return true;
            case float f:
                rendered = float.IsNaN(f) ? "float.NaN"
                    : float.IsPositiveInfinity(f) ? "float.PositiveInfinity"
                    : float.IsNegativeInfinity(f) ? "float.NegativeInfinity"
                    : f.ToString("G9", CultureInfo.InvariantCulture) + "F";
                return true;
            default:
                rendered = string.Empty;
                return false;
        }
    }

    // Whether generated code may reference the type in typeof()/new/attribute expressions: public all the way
    // out, or — when currentAssembly is provided — internal within that assembly (or one granting it access via
    // IVT). A null currentAssembly means the rendered text may be compiled into any referencing assembly, so
    // only fully-public types qualify.
    public static bool IsAccessibleFromGeneratedCode(ITypeSymbol type, IAssemblySymbol? currentAssembly) {
        if (type is IArrayTypeSymbol array)
            return IsAccessibleFromGeneratedCode(array.ElementType, currentAssembly);

        if (type is ITypeParameterSymbol)
            return false;

        if (type is not INamedTypeSymbol named)
            return type.TypeKind == TypeKind.Dynamic;

        foreach (var argument in named.TypeArguments)
            if (!IsAccessibleFromGeneratedCode(argument, currentAssembly))
                return false;

        for (var current = named; current is not null; current = current.ContainingType)
            switch (current.DeclaredAccessibility) {
                case Accessibility.Public:
                case Accessibility.NotApplicable:
                    continue;
                case Accessibility.Internal:
                case Accessibility.ProtectedOrInternal:
                    if (currentAssembly is not null &&
                        (SymbolEqualityComparer.Default.Equals(current.ContainingAssembly, currentAssembly) ||
                         current.ContainingAssembly.GivesAccessTo(currentAssembly)))
                        continue;

                    return false;
                default:
                    return false;
            }

        return true;
    }
}
