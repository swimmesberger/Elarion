using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Elarion.Generators;

/// <summary>
/// Collects a handler's declared error contract (ADR-0080): the <c>[ProducesError]</c> declarations on the handler
/// plus the failures the framework pipeline adds where they are statically knowable (request validation,
/// authorization requirements, feature gates, idempotency). The result is published with the operation, so the
/// schema export and the generated clients describe every failure the operation can report. Also verifies that the
/// response graph does not opt members out of the serializer contract (ADR-0082).
/// </summary>
internal static class ErrorContractDiscovery {
    public const string ProducesErrorAttributeMetadataName = "Elarion.Abstractions.ProducesErrorAttribute";
    private const string FeatureGateAttributeMetadataName = "Elarion.Abstractions.Features.FeatureGateAttribute";
    private const string ErrorKindMetadataName = "Elarion.Abstractions.ErrorKind";
    private const string JsonIgnoreAttributeMetadataName = "System.Text.Json.Serialization.JsonIgnoreAttribute";
    private const string ElarionValidationExtensionsMetadataName =
        "Elarion.Validation.ElarionValidationServiceCollectionExtensions";

    // Default error codes per ErrorKind member name: a copy of Elarion.Abstractions.ErrorCodes.ForKind that the
    // generator cannot reference (it targets netstandard2.0); a test asserts both stay equal.
    internal static readonly IReadOnlyDictionary<string, string> DefaultCodeByKind =
        new Dictionary<string, string>(System.StringComparer.Ordinal) {
            ["Validation"] = "validation",
            ["NotFound"] = "not_found",
            ["Conflict"] = "conflict",
            ["Forbidden"] = "forbidden",
            ["Unauthorized"] = "unauthorized",
            ["BusinessRule"] = "business_rule",
            ["Internal"] = "internal"
        };

    public const string ValidationDataFqn = "global::Elarion.Abstractions.ValidationErrorData";

    private static readonly string[] AuthorizationRequirementAttributes = [
        "Elarion.Abstractions.Authorization.RequireClaimAttribute",
        "Elarion.Abstractions.Authorization.RequirePermissionAttribute",
        "Elarion.Abstractions.Authorization.RequireRoleAttribute",
        "Elarion.Abstractions.Authorization.RequirePolicyAttribute",
        "Elarion.Abstractions.Authorization.RequireResourceAttribute"
    ];

    public static readonly DiagnosticDescriptor InvalidErrorCode = new(
        "ELERR001",
        "Invalid error code",
        "Handler '{0}' declares error code '{1}' which is not valid: use lower-case ASCII letters, digits and "
        + "underscores in dot-separated segments (for example \"token.malformed\")",
        "Elarion.Errors",
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor ConflictingErrorDeclaration = new(
        "ELERR002",
        "Conflicting error declaration",
        "Handler '{0}' declares error code '{1}' more than once with a different kind or payload type; a code "
        + "has exactly one kind and one payload type",
        "Elarion.Errors",
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor ResponseMemberOptsOutOfContract = new(
        "ELRPC004",
        "Response member is omitted from the wire contract",
        "Response member '{0}' of handler '{1}' is non-nullable but is annotated [JsonIgnore(Condition = {2})]. "
        + "Non-nullable members are required on the wire, so the serializer must always write them. Make the "
        + "member nullable to declare it optional, or remove the ignore condition.",
        "Elarion.JsonRpc",
        DiagnosticSeverity.Error,
        true);

    public sealed record ErrorDeclaration(string Code, string Kind, string? DataTypeFqn);

    /// <summary>The handler's declared and implied errors, sorted by code; reports invalid or conflicting declarations.</summary>
    public static EquatableArray<ErrorDeclaration> Collect(
        INamedTypeSymbol handler,
        INamedTypeSymbol requestType,
        Compilation compilation,
        bool isIdempotent,
        Action<DiagnosticInfo>? report) {
        var byCode = new Dictionary<string, ErrorDeclaration>(System.StringComparer.Ordinal);

        for (var current = handler; current is not null; current = current.BaseType)
            foreach (var attribute in current.GetAttributes()) {
                if (attribute.AttributeClass?.ToDisplayString() != ProducesErrorAttributeMetadataName)
                    continue;

                if (!TryReadDeclaration(attribute, out var declaration))
                    continue;

                if (!IsValidCode(declaration.Code)) {
                    report?.Invoke(DiagnosticInfo.Create(
                        InvalidErrorCode, handler.Locations.FirstOrDefault(), handler.ToDisplayString(),
                        declaration.Code));
                    continue;
                }

                if (byCode.TryGetValue(declaration.Code, out var existing)) {
                    if (existing != declaration)
                        report?.Invoke(DiagnosticInfo.Create(
                            ConflictingErrorDeclaration, handler.Locations.FirstOrDefault(),
                            handler.ToDisplayString(), declaration.Code));

                    continue;
                }

                byCode[declaration.Code] = declaration;
            }

        foreach (var implied in ImpliedErrors(handler, requestType, compilation, isIdempotent))
            if (!byCode.ContainsKey(implied.Code))
                byCode[implied.Code] = implied;

        return byCode.Values
            .OrderBy(static d => d.Code, System.StringComparer.Ordinal)
            .ToEquatableArray();
    }

    // The failures the framework pipeline adds to a handler. Only what is knowable from the handler's own
    // attributes and request graph: assembly/module authorization defaults are not visible per handler, which is
    // why the runtime check always admits the default unauthorized/forbidden codes.
    private static IEnumerable<ErrorDeclaration> ImpliedErrors(
        INamedTypeSymbol handler, INamedTypeSymbol requestType, Compilation compilation, bool isIdempotent) {
        if (compilation.GetTypeByMetadataName(ElarionValidationExtensionsMetadataName) is not null
            && ValidatableTypeWalker.IsValidatable(
                requestType, new ValidatableTypeWalker.Context(compilation.Assembly)))
            yield return new ErrorDeclaration("validation", "Validation", ValidationDataFqn);

        var hasAuthorization = false;
        var hasFeatureGate = false;
        for (var current = handler; current is not null; current = current.BaseType)
            foreach (var attribute in current.GetAttributes()) {
                var name = attribute.AttributeClass?.ToDisplayString();
                if (name is null) continue;

                if (name == FeatureGateAttributeMetadataName) hasFeatureGate = true;
                else if (AuthorizationRequirementAttributes.Contains(name)) hasAuthorization = true;
            }

        if (hasAuthorization) {
            yield return new ErrorDeclaration("unauthorized", "Unauthorized", null);
            yield return new ErrorDeclaration("forbidden", "Forbidden", null);
        }

        if (hasFeatureGate) yield return new ErrorDeclaration("not_found", "NotFound", null);

        if (isIdempotent) {
            yield return new ErrorDeclaration("idempotency.key_required", "Validation", null);
            yield return new ErrorDeclaration("idempotency.in_progress", "Conflict", null);
            yield return new ErrorDeclaration("idempotency.key_reused", "BusinessRule", null);
        }
    }

    private static bool TryReadDeclaration(AttributeData attribute, out ErrorDeclaration declaration) {
        declaration = null!;
        var args = attribute.ConstructorArguments;
        if (args.Length == 0) return false;

        string? code = null;
        string? kind = null;
        string? dataType = null;
        var index = 0;
        if (args[0].Type?.TypeKind == TypeKind.Enum) {
            kind = EnumMemberName(args[0]);
            if (kind is null || !DefaultCodeByKind.TryGetValue(kind, out code)) return false;

            index = 1;
        }
        else {
            code = args[0].Value as string;
            if (code is null || args.Length < 2) return false;

            kind = EnumMemberName(args[1]);
            if (kind is null) return false;

            index = 2;
        }

        if (args.Length > index && args[index].Value is ITypeSymbol data)
            dataType = data.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        declaration = new ErrorDeclaration(code, kind, dataType);
        return true;
    }

    private static string? EnumMemberName(TypedConstant constant) {
        if (constant.Type is not INamedTypeSymbol enumType || constant.Value is null) return null;

        foreach (var member in enumType.GetMembers())
            if (member is IFieldSymbol { HasConstantValue: true } field
                && Equals(field.ConstantValue, constant.Value))
                return field.Name;

        return null;
    }

    internal static bool IsValidCode(string code) {
        if (code.Length == 0 || code[0] < 'a' || code[0] > 'z') return false;

        var segmentStart = true;
        foreach (var character in code) {
            if (character == '.') {
                if (segmentStart) return false;

                segmentStart = true;
                continue;
            }

            if (!((character >= 'a' && character <= 'z') || (character >= '0' && character <= '9')
                  || character == '_'))
                return false;

            segmentStart = false;
        }

        return !segmentStart;
    }

    /// <summary>
    /// Reports every non-nullable response member (anywhere in the response graph) that opts out of always being
    /// written with <c>[JsonIgnore(Condition = WhenWritingDefault | WhenWritingNull)]</c> (ADR-0082): the wire
    /// contract marks non-nullable members required, so the serializer must write them.
    /// </summary>
    public static void VerifyResponseContract(
        INamedTypeSymbol handler, ITypeSymbol responseType, Action<DiagnosticInfo> report) {
        Walk(handler, responseType, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default), report);
    }

    private static void Walk(
        INamedTypeSymbol handler,
        ITypeSymbol type,
        HashSet<ITypeSymbol> visited,
        Action<DiagnosticInfo> report) {
        switch (type) {
            case IArrayTypeSymbol array:
                Walk(handler, array.ElementType, visited, report);
                return;
            case INamedTypeSymbol { IsGenericType: true } generic when IsFrameworkType(generic):
                foreach (var argument in generic.TypeArguments)
                    Walk(handler, argument, visited, report);

                return;
            case not INamedTypeSymbol:
                return;
        }

        var namedType = (INamedTypeSymbol)type;
        if (namedType.SpecialType != SpecialType.None || namedType.TypeKind is TypeKind.Enum or TypeKind.Interface
            || IsFrameworkType(namedType) || !visited.Add(namedType))
            return;

        for (var current = namedType; current is not null && current.SpecialType != SpecialType.System_Object;
             current = current.BaseType)
            foreach (var member in current.GetMembers()) {
                if (member is not IPropertySymbol {
                        IsStatic: false, IsIndexer: false, DeclaredAccessibility: Accessibility.Public
                    } property)
                    continue;

                var condition = ReadIgnoreCondition(property);
                if (condition is "WhenWritingDefault" || (condition is "WhenWritingNull" && !IsValueType(property.Type)))
                    if (!IsNullable(property.Type))
                        report(DiagnosticInfo.Create(
                            ResponseMemberOptsOutOfContract,
                            property.Locations.FirstOrDefault(static l => l.IsInSource)
                            ?? handler.Locations.FirstOrDefault(),
                            $"{namedType.Name}.{property.Name}",
                            handler.ToDisplayString(),
                            condition));

                Walk(handler, property.Type, visited, report);
            }
    }

    private static string? ReadIgnoreCondition(IPropertySymbol property) {
        foreach (var attribute in property.GetAttributes()) {
            if (attribute.AttributeClass?.ToDisplayString() != JsonIgnoreAttributeMetadataName) continue;

            foreach (var named in attribute.NamedArguments)
                if (named.Key == "Condition")
                    return EnumMemberName(named.Value);
        }

        return null;
    }

    private static bool IsValueType(ITypeSymbol type) {
        return type.IsValueType;
    }

    private static bool IsNullable(ITypeSymbol type) {
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T }) return true;

        return !type.IsValueType && type.NullableAnnotation == NullableAnnotation.Annotated;
    }

    // BCL / framework generic and leaf types are not walked (collections are unwrapped by the caller through
    // their type arguments).
    private static bool IsFrameworkType(INamedTypeSymbol type) {
        var ns = type.ContainingNamespace?.ToDisplayString() ?? string.Empty;
        return ns == "System" || ns.StartsWith("System.", System.StringComparison.Ordinal)
                              || ns.StartsWith("Microsoft.", System.StringComparison.Ordinal);
    }
}
