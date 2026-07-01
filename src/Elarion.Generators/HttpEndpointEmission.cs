using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Elarion.Generators;

/// <summary>
/// Shared discovery and emission for <c>[Elarion.Abstractions.HttpEndpoint]</c> handlers, consumed by
/// <see cref="AppModuleDiscoveryGenerator"/> for the module-grouped, feature-flag-gated mapping (the only
/// transport-wiring path). Keeps the binding-mode detection and the emitted minimal-API lambda in one place.
/// </summary>
internal static class HttpEndpointEmission
{
    public const string HttpEndpointAttributeMetadataName = "Elarion.Abstractions.HttpEndpointAttribute";
    private const string DescriptionAttributeMetadataName = "System.ComponentModel.DescriptionAttribute";
    private const string AsParametersAttributeFqn = "Microsoft.AspNetCore.Http.AsParametersAttribute";
    private const string BindingMetadataNamespace = "Microsoft.AspNetCore.Http.Metadata";
    private const string HttpNamespace = "Microsoft.AspNetCore.Http";

    public static readonly DiagnosticDescriptor MissingRequestResponse = new(
        id: "ELHTTP001",
        title: "HTTP endpoint handler has no resolvable request/response shape",
        messageFormat:
        "Handler '{0}' is annotated with [HttpEndpoint] but does not implement IHandler<TRequest, TResponse> with a "
        + "Result<T> response; no endpoint will be generated",
        category: "Elarion.Http",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor CannotInferVerb = new(
        id: "ELHTTP004",
        title: "Cannot infer HTTP verb",
        messageFormat:
        "Handler '{0}' has [HttpEndpoint] without an explicit verb and its request implements neither ICommand "
        + "(POST) nor IQuery (GET); specify a verb on [HttpEndpoint] or implement ICommand/IQuery",
        category: "Elarion.Http",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor DuplicateRoute = new(
        id: "ELHTTP002",
        title: "Duplicate HTTP endpoint route",
        messageFormat: "The route '{0} {1}' is mapped by both '{2}' and '{3}'",
        category: "Elarion.Http",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor UnmatchedModule = new(
        id: "ELHTTP003",
        title: "HTTP endpoint handler is not in any module",
        messageFormat:
        "Handler '{0}' is annotated with [HttpEndpoint] but its namespace is not under any [AppModule]; it will "
        + "be mapped unconditionally (not gated by a module feature flag)",
        category: "Elarion.Http",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    /// <summary>One discovered HTTP endpoint. String-only for incremental-generator value equality.</summary>
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
        string? Description
    );

    public static void ReportDuplicateRoutes(IEnumerable<Model> entries, List<DiagnosticInfo> diagnostics)
    {
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var key = $"{entry.Verb} {entry.Route}";
            if (seen.TryGetValue(key, out var existing))
            {
                diagnostics.Add(DiagnosticInfo.Create(
                    DuplicateRoute, (Location?)null, entry.Verb.ToUpperInvariant(), entry.Route, existing, entry.EndpointName));
            }
            else
            {
                seen[key] = entry.EndpointName;
            }
        }
    }

    /// <summary>
    /// Emits one minimal-API endpoint registration onto <paramref name="target"/> (e.g. <c>app</c> or
    /// <c>endpoints</c>), indented by <paramref name="indent"/>.
    /// </summary>
    public static void AppendRegistration(StringBuilder sb, Model entry, string indent, string target)
    {
        const string Handler = "global::Elarion.Abstractions.IHandler";
        const string Result = "global::Elarion.Abstractions.Result";
        const string Results = "global::Elarion.AspNetCore.ElarionHttpResults";

        var inner = indent + "    ";
        var deeper = indent + "        ";
        var resultCall = entry.ResponseIsEmpty ? "ToNoContentResult" : "ToResult";
        var requestParameter = entry.UseAsParameters
            ? $"[global::Microsoft.AspNetCore.Http.AsParameters] {entry.RequestTypeFqn} request"
            : $"{entry.RequestTypeFqn} request";

        sb.AppendLine($"{indent}{target}.Map{entry.Verb}({Literal(entry.Route)},");
        sb.AppendLine($"{inner}static async (");
        sb.AppendLine($"{deeper}{requestParameter},");
        sb.AppendLine(
            $"{deeper}[global::Microsoft.AspNetCore.Mvc.FromServices] {Handler}<{entry.RequestTypeFqn}, {Result}<{entry.ResponseTypeFqn}>> handler,");
        sb.AppendLine($"{deeper}global::System.Threading.CancellationToken ct) =>");
        sb.AppendLine($"{deeper}{Results}.{resultCall}(await handler.HandleAsync(request, ct)))");
        sb.AppendLine($"{inner}.WithName({Literal(entry.EndpointName)})");
        if (entry.Description is not null)
            sb.AppendLine($"{inner}.WithDescription({Literal(entry.Description)})");

        if (entry.ResponseIsEmpty)
            sb.AppendLine($"{inner}.Produces(204)");
        else
            sb.AppendLine($"{inner}.Produces<{entry.ResponseTypeFqn}>(200)");

        sb.Append($"{inner}.ProducesElarionErrors()");
        if (entry.DisableAntiforgery)
        {
            sb.AppendLine();
            sb.Append($"{inner}.DisableAntiforgery()");
        }

        sb.AppendLine(";");
    }

    public static bool TryCreateModel(
        INamedTypeSymbol type,
        AttributeData attr,
        INamedTypeSymbol? descriptionType,
        SymbolDisplayFormat fmt,
        Action<Diagnostic>? report,
        CancellationToken ct,
        out Model? model)
    {
        model = null;

        var (route, explicitVerb) = ReadHttpEndpoint(attr);
        if (route is null)
            return false;

        ct.ThrowIfCancellationRequested();
        if (!HandlerShape.TryResolve(type, out var requestType, out var responseInner, out _))
        {
            report?.Invoke(Diagnostic.Create(
                MissingRequestResponse, type.Locations.FirstOrDefault() ?? Location.None, type.ToDisplayString()));
            return false;
        }

        var verb = explicitVerb ?? InferVerb(requestType);
        if (verb is null)
        {
            report?.Invoke(Diagnostic.Create(
                CannotInferVerb, type.Locations.FirstOrDefault() ?? Location.None, type.ToDisplayString()));
            return false;
        }

        var (useAsParameters, disableAntiforgery) = DetermineBinding(requestType, verb);
        var responseNamed = responseInner as INamedTypeSymbol;

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
            GetDescription(type, descriptionType));
        return true;
    }

    private const string CommandMarkerDisplay = "Elarion.Abstractions.ICommand";
    private const string QueryMarkerDisplay = "Elarion.Abstractions.IQuery";

    // Verb inference is marker-based only: a request implementing ICommand maps to POST, IQuery to GET.
    // Naming/nesting carry no semantic weight; an unmarked request needs an explicit verb on [HttpEndpoint].
    private static string? InferVerb(INamedTypeSymbol requestType)
    {
        if (HandlerShape.Implements(requestType, CommandMarkerDisplay))
            return "Post";
        if (HandlerShape.Implements(requestType, QueryMarkerDisplay))
            return "Get";
        return null;
    }

    private static (string? Route, string? Verb) ReadHttpEndpoint(AttributeData attr)
    {
        var args = attr.ConstructorArguments;
        return args.Length switch
        {
            1 => (args[0].Value as string, null),
            2 => (args[1].Value as string, VerbName(args[0])),
            _ => (null, null),
        };
    }

    private static string? VerbName(TypedConstant verb)
    {
        if (verb.Type is not INamedTypeSymbol enumType || verb.Value is not int value)
            return null;

        foreach (var member in enumType.GetMembers())
        {
            if (member is IFieldSymbol { HasConstantValue: true, ConstantValue: int fieldValue } field && fieldValue == value)
                return field.Name;
        }

        return null;
    }

    private static (bool UseAsParameters, bool DisableAntiforgery) DetermineBinding(INamedTypeSymbol requestType, string verb)
    {
        var optIn = HasAsParametersAttribute(requestType);
        var hasForm = false;

        foreach (var property in PublicInstanceProperties(requestType))
        {
            if (IsFormFileType(property.Type))
            {
                optIn = true;
                hasForm = true;
            }

            foreach (var attr in property.GetAttributes())
            {
                if (attr.AttributeClass is not { } attributeClass)
                    continue;

                if (ImplementsBindingMetadata(attributeClass, out var isForm))
                {
                    optIn = true;
                    hasForm |= isForm;
                }
            }
        }

        var useAsParameters = optIn || verb is "Get" or "Delete";
        return (useAsParameters, hasForm);
    }

    private static bool HasAsParametersAttribute(INamedTypeSymbol type)
    {
        foreach (var attr in type.GetAttributes())
        {
            if (attr.AttributeClass?.ToDisplayString() == AsParametersAttributeFqn)
                return true;
        }

        return false;
    }

    private static bool ImplementsBindingMetadata(INamedTypeSymbol attributeClass, out bool isForm)
    {
        isForm = false;
        var found = false;
        foreach (var iface in attributeClass.AllInterfaces)
        {
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

    private static bool IsFormFileType(ITypeSymbol type)
    {
        var element = type switch
        {
            IArrayTypeSymbol array => array.ElementType,
            INamedTypeSymbol { TypeArguments.Length: 1 } generic => generic.TypeArguments[0],
            _ => type,
        };

        return element.ContainingNamespace?.ToDisplayString() == HttpNamespace
            && element.Name is "IFormFile" or "IFormFileCollection";
    }

    private static bool IsResponseEmpty(INamedTypeSymbol responseType) =>
        !PublicInstanceProperties(responseType).Any();

    private static IEnumerable<IPropertySymbol> PublicInstanceProperties(INamedTypeSymbol type)
    {
        for (INamedTypeSymbol? current = type; current is not null; current = current.BaseType)
        {
            foreach (var member in current.GetMembers())
            {
                if (member is IPropertySymbol
                    {
                        IsStatic: false,
                        IsIndexer: false,
                        DeclaredAccessibility: Accessibility.Public,
                    } property)
                {
                    yield return property;
                }
            }
        }
    }

    private static string? GetDescription(ISymbol symbol, INamedTypeSymbol? descriptionType)
    {
        if (descriptionType is null)
            return null;

        foreach (var attr in symbol.GetAttributes())
        {
            if (!SymbolEqualityComparer.Default.Equals(attr.AttributeClass, descriptionType))
                continue;
            if (attr.ConstructorArguments.Length == 0)
                continue;

            return attr.ConstructorArguments[0].Value as string is { Length: > 0 } value ? value : null;
        }

        return null;
    }

    private static string Literal(string value) => SymbolDisplay.FormatLiteral(value, quote: true);
}
