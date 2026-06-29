using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Elarion.Generators;

/// <summary>
/// Shared discovery of <c>[RequirePermission]</c>/<c>[RequireRole]</c> strings (and the permission's
/// <c>PermissionKind</c>) off a handler, used by both <see cref="ElarionManifestGenerator"/> (which records them
/// in the assembly manifest for cross-assembly aggregation) and <see cref="PermissionCatalogGenerator"/> (which
/// emits the runtime catalog contributions and the compile-time <c>ElarionPermissions</c> static). Keeping it in
/// one place means the two generators can never drift on how a requirement is read.
/// </summary>
internal static class PermissionDiscovery
{
    public const string RequirePermissionAttributeMetadataName =
        "Elarion.Abstractions.Authorization.RequirePermissionAttribute";
    public const string RequireRoleAttributeMetadataName =
        "Elarion.Abstractions.Authorization.RequireRoleAttribute";

    private const string UnspecifiedKind = "Unspecified";

    /// <summary>A permission string with its declared <c>PermissionKind</c> member name (e.g. "Read").</summary>
    public sealed record PermissionValue(string Value, string Kind);

    /// <summary>Every <c>[RequirePermission]</c> on one handler.</summary>
    public sealed record PermissionGuard(
        string HandlerFqn,
        string Namespace,
        EquatableArray<PermissionValue> Values,
        LocationInfo Location);

    /// <summary>Every <c>[RequireRole]</c> on one handler.</summary>
    public sealed record RoleGuard(
        string HandlerFqn,
        string Namespace,
        EquatableArray<string> Values,
        LocationInfo Location);

    public static PermissionGuard? ReadPermissions(GeneratorAttributeSyntaxContext ctx)
    {
        if (ctx.TargetSymbol is not INamedTypeSymbol type)
            return null;

        var values = ImmutableArray.CreateBuilder<PermissionValue>();
        foreach (var attribute in ctx.Attributes)
        {
            if (attribute.ConstructorArguments.Length > 0 &&
                attribute.ConstructorArguments[0].Value is string value &&
                !string.IsNullOrWhiteSpace(value))
            {
                values.Add(new PermissionValue(value, ResolveKind(attribute)));
            }
        }

        if (values.Count == 0)
            return null;

        return new PermissionGuard(
            type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            Namespace(type),
            values.ToImmutable().ToEquatableArray(),
            LocationInfo.From(type));
    }

    public static RoleGuard? ReadRoles(GeneratorAttributeSyntaxContext ctx)
    {
        if (ctx.TargetSymbol is not INamedTypeSymbol type)
            return null;

        var values = ImmutableArray.CreateBuilder<string>();
        foreach (var attribute in ctx.Attributes)
        {
            if (attribute.ConstructorArguments.Length > 0 &&
                attribute.ConstructorArguments[0].Value is string value &&
                !string.IsNullOrWhiteSpace(value))
            {
                values.Add(value);
            }
        }

        if (values.Count == 0)
            return null;

        return new RoleGuard(
            type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            Namespace(type),
            values.ToImmutable().ToEquatableArray(),
            LocationInfo.From(type));
    }

    // The PermissionKind member name from the attribute's optional second argument; "Unspecified" when omitted.
    private static string ResolveKind(AttributeData attribute)
    {
        if (attribute.ConstructorArguments.Length < 2)
            return UnspecifiedKind;

        var argument = attribute.ConstructorArguments[1];
        if (argument.Kind == TypedConstantKind.Error || argument.Value is null)
            return UnspecifiedKind;

        if (argument.Type is INamedTypeSymbol enumType)
        {
            foreach (var member in enumType.GetMembers())
            {
                if (member is IFieldSymbol { HasConstantValue: true } field && Equals(field.ConstantValue, argument.Value))
                    return field.Name;
            }
        }

        return UnspecifiedKind;
    }

    private static string Namespace(INamedTypeSymbol type) =>
        type.ContainingNamespace is { IsGlobalNamespace: false } containing ? containing.ToDisplayString() : "";
}
