using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Elarion.Generators;

/// <summary>
/// Collects a handler's declared error contract (ADR-0080): the <c>[ProducesError]</c> declarations on the handler
/// plus the failures the framework pipeline adds where they are statically knowable (request validation,
/// authorization requirements, feature gates, idempotency, previews, declared upload limits);
/// <see cref="ErrorContractScopes"/> later adds the declarations the operation inherits from its pipeline decorators,
/// its module and its assembly. The result is published with the operation, so the schema export and the generated
/// clients describe every failure the operation can report. Also verifies that the response graph does not opt
/// members out of the serializer contract (ADR-0082).
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
            ["Internal"] = "internal",
            ["DecisionRequired"] = "decision_required"
        };

    public const string ValidationDataFqn = "global::Elarion.Abstractions.ValidationErrorData";

    private const string RequireResourceAttributeMetadataName =
        "Elarion.Abstractions.Authorization.RequireResourceAttribute";

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
        "'{0}' declares error code '{1}' which is not valid: use lower-case ASCII letters, digits and "
        + "underscores in dot-separated segments (for example \"token.malformed\")",
        "Elarion.Errors",
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor ConflictingErrorDeclaration = new(
        "ELERR002",
        "Conflicting error declaration",
        "'{0}' declares error code '{1}' more than once with a different kind or payload type; a code "
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

    /// <summary>
    /// One declared error. <see cref="Decision"/> is set for a <c>[RequiresDecision]</c> declaration (kind
    /// <c>DecisionRequired</c>): <c>"1|a,b"</c> for a per-item decision with options <c>a</c> and <c>b</c>,
    /// <c>"0|confirm"</c> for a whole-request confirmation.
    /// </summary>
    public sealed record ErrorDeclaration(string Code, string Kind, string? DataTypeFqn, string? Decision = null);

    public const string RequiresDecisionAttributeMetadataName = "Elarion.Abstractions.RequiresDecisionAttribute";

    public static readonly DiagnosticDescriptor DecisionOptionsNotEnum = new(
        "ELERR003",
        "Decision options are not an enum",
        "'{0}' declares decision '{1}' with Options = typeof({2}), which is not an enum with members; the options of a "
        + "decision are the members of an enum (omit Options for a confirmation)",
        "Elarion.Errors",
        DiagnosticSeverity.Error,
        true);

    /// <summary>The handler's declared and implied errors, sorted by code; reports invalid or conflicting declarations.</summary>
    /// <remarks>
    /// Declarations inherited from outside the handler class (pipeline decorators, the module, the assembly) are
    /// added by a later stage, <see cref="ErrorContractScopes"/>, because they live in other files.
    /// <paramref name="responseValueType"/> is the handler's success type (the <c>T</c> of <c>Result&lt;T&gt;</c>): the
    /// preview errors are implied only when it is a preview outcome, and <c>preview.changed</c> carries it as payload.
    /// </remarks>
    public static EquatableArray<ErrorDeclaration> Collect(
        INamedTypeSymbol handler,
        INamedTypeSymbol requestType,
        Compilation compilation,
        bool isIdempotent,
        Action<DiagnosticInfo>? report,
        ITypeSymbol? responseValueType = null) {
        var byCode = new Dictionary<string, ErrorDeclaration>(System.StringComparer.Ordinal);
        var owner = handler.ToDisplayString();
        var location = handler.Locations.FirstOrDefault();
        for (var current = handler; current is not null; current = current.BaseType)
            ReadDeclarations(current.GetAttributes(), owner, _ => location, byCode, report);

        // The handler's own declaration wins over an implied one, so an upload code declared with another payload than
        // the one IUploadedFiles answers with (typically none, as before the payloads existed) would hide the typed data.
        if (report is not null)
            foreach (var (code, payload) in UploadPayloads)
                if (byCode.TryGetValue(code, out var declared) && declared.DataTypeFqn != payload)
                    report(DiagnosticInfo.Create(
                        UploadErrorPayloadMismatch, location, owner, code,
                        DescribePayload(declared.DataTypeFqn), DescribePayload(payload)));

        foreach (var implied in ImpliedErrors(handler, requestType, compilation, isIdempotent, responseValueType))
            if (!byCode.ContainsKey(implied.Code))
                byCode[implied.Code] = implied;

        return Sorted(byCode.Values);
    }

    /// <summary>
    /// Reads the <c>[ProducesError]</c> declarations among <paramref name="attributes"/> into
    /// <paramref name="byCode"/>, keeping the first declaration of a code. An invalid code (ELERR001) and a second
    /// declaration of a code with a different kind or payload (ELERR002) are reported against
    /// <paramref name="owner"/>, the type or assembly that carries the attributes.
    /// </summary>
    public static void ReadDeclarations(
        IEnumerable<AttributeData> attributes,
        string owner,
        Func<AttributeData, Location?> locate,
        Dictionary<string, ErrorDeclaration> byCode,
        Action<DiagnosticInfo>? report) {
        foreach (var attribute in attributes) {
            ErrorDeclaration? declaration;
            var attributeName = attribute.AttributeClass?.ToDisplayString();
            if (attributeName == ProducesErrorAttributeMetadataName) {
                if (!TryReadDeclaration(attribute, out declaration))
                    continue;
            }
            else if (attributeName == RequiresDecisionAttributeMetadataName) {
                if (!TryReadDecision(attribute, owner, locate, report, out declaration))
                    continue;
            }
            else {
                continue;
            }

            if (!IsValidCode(declaration.Code)) {
                report?.Invoke(DiagnosticInfo.Create(InvalidErrorCode, locate(attribute), owner, declaration.Code));
                continue;
            }

            if (byCode.TryGetValue(declaration.Code, out var existing)) {
                if (existing != declaration)
                    report?.Invoke(DiagnosticInfo.Create(
                        ConflictingErrorDeclaration, locate(attribute), owner, declaration.Code));

                continue;
            }

            byCode[declaration.Code] = declaration;
        }
    }

    /// <summary>The declarations in ordinal code order — the published, deterministic contract order.</summary>
    public static EquatableArray<ErrorDeclaration> Sorted(IEnumerable<ErrorDeclaration> declarations) {
        return declarations
            .OrderBy(static d => d.Code, System.StringComparer.Ordinal)
            .ToEquatableArray();
    }

    // The failures the framework pipeline adds to a handler. Only what is knowable from the handler's own
    // attributes and request graph: assembly/module authorization defaults are not visible per handler, which is
    // why the runtime check always admits the default unauthorized/forbidden codes.
    private static IEnumerable<ErrorDeclaration> ImpliedErrors(
        INamedTypeSymbol handler, INamedTypeSymbol requestType, Compilation compilation, bool isIdempotent,
        ITypeSymbol? responseValueType) {
        if (compilation.GetTypeByMetadataName(ElarionValidationExtensionsMetadataName) is not null
            && ValidatableTypeWalker.IsValidatable(
                requestType, new ValidatableTypeWalker.Context(compilation.Assembly)))
            yield return new ErrorDeclaration("validation", "Validation", ValidationDataFqn);

        var hasAuthorization = false;
        var hasFeatureGate = false;
        var hasHiddenResource = false;
        for (var current = handler; current is not null; current = current.BaseType)
            foreach (var attribute in current.GetAttributes()) {
                var name = attribute.AttributeClass?.ToDisplayString();
                if (name is null) continue;

                if (name == FeatureGateAttributeMetadataName) hasFeatureGate = true;
                else if (AuthorizationRequirementAttributes.Contains(name)) hasAuthorization = true;

                // [RequireResource(DenyAs = ResourceDenial.NotFound)] answers a denial as not_found.
                if (name == RequireResourceAttributeMetadataName && attribute.NamedArguments.Any(static argument =>
                        argument.Key == "DenyAs" && argument.Value.Value is 1))
                    hasHiddenResource = true;
            }

        if (hasAuthorization) {
            yield return new ErrorDeclaration("unauthorized", "Unauthorized", null);
            yield return new ErrorDeclaration("forbidden", "Forbidden", null);
        }

        if (hasFeatureGate || hasHiddenResource) yield return new ErrorDeclaration("not_found", "NotFound", null);

        if (isIdempotent) {
            yield return new ErrorDeclaration("idempotency.key_required", "Validation", null);
            yield return new ErrorDeclaration("idempotency.in_progress", "Conflict", null);
            yield return new ErrorDeclaration("idempotency.key_reused", "BusinessRule", null);
        }

        // A previewable command (ADR-0104): exactly where the preview decorator attaches, it can refuse a stale
        // commit (carrying the new outcome) and refuse a preview on a host whose unit of work cannot roll back.
        if (responseValueType is not null
            && HandlerRegistrationGenerator.IsPreviewableRequest(requestType, compilation)
            && HandlerRegistrationGenerator.IsPreviewOutcome(responseValueType, compilation)) {
            yield return new ErrorDeclaration(
                "preview.changed", "Conflict", responseValueType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
            yield return new ErrorDeclaration("preview.unsupported", "Internal", null);
        }

        // Declared upload limits (ADR-0105): exactly where the upload-limits decorator attaches, the handler reads
        // uploads through IUploadedFiles, which refuses a missing reference and a file that breaks a declared limit,
        // each limit with the limit as typed payload. Invalid declarations are reported by the handler generator.
        if (HandlerRegistrationGenerator.ParseUploadLimits(
                requestType, compilation, ImmutableArray.CreateBuilder<DiagnosticInfo>()) is { Members.IsEmpty: false } uploads) {
            yield return new ErrorDeclaration("upload.not_found", "Validation", null);
            if (uploads.Members.Any(static member => member.MaxBytes > 0))
                yield return new ErrorDeclaration("upload.too_large", "Validation", UploadTooLargeDataFqn);
            if (uploads.Members.Any(static member => !member.ContentTypes.IsEmpty))
                yield return new ErrorDeclaration("upload.content_type", "Validation", UploadContentTypeDataFqn);
            if (uploads.Members.Any(static member => !member.FileExtensions.IsEmpty))
                yield return new ErrorDeclaration("upload.file_extension", "Validation", UploadFileExtensionDataFqn);
        }
    }

    private const string UploadTooLargeDataFqn = "global::Elarion.Abstractions.UploadTooLargeData";
    private const string UploadContentTypeDataFqn = "global::Elarion.Abstractions.UploadContentTypeData";
    private const string UploadFileExtensionDataFqn = "global::Elarion.Abstractions.UploadFileExtensionData";

    // The payload IUploadedFiles answers each of its codes with (ErrorCodes.Upload*), null for none.
    private static readonly (string Code, string? Payload)[] UploadPayloads = [
        ("upload.not_found", null),
        ("upload.too_large", UploadTooLargeDataFqn),
        ("upload.content_type", UploadContentTypeDataFqn),
        ("upload.file_extension", UploadFileExtensionDataFqn)
    ];

    public static readonly DiagnosticDescriptor UploadErrorPayloadMismatch = new(
        "ELERR004",
        "Upload error declared with another payload",
        "'{0}' declares error code '{1}' with {2}, but IUploadedFiles answers it with {3}, so the schema and the "
        + "generated client misdescribe it; declare the code with that payload, or drop the declaration when the "
        + "request's [UploadLimits] implies it",
        "Elarion.Errors",
        DiagnosticSeverity.Warning,
        true);

    private static string DescribePayload(string? fqn) {
        return fqn is null
            ? "no payload"
            : "payload '" + (fqn.StartsWith("global::", System.StringComparison.Ordinal) ? fqn.Substring(8) : fqn) + "'";
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

    // [RequiresDecision(code, Options = typeof(TEnum), PerItem = …, DataType = typeof(T))]: a DecisionRequired error
    // whose options are the enum's member names in camel case (the wire names DecisionExtensions parses), or the one
    // option "confirm" without Options.
    private static bool TryReadDecision(
        AttributeData attribute,
        string owner,
        Func<AttributeData, Location?> locate,
        Action<DiagnosticInfo>? report,
        out ErrorDeclaration declaration) {
        declaration = null!;
        if (attribute.ConstructorArguments.Length != 1 || attribute.ConstructorArguments[0].Value is not string code)
            return false;

        string? dataType = null;
        var perItem = false;
        var options = new List<string>();
        foreach (var named in attribute.NamedArguments) {
            switch (named.Key) {
                case "DataType" when named.Value.Value is ITypeSymbol data:
                    dataType = data.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                    break;
                case "PerItem" when named.Value.Value is bool value:
                    perItem = value;
                    break;
                case "Options" when named.Value.Value is ITypeSymbol optionsType:
                    if (optionsType.TypeKind == TypeKind.Enum)
                        foreach (var member in optionsType.GetMembers())
                            if (member is IFieldSymbol { HasConstantValue: true } field)
                                options.Add(CamelCase(field.Name));

                    if (options.Count == 0) {
                        report?.Invoke(DiagnosticInfo.Create(DecisionOptionsNotEnum, locate(attribute), owner, code,
                            optionsType.ToDisplayString()));
                        return false;
                    }

                    break;
            }
        }

        if (options.Count == 0)
            options.Add("confirm");

        declaration = new ErrorDeclaration(code, "DecisionRequired", dataType,
            (perItem ? "1|" : "0|") + string.Join(",", options));
        return true;
    }

    // System.Text.Json's camel case: the leading run of upper-case letters is lowered, keeping the last one of a run
    // that starts a new word ("URLValue" -> "urlValue", "OnlyHere" -> "onlyHere").
    internal static string CamelCase(string name) {
        if (name.Length == 0 || !char.IsUpper(name[0]))
            return name;

        var chars = name.ToCharArray();
        for (var i = 0; i < chars.Length; i++) {
            if (i == 1 && !char.IsUpper(chars[i]))
                break;

            var hasNext = i + 1 < chars.Length;
            if (i > 0 && hasNext && !char.IsUpper(chars[i + 1]))
                break;

            chars[i] = char.ToLowerInvariant(chars[i]);
        }

        return new string(chars);
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
