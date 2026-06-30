using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Elarion.Generators;

/// <summary>
/// Shared discovery of <c>[RequirePermission(resource, verb)]</c>/<c>[RequireRole("…")]</c> off a handler, used by
/// both <see cref="ElarionManifestGenerator"/> (which records them in the assembly manifest for cross-assembly
/// aggregation) and <see cref="PermissionCatalogGenerator"/> (which emits the runtime catalog contributions and
/// the compile-time <c>ElarionPermissions</c> static). Keeping it in one place means the two generators can never
/// drift on how a requirement is read or how the permission string is composed.
/// </summary>
internal static class PermissionDiscovery
{
    public const string RequirePermissionAttributeMetadataName =
        "Elarion.Abstractions.Authorization.RequirePermissionAttribute";
    public const string RequireRoleAttributeMetadataName =
        "Elarion.Abstractions.Authorization.RequireRoleAttribute";

    // Must match RequirePermissionAttribute.Separator — the resource/verb join in the composed permission string.
    public const string Separator = ".";

    /// <summary>A permission as <c>(resource, verb)</c> plus the composed <c>{resource}.{verb}</c> string.</summary>
    public sealed record PermissionValue(string Resource, string Verb, string Permission);

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
            if (attribute.ConstructorArguments.Length >= 2 &&
                attribute.ConstructorArguments[0].Value is string resource && !string.IsNullOrWhiteSpace(resource) &&
                attribute.ConstructorArguments[1].Value is string verb && !string.IsNullOrWhiteSpace(verb))
            {
                values.Add(new PermissionValue(resource, verb, resource + Separator + verb));
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

    private static string Namespace(INamedTypeSymbol type) =>
        type.ContainingNamespace is { IsGlobalNamespace: false } containing ? containing.ToDisplayString() : "";
}
