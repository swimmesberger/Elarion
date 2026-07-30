using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Elarion.Generators;

/// <summary>
/// Request-binding analysis and emission for <c>[HttpEndpoint]</c> handlers (ADR-0071). The generator owns
/// binding: it classifies every bindable request member at compile time (route token, query, header, form,
/// file, or JSON-body) and emits a concrete <c>RequestDelegate</c> that binds through
/// <c>Elarion.AspNetCore.ElarionHttpEndpointBinder</c> — the AOT-safe registration path that never touches
/// ASP.NET Core's reflection-based <c>RequestDelegateFactory</c>. A parallel, never-invoked "API shape" method
/// with the classic typed signature is attached as <c>MethodInfo</c> metadata so ApiExplorer/OpenAPI keep
/// describing parameters and request bodies exactly as before.
/// </summary>
internal static partial class HttpEndpointEmission {
    private const string ParsableMetadataName = "System.IParsable`1";

    public static readonly DiagnosticDescriptor UnbindableMember = new(
        "ELHTTP005",
        "HTTP endpoint request member cannot be bound",
        "Handler '{0}': request member '{1}' of type '{2}' cannot be bound from the {3}; supported shapes are "
        + "string, enum, and IParsable<T> value types (plus their nullable forms), arrays of those from the "
        + "query string, IFormFile/IFormFileCollection, and one [FromBody] member — no endpoint will be generated",
        "Elarion.Http",
        DiagnosticSeverity.Warning,
        true);

    /// <summary>Where a request member's value comes from on the wire.</summary>
    public enum BindingSource {
        Route,
        Query,
        Header,
        Form,
        FormFile,
        FormFiles,
        Body
    }

    /// <summary>How a request member's raw value is parsed into its declared type.</summary>
    public enum BindingParse {
        String,
        Parsable,
        Enum,
        StringArray,
        ParsableArray,
        EnumArray,
        File,
        Files,
        Body
    }

    /// <summary>
    /// One bindable request member: a primary-constructor parameter or a settable/init property. Members appear
    /// in binding order — constructor parameters first (in signature order), then remaining properties in
    /// declaration order — and the emitted construction relies on that order.
    /// <see cref="ValidationAttributes"/> carries the member's constant-argument DataAnnotations attributes as
    /// attribute-application text (e.g. <c>global::…RangeAttribute(1, 120)</c>), re-applied to the OpenAPI shape
    /// parameter so route/query/header/form constraints keep flowing into the served document (JSON-body
    /// constraints ride the schema transformer instead).
    /// </summary>
    public sealed record BindingMember(
        string MemberName,
        string LookupName,
        BindingSource Source,
        BindingParse Parse,
        string ValueTypeFqn,
        bool DeclaredNullable,
        bool IsRequired,
        bool IsCtorParameter,
        bool UseDefaultsFallback,
        string? DefaultLiteral,
        EquatableArray<string> ValidationAttributes
    );

    /// <summary>
    /// Classifies every bindable member of <paramref name="requestType"/> for member-wise binding
    /// (the <c>[AsParameters]</c>-style GET/DELETE and opt-in shapes). Returns <see langword="false"/> and
    /// reports <see cref="UnbindableMember"/> when a member cannot be bound at compile time.
    /// </summary>
    private static bool TryAnalyzeBindingMembers(
        INamedTypeSymbol handlerType,
        INamedTypeSymbol requestType,
        string route,
        SymbolDisplayFormat fmt,
        Action<DiagnosticInfo>? report,
        out EquatableArray<BindingMember> members) {
        members = EquatableArray<BindingMember>.Empty;
        var tokens = RouteTokenNames(route);
        var builder = ImmutableArray.CreateBuilder<BindingMember>();
        var boundNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hasBodyMember = false;

        var ctor = SelectConstructor(requestType);
        if (ctor is not null)
            foreach (var parameter in ctor.Parameters) {
                if (!TryCreateMember(
                        handlerType, requestType, parameter, parameter.Type, parameter.GetAttributes(),
                        MatchingPropertyAttributes(requestType, parameter.Name), tokens,
                        isCtorParameter: true, isRequired: !parameter.HasExplicitDefaultValue,
                        defaultLiteral: DefaultLiteral(parameter, fmt), useDefaultsFallback: false,
                        fmt, report, ref hasBodyMember, out var member))
                    return false;

                builder.Add(member!);
                boundNames.Add(parameter.Name);
            }

        foreach (var property in PublicInstanceProperties(requestType)) {
            if (property.SetMethod is null || boundNames.Contains(property.Name))
                continue;

            var hasInitializer = HasInitializer(property);
            var isRequired = property.IsRequired
                             || (!hasInitializer && !IsNullableType(property.Type));
            if (!TryCreateMember(
                    handlerType, requestType, property, property.Type, property.GetAttributes(),
                    ImmutableArray<AttributeData>.Empty, tokens,
                    isCtorParameter: false, isRequired, defaultLiteral: null,
                    useDefaultsFallback: hasInitializer && !property.IsRequired,
                    fmt, report, ref hasBodyMember, out var member))
                return false;

            builder.Add(member!);
            boundNames.Add(property.Name);
        }

        members = builder.ToImmutable().ToEquatableArray();
        return true;
    }

    private static bool TryCreateMember(
        INamedTypeSymbol handlerType,
        INamedTypeSymbol requestType,
        ISymbol member,
        ITypeSymbol type,
        ImmutableArray<AttributeData> attributes,
        ImmutableArray<AttributeData> mirroredAttributes,
        HashSet<string> routeTokens,
        bool isCtorParameter,
        bool isRequired,
        string? defaultLiteral,
        bool useDefaultsFallback,
        SymbolDisplayFormat fmt,
        Action<DiagnosticInfo>? report,
        ref bool hasBodyMember,
        out BindingMember? bindingMember) {
        bindingMember = null;

        var (attributeSource, nameOverride, unsupportedAttribute) = ReadSourceAttribute(attributes);
        if (unsupportedAttribute) {
            Report(report, handlerType, member, type, "request DTO (service-sourced members are unsupported)", fmt);
            return false;
        }

        var lookupName = nameOverride ?? member.Name;
        var source = attributeSource ?? InferSource(type, lookupName, routeTokens);
        var validationAttributes = RenderShapeValidationAttributes(attributes, mirroredAttributes, fmt);

        if (source == BindingSource.Body) {
            if (hasBodyMember) {
                Report(report, handlerType, member, type, "request body (only one [FromBody] member is supported)",
                    fmt);
                return false;
            }

            hasBodyMember = true;
            bindingMember = new BindingMember(
                member.Name, lookupName, BindingSource.Body, BindingParse.Body,
                type.ToDisplayString(fmt), IsNullableType(type), isRequired, isCtorParameter,
                useDefaultsFallback, defaultLiteral, validationAttributes);
            return true;
        }

        if (!TryClassifyValue(type, source, fmt, out var parse, out var valueTypeFqn, out var declaredNullable)) {
            Report(report, handlerType, member, type, SourceDescription(source), fmt);
            return false;
        }

        // File members always bind from the form regardless of how the source was inferred.
        if (parse == BindingParse.File) source = BindingSource.FormFile;
        else if (parse == BindingParse.Files) source = BindingSource.FormFiles;
        else if (source is BindingSource.FormFile or BindingSource.FormFiles) source = BindingSource.Form;

        bindingMember = new BindingMember(
            member.Name, lookupName, source, parse, valueTypeFqn, declaredNullable, isRequired,
            isCtorParameter, useDefaultsFallback, defaultLiteral, validationAttributes);
        return true;
    }

    // A positional record's `[StringLength(…)] string Name` may land on the constructor parameter or — with a
    // `[property:]` target — on the synthesized property; the shape parameter must see both, mirroring
    // ValidatableTypeWalker's record handling. Binding-source classification deliberately stays on the
    // member's own attributes.
    private static ImmutableArray<AttributeData> MatchingPropertyAttributes(
        INamedTypeSymbol requestType, string parameterName) {
        foreach (var property in PublicInstanceProperties(requestType))
            if (string.Equals(property.Name, parameterName, StringComparison.OrdinalIgnoreCase))
                return property.GetAttributes();

        return ImmutableArray<AttributeData>.Empty;
    }

    /// <summary>AttributeTargets.Parameter — the only target the generated shape parameters can host.</summary>
    private const int ParameterAttributeTarget = 0x0800;

    /// <summary>
    /// Renders the member's DataAnnotations validation attributes as attribute-application text (no enclosing
    /// brackets), for re-application on the OpenAPI shape parameter. Copied attributes must be
    /// parameter-applicable and fully public with constant-renderable arguments — the emitted text travels
    /// through the manifest into any referencing host compilation. Anything else is skipped: the endpoint still
    /// maps, the constraint just does not surface in the document.
    /// </summary>
    private static EquatableArray<string> RenderShapeValidationAttributes(
        ImmutableArray<AttributeData> attributes,
        ImmutableArray<AttributeData> mirroredAttributes,
        SymbolDisplayFormat fmt) {
        var builder = ImmutableArray.CreateBuilder<string>();
        RenderShapeValidationAttributes(attributes, fmt, builder);
        RenderShapeValidationAttributes(mirroredAttributes, fmt, builder);
        return builder.ToImmutable().ToEquatableArray();
    }

    private static void RenderShapeValidationAttributes(
        ImmutableArray<AttributeData> attributes, SymbolDisplayFormat fmt,
        ImmutableArray<string>.Builder builder) {
        foreach (var attribute in attributes) {
            if (attribute.AttributeClass is not { TypeKind: not TypeKind.Error } attributeClass)
                continue;

            if (!AttributeArgumentRendering.DerivesFromValidationAttribute(attributeClass))
                continue;

            if (!AllowsParameterTarget(attributeClass))
                continue;

            if (!AttributeArgumentRendering.IsAccessibleFromGeneratedCode(attributeClass, currentAssembly: null))
                continue;

            if (TryRenderAttributeApplication(attribute, attributeClass, fmt, out var rendered))
                builder.Add(rendered);
        }
    }

    private static bool TryRenderAttributeApplication(
        AttributeData attribute, INamedTypeSymbol attributeClass, SymbolDisplayFormat fmt, out string rendered) {
        rendered = string.Empty;

        var arguments = new List<string>(attribute.ConstructorArguments.Length + attribute.NamedArguments.Length);
        foreach (var constant in attribute.ConstructorArguments) {
            if (!AttributeArgumentRendering.TryRenderTypedConstant(
                    constant, currentAssembly: null, fmt, attributeArgument: true, out var value))
                return false;

            arguments.Add(value);
        }

        foreach (var named in attribute.NamedArguments) {
            if (!AttributeArgumentRendering.TryRenderTypedConstant(
                    named.Value, currentAssembly: null, fmt, attributeArgument: true, out var value))
                return false;

            arguments.Add($"{named.Key} = {value}");
        }

        var name = attributeClass.ToDisplayString(fmt);
        rendered = arguments.Count == 0 ? name : $"{name}({string.Join(", ", arguments)})";
        return true;
    }

    // AttributeUsage is inherited: the nearest declaration up the base chain wins; no declaration anywhere
    // means AttributeTargets.All.
    private static bool AllowsParameterTarget(INamedTypeSymbol attributeClass) {
        for (var current = attributeClass; current is not null; current = current.BaseType)
            foreach (var attr in current.GetAttributes())
                if (attr.AttributeClass?.ToDisplayString() == "System.AttributeUsageAttribute")
                    return attr.ConstructorArguments.Length > 0
                           && attr.ConstructorArguments[0].Value is int targets
                           && (targets & ParameterAttributeTarget) != 0;

        return true;
    }

    private static void Report(
        Action<DiagnosticInfo>? report, INamedTypeSymbol handlerType, ISymbol member, ITypeSymbol type,
        string sourceDescription, SymbolDisplayFormat fmt) {
        report?.Invoke(DiagnosticInfo.Create(
            UnbindableMember, member.Locations.FirstOrDefault() ?? handlerType.Locations.FirstOrDefault(),
            handlerType.ToDisplayString(), member.Name, type.ToDisplayString(), sourceDescription));
    }

    private static string SourceDescription(BindingSource source) {
        return source switch {
            BindingSource.Route => "route",
            BindingSource.Header => "request headers",
            BindingSource.Form or BindingSource.FormFile or BindingSource.FormFiles => "form",
            _ => "query string"
        };
    }

    private static (BindingSource? Source, string? NameOverride, bool Unsupported) ReadSourceAttribute(
        ImmutableArray<AttributeData> attributes) {
        foreach (var attr in attributes) {
            if (attr.AttributeClass is not { } attributeClass)
                continue;

            foreach (var iface in attributeClass.AllInterfaces) {
                if (iface.ContainingNamespace?.ToDisplayString() != BindingMetadataNamespace)
                    continue;

                switch (iface.Name) {
                    case "IFromRouteMetadata":
                        return (BindingSource.Route, AttributeName(attr), false);
                    case "IFromQueryMetadata":
                        return (BindingSource.Query, AttributeName(attr), false);
                    case "IFromHeaderMetadata":
                        return (BindingSource.Header, AttributeName(attr), false);
                    case "IFromFormMetadata":
                        return (BindingSource.Form, AttributeName(attr), false);
                    case "IFromBodyMetadata":
                        return (BindingSource.Body, null, false);
                    case "IFromServiceMetadata":
                    case "IFromKeyedServicesMetadata":
                        return (null, null, true);
                }
            }
        }

        return (null, null, false);
    }

    private static string? AttributeName(AttributeData attr) {
        foreach (var named in attr.NamedArguments)
            if (named.Key == "Name" && named.Value.Value is string { Length: > 0 } name)
                return name;

        return null;
    }

    private static BindingSource InferSource(ITypeSymbol type, string lookupName, HashSet<string> routeTokens) {
        if (IsFormFileType(type))
            return type.Name == "IFormFileCollection" ? BindingSource.FormFiles : BindingSource.FormFile;

        return routeTokens.Contains(lookupName) ? BindingSource.Route : BindingSource.Query;
    }

    private static bool TryClassifyValue(
        ITypeSymbol type, BindingSource source, SymbolDisplayFormat fmt,
        out BindingParse parse, out string valueTypeFqn, out bool declaredNullable) {
        parse = BindingParse.String;
        valueTypeFqn = string.Empty;
        declaredNullable = false;

        if (type.ContainingNamespace?.ToDisplayString() == HttpNamespace) {
            switch (type.Name) {
                case "IFormFile":
                    parse = BindingParse.File;
                    declaredNullable = type.NullableAnnotation == NullableAnnotation.Annotated;
                    return true;
                case "IFormFileCollection":
                    parse = BindingParse.Files;
                    declaredNullable = type.NullableAnnotation == NullableAnnotation.Annotated;
                    return true;
            }
        }

        if (type is IArrayTypeSymbol array) {
            // Repeated keys are a query-string shape; a route token, header, or form field binds one value.
            if (source != BindingSource.Query)
                return false;

            declaredNullable = type.NullableAnnotation == NullableAnnotation.Annotated;
            var element = array.ElementType;
            if (element.SpecialType == SpecialType.System_String) {
                parse = BindingParse.StringArray;
                return true;
            }

            if (element.TypeKind == TypeKind.Enum) {
                parse = BindingParse.EnumArray;
                valueTypeFqn = element.ToDisplayString(fmt);
                return true;
            }

            if (element.IsValueType && IsSelfParsable(element)) {
                parse = BindingParse.ParsableArray;
                valueTypeFqn = element.ToDisplayString(fmt);
                return true;
            }

            return false;
        }

        if (type.SpecialType == SpecialType.System_String) {
            parse = BindingParse.String;
            declaredNullable = type.NullableAnnotation == NullableAnnotation.Annotated;
            return true;
        }

        var valueType = type;
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable) {
            declaredNullable = true;
            valueType = nullable.TypeArguments[0];
        }

        if (valueType.TypeKind == TypeKind.Enum) {
            parse = BindingParse.Enum;
            valueTypeFqn = valueType.ToDisplayString(fmt);
            return true;
        }

        if (valueType.IsValueType && IsSelfParsable(valueType)) {
            parse = BindingParse.Parsable;
            valueTypeFqn = valueType.ToDisplayString(fmt);
            return true;
        }

        return false;
    }

    private static bool IsSelfParsable(ITypeSymbol type) {
        foreach (var iface in type.AllInterfaces)
            if (iface is { Name: "IParsable", TypeArguments.Length: 1 }
                && iface.ContainingNamespace is { Name: "System", ContainingNamespace.IsGlobalNamespace: true }
                && SymbolEqualityComparer.Default.Equals(iface.TypeArguments[0], type))
                return true;

        return false;
    }

    private static bool IsNullableType(ITypeSymbol type) {
        return type.NullableAnnotation == NullableAnnotation.Annotated
               || type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T };
    }

    private static IMethodSymbol? SelectConstructor(INamedTypeSymbol requestType) {
        IMethodSymbol? best = null;
        foreach (var ctor in requestType.InstanceConstructors) {
            if (ctor.DeclaredAccessibility != Accessibility.Public || ctor.Parameters.Length == 0)
                continue;
            if (best is null || ctor.Parameters.Length > best.Parameters.Length)
                best = ctor;
        }

        return best;
    }

    private static bool HasInitializer(IPropertySymbol property) {
        foreach (var reference in property.DeclaringSyntaxReferences)
            if (reference.GetSyntax() is PropertyDeclarationSyntax { Initializer: not null })
                return true;

        return false;
    }

    /// <summary>The route template's parameter token names, e.g. <c>{id:guid}</c> and <c>{*path}</c> → id, path.</summary>
    private static HashSet<string> RouteTokenNames(string route) {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        while (index < route.Length) {
            var open = route.IndexOf('{', index);
            if (open < 0) break;

            var close = route.IndexOf('}', open + 1);
            if (close < 0) break;

            var inner = route.Substring(open + 1, close - open - 1).TrimStart('*');
            foreach (var stop in new[] { ':', '=', '?' }) {
                var at = inner.IndexOf(stop);
                if (at >= 0) inner = inner.Substring(0, at);
            }

            if (inner.Length > 0) tokens.Add(inner);
            index = close + 1;
        }

        return tokens;
    }

    private static string? DefaultLiteral(IParameterSymbol parameter, SymbolDisplayFormat fmt) {
        if (!parameter.HasExplicitDefaultValue)
            return null;

        var value = parameter.ExplicitDefaultValue;
        if (value is null)
            return "null";

        var type = parameter.Type;
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
            type = nullable.TypeArguments[0];

        if (type.TypeKind == TypeKind.Enum)
            return $"({type.ToDisplayString(fmt)})({Convert.ToString(value, CultureInfo.InvariantCulture)})";

        return value switch {
            string s => SymbolDisplay.FormatLiteral(s, true),
            char c => SymbolDisplay.FormatLiteral(c, true),
            bool b => b ? "true" : "false",
            float f => f.ToString("R", CultureInfo.InvariantCulture) + "f",
            double d => d.ToString("R", CultureInfo.InvariantCulture) + "d",
            decimal m => m.ToString(CultureInfo.InvariantCulture) + "m",
            uint u => u.ToString(CultureInfo.InvariantCulture) + "u",
            long l => l.ToString(CultureInfo.InvariantCulture) + "L",
            ulong ul => ul.ToString(CultureInfo.InvariantCulture) + "UL",
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "default"
        };
    }
}
