using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Elarion.Generators;

/// <summary>
/// Shared discovery of <c>[SettingDefinitions]</c> containers and their <c>[Setting]</c> properties for the
/// settings-definition generator and the manifest generator (ADR-0078): one pure transform that returns the
/// value-equatable container model plus its diagnostics, so the emitted implementation, the cross-assembly
/// manifest entry, and the diagnostics cannot drift.
/// </summary>
internal static class SettingDefinitionDiscovery {
    public const string DefinitionsAttributeMetadataName = "Elarion.Abstractions.Settings.SettingDefinitionsAttribute";
    private const string SettingAttributeMetadataName = "Elarion.Abstractions.Settings.SettingAttribute";
    private const string DefinitionTypeDisplayName = "Elarion.Abstractions.Settings.SettingDefinition<T>";

    private static readonly SymbolDisplayFormat TypeFormat = SymbolDisplayFormat.FullyQualifiedFormat
        .AddMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    public static readonly DiagnosticDescriptor InvalidContainer = new(
        "ELSDEF001", "Invalid setting definitions container",
        "'{0}' is marked [SettingDefinitions] but {1}", "Elarion.Abstractions.Settings",
        DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor InvalidSettingMember = new(
        "ELSDEF002", "Invalid setting declaration",
        "Setting property '{0}' {1}", "Elarion.Abstractions.Settings", DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor InvalidKey = new(
        "ELSDEF003", "Invalid setting key",
        "Setting key '{0}' is invalid: a key must be non-blank, without surrounding whitespace, and made of " +
        "non-empty ':'-separated segments", "Elarion.Abstractions.Settings", DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor DuplicateKey = new(
        "ELSDEF004", "Duplicate setting key",
        "Setting key '{0}' is declared more than once (keys are compared case-insensitively because they double as " +
        "IConfiguration keys)", "Elarion.Abstractions.Settings", DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor SecretWithDefault = new(
        "ELSDEF005", "Secret setting declares a default",
        "Secret setting '{0}' must not declare a default: a default would be a plaintext secret in source",
        "Elarion.Abstractions.Settings", DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor PinnableNeedsGlobalScope = new(
        "ELSDEF006", "Pinnable setting must allow the global scope",
        "Setting '{0}' is Pinnable but does not allow the 'global' scope; deployment configuration is app-wide",
        "Elarion.Abstractions.Settings", DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor InvalidDefault = new(
        "ELSDEF007", "Invalid setting default",
        "Setting '{0}' default is invalid: {1}", "Elarion.Abstractions.Settings", DiagnosticSeverity.Error, true);

    public sealed record Setting(
        string MemberName,
        string Accessibility,
        string DefinitionTypeFqn,
        string Key,
        EquatableArray<string> Scopes,
        bool IsSecret,
        bool IsPinnable,
        string? Description,
        string? DefaultExpression,
        string? DefaultFactory,
        LocationInfo Location);

    public sealed record Container(
        string Fqn,
        string Namespace,
        string Name,
        bool IsPublic,
        EquatableArray<Setting> Settings,
        EquatableArray<DiagnosticInfo> Diagnostics);

    public static Container? CreateContainer(GeneratorAttributeSyntaxContext ctx) {
        if (ctx.TargetSymbol is not INamedTypeSymbol type) return null;

        var diagnostics = ImmutableArray.CreateBuilder<DiagnosticInfo>();
        var fqn = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var ns = type.ContainingNamespace is { IsGlobalNamespace: false } containing
            ? containing.ToDisplayString()
            : string.Empty;
        var isPublic = type.DeclaredAccessibility == Accessibility.Public;
        var settings = ImmutableArray.CreateBuilder<Setting>();

        var allPartial = type.DeclaringSyntaxReferences.All(reference =>
            reference.GetSyntax() is ClassDeclarationSyntax cls &&
            cls.Modifiers.Any(static m => m.ValueText == "partial"));

        string? problem = null;
        if (type.ContainingType is not null) problem = "must be a top-level class";
        else if (type.TypeKind != TypeKind.Class || !type.IsStatic || !allPartial) problem = "must be a static partial class";
        else if (type.IsGenericType) problem = "must not be generic";

        if (problem is not null) {
            diagnostics.Add(DiagnosticInfo.Create(InvalidContainer, LocationInfo.From(type), type.Name, problem));
            return new Container(fqn, ns, type.Name, isPublic, EquatableArray<Setting>.Empty,
                diagnostics.ToImmutable().ToEquatableArray());
        }

        foreach (var member in type.GetMembers().OfType<IPropertySymbol>()) {
            var attribute = member.GetAttributes().FirstOrDefault(static a =>
                a.AttributeClass?.ToDisplayString() == SettingAttributeMetadataName);
            if (attribute is null) continue;

            var setting = CreateSetting(type, member, attribute, diagnostics);
            if (setting is not null) settings.Add(setting);
        }

        return new Container(fqn, ns, type.Name, isPublic,
            settings.OrderBy(static s => s.MemberName, StringComparer.Ordinal).ToImmutableArray().ToEquatableArray(),
            diagnostics.ToImmutable().ToEquatableArray());
    }

    private static Setting? CreateSetting(
        INamedTypeSymbol container,
        IPropertySymbol property,
        AttributeData attribute,
        ImmutableArray<DiagnosticInfo>.Builder diagnostics) {
        var location = LocationInfo.From(property);
        var key = attribute.ConstructorArguments.Length > 0
            ? attribute.ConstructorArguments[0].Value as string ?? string.Empty
            : string.Empty;

        if (!property.IsStatic || !property.IsPartialDefinition || property.SetMethod is not null) {
            diagnostics.Add(DiagnosticInfo.Create(InvalidSettingMember, location, property.Name,
                "must be a 'static partial' get-only property"));
            return null;
        }

        if (property.Type is not INamedTypeSymbol { IsGenericType: true } definitionType ||
            definitionType.OriginalDefinition.ToDisplayString() != DefinitionTypeDisplayName) {
            diagnostics.Add(DiagnosticInfo.Create(InvalidSettingMember, location, property.Name,
                "must have the type SettingDefinition<T>"));
            return null;
        }

        var valueType = definitionType.TypeArguments[0];

        if (!IsValidKey(key)) {
            diagnostics.Add(DiagnosticInfo.Create(InvalidKey, location, key));
            return null;
        }

        var scopes = new List<string> { "global" };
        var isSecret = false;
        var isPinnable = false;
        string? description = null;
        TypedConstant? defaultConstant = null;
        string? defaultFactory = null;
        foreach (var named in attribute.NamedArguments)
            switch (named.Key) {
                case "Scopes":
                    scopes = named.Value.IsNull
                        ? scopes
                        : named.Value.Values.Select(static v => v.Value as string ?? string.Empty).ToList();
                    break;
                case "Secret":
                    isSecret = named.Value.Value is true;
                    break;
                case "Pinnable":
                    isPinnable = named.Value.Value is true;
                    break;
                case "Description":
                    description = named.Value.Value as string;
                    break;
                case "Default":
                    defaultConstant = named.Value;
                    break;
                case "DefaultFactory":
                    defaultFactory = named.Value.Value as string;
                    break;
            }

        if (scopes.Count == 0 || scopes.Any(string.IsNullOrWhiteSpace)) {
            diagnostics.Add(DiagnosticInfo.Create(InvalidSettingMember, location, property.Name,
                "declares an empty scope kind"));
            return null;
        }

        scopes = scopes.Distinct(StringComparer.Ordinal).OrderBy(static s => s, StringComparer.Ordinal).ToList();
        if (isPinnable && !scopes.Contains("global")) {
            diagnostics.Add(DiagnosticInfo.Create(PinnableNeedsGlobalScope, location, key));
            return null;
        }

        var hasDefault = defaultConstant is not null || defaultFactory is not null;
        if (isSecret && hasDefault) {
            diagnostics.Add(DiagnosticInfo.Create(SecretWithDefault, location, key));
            return null;
        }

        string? defaultExpression = null;
        if (defaultConstant is not null && defaultFactory is not null) {
            diagnostics.Add(DiagnosticInfo.Create(InvalidDefault, location, key,
                "Default and DefaultFactory are mutually exclusive"));
            return null;
        }

        if (defaultConstant is { } constant) {
            if (!TryRenderConstantDefault(constant, valueType, out defaultExpression, out var reason)) {
                diagnostics.Add(DiagnosticInfo.Create(InvalidDefault, location, key, reason));
                return null;
            }
        }

        if (defaultFactory is not null) {
            var factory = container.GetMembers(defaultFactory).OfType<IMethodSymbol>().FirstOrDefault(m =>
                m.IsStatic && m.Parameters.IsEmpty && m.TypeParameters.IsEmpty &&
                SymbolEqualityComparer.Default.Equals(m.ReturnType, valueType));
            if (factory is null) {
                diagnostics.Add(DiagnosticInfo.Create(InvalidDefault, location, key,
                    $"DefaultFactory '{defaultFactory}' must name a static parameterless method of '{container.Name}' " +
                    $"returning '{valueType.ToDisplayString()}'"));
                return null;
            }
        }

        return new Setting(
            property.Name,
            AccessibilityKeyword(property.DeclaredAccessibility),
            definitionType.ToDisplayString(TypeFormat),
            key,
            scopes.ToImmutableArray().ToEquatableArray(),
            isSecret,
            isPinnable,
            description,
            defaultExpression is null ? null : $"({valueType.ToDisplayString(TypeFormat)})({defaultExpression})",
            defaultFactory,
            location);
    }

    public static bool IsValidKey(string key) {
        if (string.IsNullOrWhiteSpace(key) || key.Trim() != key) return false;

        foreach (var segment in key.Split(':'))
            if (segment.Length == 0 || segment.Trim() != segment)
                return false;

        return true;
    }

    private static bool TryRenderConstantDefault(TypedConstant constant, ITypeSymbol valueType, out string rendered,
        out string reason) {
        rendered = string.Empty;
        reason = string.Empty;
        var target = valueType is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } n
            ? n.TypeArguments[0]
            : valueType;

        bool compatible;
        if (constant.IsNull) {
            compatible = valueType.IsReferenceType || !ReferenceEquals(target, valueType);
        }
        else if (target.TypeKind == TypeKind.Enum) {
            compatible = constant.Kind == TypedConstantKind.Enum
                ? SymbolEqualityComparer.Default.Equals(constant.Type, target)
                : constant.Value is int or long or short or byte or sbyte or ushort or uint or ulong;
        }
        else {
            var value = constant.Value;
            compatible = target.SpecialType switch {
                SpecialType.System_String => value is string,
                SpecialType.System_Boolean => value is bool,
                SpecialType.System_Char => value is char,
                SpecialType.System_Single or SpecialType.System_Double or SpecialType.System_Decimal =>
                    value is sbyte or byte or short or ushort or int or uint or long or ulong or float or double,
                SpecialType.System_SByte or SpecialType.System_Byte or SpecialType.System_Int16
                    or SpecialType.System_UInt16 or SpecialType.System_Int32 or SpecialType.System_UInt32
                    or SpecialType.System_Int64 or SpecialType.System_UInt64 =>
                    value is sbyte or byte or short or ushort or int or uint or long or ulong,
                _ => false
            };
        }

        if (!compatible) {
            reason = $"the constant does not fit '{valueType.ToDisplayString()}'; use DefaultFactory for non-scalar defaults";
            return false;
        }

        if (!AttributeArgumentRendering.TryRenderTypedConstant(constant, null, TypeFormat, false, out rendered)) {
            reason = "the constant cannot be rendered";
            return false;
        }

        return true;
    }

    private static string AccessibilityKeyword(Accessibility accessibility) {
        return accessibility switch {
            Accessibility.Public => "public",
            Accessibility.Internal => "internal",
            Accessibility.Protected => "protected",
            Accessibility.ProtectedOrInternal => "protected internal",
            Accessibility.ProtectedAndInternal => "private protected",
            _ => "private"
        };
    }
}
