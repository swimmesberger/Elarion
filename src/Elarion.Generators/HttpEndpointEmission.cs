using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Elarion.Generators;

/// <summary>
/// Shared discovery and emission for <c>[Elarion.Abstractions.HttpEndpoint]</c> handlers, consumed by
/// <see cref="AppModuleDiscoveryGenerator"/> for the module-grouped, feature-flag-gated mapping (the only
/// transport-wiring path). Keeps the binding-mode detection, the compile-time request-binding analysis, and the
/// emitted <c>RequestDelegate</c> registration in one place (ADR-0071).
/// </summary>
internal static partial class HttpEndpointEmission {
    public const string HttpEndpointAttributeMetadataName = "Elarion.Abstractions.HttpEndpointAttribute";
    private const string DescriptionAttributeMetadataName = "System.ComponentModel.DescriptionAttribute";
    private const string IdempotentAttributeFqn = "Elarion.Abstractions.Idempotency.IdempotentAttribute";
    private const string AsParametersAttributeFqn = "Microsoft.AspNetCore.Http.AsParametersAttribute";
    private const string BindingMetadataNamespace = "Microsoft.AspNetCore.Http.Metadata";
    private const string HttpNamespace = "Microsoft.AspNetCore.Http";

    public static readonly DiagnosticDescriptor MissingRequestResponse = new(
        "ELHTTP001",
        "HTTP endpoint handler has no resolvable request/response shape",
        "Handler '{0}' is annotated with [HttpEndpoint] but does not implement IHandler<TRequest, TResponse> with a "
        + "Result<T> response; no endpoint will be generated",
        "Elarion.Http",
        DiagnosticSeverity.Warning,
        true);

    public static readonly DiagnosticDescriptor CannotInferVerb = new(
        "ELHTTP004",
        "Cannot infer HTTP verb",
        "Handler '{0}' has [HttpEndpoint] without an explicit verb and its request implements neither ICommand "
        + "(POST) nor IQuery (GET); specify a verb on [HttpEndpoint] or implement ICommand/IQuery",
        "Elarion.Http",
        DiagnosticSeverity.Warning,
        true);

    public static readonly DiagnosticDescriptor DuplicateRoute = new(
        "ELHTTP002",
        "Duplicate HTTP endpoint route",
        "The route '{0} {1}' is mapped by both '{2}' and '{3}'",
        "Elarion.Http",
        DiagnosticSeverity.Warning,
        true);

    public static readonly DiagnosticDescriptor InvalidCustomizeEndpointHook = new(
        "ELHTTP006",
        "CustomizeEndpoint hook has an unusable shape",
        "Handler '{0}' declares a CustomizeEndpoint method that is not "
        + "'public static void CustomizeEndpoint(IEndpointConventionBuilder)'; the hook is ignored",
        "Elarion.Http",
        DiagnosticSeverity.Warning,
        true);

    public static readonly DiagnosticDescriptor UnmatchedModule = new(
        "ELHTTP003",
        "HTTP endpoint handler is not in any module",
        "Handler '{0}' is annotated with [HttpEndpoint] but its namespace is not under any [AppModule]; it will "
        + "be mapped unconditionally (not gated by a module feature flag)",
        "Elarion.Http",
        DiagnosticSeverity.Warning,
        true);

    /// <summary>
    /// One discovered HTTP endpoint. Strings, bools, and nested value-equatable records only, so the model stays
    /// cache-friendly in the incremental pipelines. <see cref="BindingMembers"/> carries the compile-time
    /// request-binding classification for the member-wise (<c>[AsParameters]</c>-style) shapes; it is empty when
    /// the whole request binds from the JSON body. <see cref="CustomizeEndpointTypeFqn"/> carries the handler
    /// type declaring a valid <c>CustomizeEndpoint</c> hook, or <see langword="null"/> when there is none.
    /// </summary>
    public sealed record Model(
        string EndpointName,
        string HandlerNamespace,
        string RequestTypeFqn,
        string ResponseTypeFqn,
        string Route,
        string Verb,
        bool UseAsParameters,
        bool DisableAntiforgery,
        bool ResponseIsEmpty,
        string? Description,
        bool IsIdempotent,
        EquatableArray<BindingMember> BindingMembers,
        string? CustomizeEndpointTypeFqn
    ) {
        /// <summary>
        /// Whether the response is the binary file payload (<c>Result&lt;ElarionFile&gt;</c>), mapped through the
        /// file translation instead of the JSON one. Derived from <see cref="ResponseTypeFqn"/>, so the manifest
        /// encoding is unchanged and older manifests decode into the same behavior.
        /// </summary>
        public bool ResponseIsFile => ResponseTypeFqn == ElarionGeneratorConventions.FileResponseTypeFqn;

        /// <summary>
        /// Whether the response is the created-resource envelope (<c>Result&lt;ElarionCreated&lt;T&gt;&gt;</c>),
        /// mapped through the created translation (<c>201</c> + <c>Location</c>, body = inner value). Derived
        /// from <see cref="ResponseTypeFqn"/> like <see cref="ResponseIsFile"/>, so the manifest encoding is
        /// unchanged and older manifests decode into the same behavior.
        /// </summary>
        public bool ResponseIsCreated =>
            ResponseTypeFqn.StartsWith(ElarionGeneratorConventions.CreatedResponseTypePrefix, StringComparison.Ordinal)
            && ResponseTypeFqn.EndsWith(">", StringComparison.Ordinal);

        /// <summary>The inner response type of a created-resource envelope — the advertised <c>201</c> body.</summary>
        public string CreatedInnerTypeFqn =>
            ResponseTypeFqn.Substring(
                ElarionGeneratorConventions.CreatedResponseTypePrefix.Length,
                ResponseTypeFqn.Length - ElarionGeneratorConventions.CreatedResponseTypePrefix.Length - 1);
    }

    public static void ReportDuplicateRoutes(IEnumerable<Model> entries, List<DiagnosticInfo> diagnostics) {
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in entries) {
            var key = $"{entry.Verb} {entry.Route}";
            if (seen.TryGetValue(key, out var existing))
                diagnostics.Add(DiagnosticInfo.Create(
                    DuplicateRoute, (Location?)null, entry.Verb.ToUpperInvariant(), entry.Route, existing,
                    entry.EndpointName));
            else
                seen[key] = entry.EndpointName;
        }
    }

    /// <summary>
    /// Reads one discovered <c>[HttpEndpoint]</c> handler from its attribute-provider context. Shared by
    /// <see cref="ElarionManifestGenerator"/> (which publishes the model and reports the shape diagnostics) and
    /// <see cref="AppModuleDiscoveryGenerator"/> (which maps current-compilation handlers and passes
    /// <paramref name="report"/> <c>null</c> — the manifest generator always runs alongside and owns the
    /// diagnostics), so the two discoveries cannot drift.
    /// </summary>
    public static Model? CreateModel(
        GeneratorAttributeSyntaxContext ctx,
        Action<DiagnosticInfo>? report,
        CancellationToken ct) {
        if (ctx.TargetSymbol is not INamedTypeSymbol type)
            return null;

        var descriptionType = ctx.SemanticModel.Compilation.GetTypeByMetadataName(DescriptionAttributeMetadataName);
        foreach (var attr in ctx.Attributes)
            if (TryCreateModel(type, attr, descriptionType, SymbolDisplayFormat.FullyQualifiedFormat, report, ct,
                    out var model)
                && model is not null)
                return model;

        return null;
    }

    private static bool TryCreateModel(
        INamedTypeSymbol type,
        AttributeData attr,
        INamedTypeSymbol? descriptionType,
        SymbolDisplayFormat fmt,
        Action<DiagnosticInfo>? report,
        CancellationToken ct,
        out Model? model) {
        model = null;

        var (route, explicitVerb) = ReadHttpEndpoint(attr);
        if (route is null)
            return false;

        ct.ThrowIfCancellationRequested();
        if (!HandlerShape.TryResolve(type, out var requestType, out var responseInner, out _)) {
            report?.Invoke(DiagnosticInfo.Create(
                MissingRequestResponse, type.Locations.FirstOrDefault(), type.ToDisplayString()));
            return false;
        }

        var verb = explicitVerb ?? InferVerb(requestType);
        if (verb is null) {
            report?.Invoke(DiagnosticInfo.Create(
                CannotInferVerb, type.Locations.FirstOrDefault(), type.ToDisplayString()));
            return false;
        }

        var (useAsParameters, disableAntiforgery) = DetermineBinding(requestType, verb);
        var responseNamed = responseInner as INamedTypeSymbol;

        // Member-wise shapes (GET/DELETE and the [AsParameters]/[From*]/file opt-ins) are classified at compile
        // time; the whole-body shapes bind the request as one JSON payload and carry no member facts.
        var bindingMembers = EquatableArray<BindingMember>.Empty;
        if (useAsParameters
            && !TryAnalyzeBindingMembers(type, requestType, route, fmt, report, out bindingMembers))
            return false;

        model = new Model(
            type.ToDisplayString(),
            type.ContainingNamespace?.ToDisplayString() ?? string.Empty,
            requestType.ToDisplayString(fmt),
            responseInner.ToDisplayString(fmt),
            route,
            verb,
            useAsParameters,
            disableAntiforgery,
            responseNamed is not null && IsResponseEmpty(responseNamed),
            GetDescription(type, descriptionType),
            IsIdempotentHandler(type),
            bindingMembers,
            DetectCustomizeEndpointHook(type, fmt, report));
        return true;
    }

    private const string CustomizeEndpointMethodName = "CustomizeEndpoint";
    private const string EndpointConventionBuilderFqn = "Microsoft.AspNetCore.Builder.IEndpointConventionBuilder";

    /// <summary>
    /// Detects the optional per-endpoint convention hook on the handler type:
    /// <c>public static void CustomizeEndpoint(IEndpointConventionBuilder)</c>. The generated registration calls
    /// it after the emitted metadata chain, so the handler can attach per-endpoint conventions (a policy, rate
    /// limiting, output caching) without leaving the generated mapping. The method must be <c>public</c> because
    /// the call site is emitted into the referencing host's compilation — the same visibility a cross-assembly
    /// handler already needs for its request/response DTOs. A method with the right name but the wrong shape is
    /// reported (<c>ELHTTP006</c>) and ignored rather than silently skipped.
    /// </summary>
    private static string? DetectCustomizeEndpointHook(
        INamedTypeSymbol type, SymbolDisplayFormat fmt, Action<DiagnosticInfo>? report) {
        var misshapen = false;
        foreach (var member in type.GetMembers(CustomizeEndpointMethodName)) {
            if (member is not IMethodSymbol method)
                continue;

            if (method is {
                    IsStatic: true,
                    ReturnsVoid: true,
                    IsGenericMethod: false,
                    DeclaredAccessibility: Accessibility.Public,
                    Parameters.Length: 1
                }
                && method.Parameters[0].Type.ToDisplayString() == EndpointConventionBuilderFqn)
                return type.ToDisplayString(fmt);

            misshapen = true;
        }

        if (misshapen)
            report?.Invoke(DiagnosticInfo.Create(
                InvalidCustomizeEndpointHook, type.Locations.FirstOrDefault(), type.ToDisplayString()));

        return null;
    }

    // [Idempotent] is declared with Inherited = false, so only the handler type's own attributes are inspected
    // (never a base type's). A simple presence check is enough for the HTTP marker — the full validation of the
    // attribute (e.g. the cacheable conflict) is owned by HandlerRegistrationGenerator's registration path.
    private static bool IsIdempotentHandler(INamedTypeSymbol type) {
        foreach (var attr in type.GetAttributes())
            if (attr.AttributeClass?.ToDisplayString() == IdempotentAttributeFqn)
                return true;

        return false;
    }

    private const string CommandMarkerDisplay = "Elarion.Abstractions.ICommand";
    private const string QueryMarkerDisplay = "Elarion.Abstractions.IQuery";

    // Verb inference is marker-based only: a request implementing ICommand maps to POST, IQuery to GET.
    // Naming/nesting carry no semantic weight; an unmarked request needs an explicit verb on [HttpEndpoint].
    private static string? InferVerb(INamedTypeSymbol requestType) {
        if (HandlerShape.Implements(requestType, CommandMarkerDisplay))
            return "Post";
        if (HandlerShape.Implements(requestType, QueryMarkerDisplay))
            return "Get";
        return null;
    }

    private static (string? Route, string? Verb) ReadHttpEndpoint(AttributeData attr) {
        var args = attr.ConstructorArguments;
        return args.Length switch {
            1 => (args[0].Value as string, null),
            2 => (args[1].Value as string, VerbName(args[0])),
            _ => (null, null)
        };
    }

    private static string? VerbName(TypedConstant verb) {
        if (verb.Type is not INamedTypeSymbol enumType || verb.Value is not int value)
            return null;

        foreach (var member in enumType.GetMembers())
            if (member is IFieldSymbol { HasConstantValue: true, ConstantValue: int fieldValue } field &&
                fieldValue == value)
                return field.Name;

        return null;
    }

    private static (bool UseAsParameters, bool DisableAntiforgery) DetermineBinding(INamedTypeSymbol requestType,
        string verb) {
        var optIn = HasAsParametersAttribute(requestType);
        var hasForm = false;

        foreach (var property in PublicInstanceProperties(requestType)) {
            if (IsFormFileType(property.Type)) {
                optIn = true;
                hasForm = true;
            }

            foreach (var attr in property.GetAttributes()) {
                if (attr.AttributeClass is not { } attributeClass)
                    continue;

                if (ImplementsBindingMetadata(attributeClass, out var isForm)) {
                    optIn = true;
                    hasForm |= isForm;
                }
            }
        }

        var useAsParameters = optIn || verb is "Get" or "Delete";
        return (useAsParameters, hasForm);
    }

    private static bool HasAsParametersAttribute(INamedTypeSymbol type) {
        foreach (var attr in type.GetAttributes())
            if (attr.AttributeClass?.ToDisplayString() == AsParametersAttributeFqn)
                return true;

        return false;
    }

    private static bool ImplementsBindingMetadata(INamedTypeSymbol attributeClass, out bool isForm) {
        isForm = false;
        var found = false;
        foreach (var iface in attributeClass.AllInterfaces) {
            if (iface.ContainingNamespace?.ToDisplayString() != BindingMetadataNamespace)
                continue;
            if (!iface.Name.StartsWith("IFrom", StringComparison.Ordinal) ||
                !iface.Name.EndsWith("Metadata", StringComparison.Ordinal))
                continue;

            found = true;
            if (iface.Name == "IFromFormMetadata")
                isForm = true;
        }

        return found;
    }

    private static bool IsFormFileType(ITypeSymbol type) {
        var element = type switch {
            IArrayTypeSymbol array => array.ElementType,
            INamedTypeSymbol { TypeArguments.Length: 1 } generic => generic.TypeArguments[0],
            _ => type
        };

        return element.ContainingNamespace?.ToDisplayString() == HttpNamespace
               && element.Name is "IFormFile" or "IFormFileCollection";
    }

    private static bool IsResponseEmpty(INamedTypeSymbol responseType) {
        return !PublicInstanceProperties(responseType).Any();
    }

    private static IEnumerable<IPropertySymbol> PublicInstanceProperties(INamedTypeSymbol type) {
        for (var current = type; current is not null; current = current.BaseType)
            foreach (var member in current.GetMembers())
                if (member is IPropertySymbol {
                        IsStatic: false,
                        IsIndexer: false,
                        DeclaredAccessibility: Accessibility.Public
                    } property)
                    yield return property;
    }

    private static string? GetDescription(ISymbol symbol, INamedTypeSymbol? descriptionType) {
        if (descriptionType is null)
            return null;

        foreach (var attr in symbol.GetAttributes()) {
            if (!SymbolEqualityComparer.Default.Equals(attr.AttributeClass, descriptionType))
                continue;
            if (attr.ConstructorArguments.Length == 0)
                continue;

            return attr.ConstructorArguments[0].Value as string is { Length: > 0 } value ? value : null;
        }

        return null;
    }

    private static string Literal(string value) {
        return SymbolDisplay.FormatLiteral(value, true);
    }
}
