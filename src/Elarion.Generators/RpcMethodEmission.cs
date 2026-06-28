using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Elarion.Generators;

/// <summary>
/// Shared discovery for <c>[Elarion.Abstractions.Handler]</c> handlers (including MCP metadata) and statement-style
/// <c>Map</c> emission onto the transport-neutral <c>HandlerDispatcher</c>, consumed by
/// <see cref="AppModuleDiscoveryGenerator"/> for the module-grouped, feature-flag-gated registration (the only
/// transport-wiring path).
/// </summary>
internal static class RpcMethodEmission
{
    public const string HandlerAttributeMetadataName = "Elarion.Abstractions.HandlerAttribute";
    public const string McpHandlerAttributeMetadataName = "Elarion.Abstractions.McpHandlerAttribute";
    private const string DescriptionAttributeMetadataName = "System.ComponentModel.DescriptionAttribute";

    // Suffixes stripped from a handler type name when inferring an operation name (e.g. CreateClientCommand -> createClient).
    private static readonly string[] OperationNameSuffixes = ["Handler", "Command", "Query", "Request"];

    public static readonly DiagnosticDescriptor UnmatchedModule = new(
        id: "ELRPC001",
        title: "Handler is not in any module",
        messageFormat:
        "Handler '{0}' is annotated with [Handler] but its namespace is not under any [AppModule]; it will be "
        + "registered unconditionally (not gated by a module feature flag)",
        category: "Elarion.JsonRpc",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor MissingHandlerShape = new(
        id: "ELRPC002",
        title: "Handler has no resolvable request/response shape",
        messageFormat:
        "Handler '{0}' is annotated with [Handler] but does not implement IHandler<TRequest, TResponse> with a "
        + "Result<T> response; no operation will be generated",
        category: "Elarion.JsonRpc",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor DuplicateOperationName = new(
        id: "ELRPC003",
        title: "Duplicate operation name",
        messageFormat:
        "Operation name '{0}' is produced by more than one handler; operation names must be unique across the bus — "
        + "give one an explicit [Handler(\"...\")] name (inferred names can collide)",
        category: "Elarion.JsonRpc",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor McpCustomizationIgnored = new(
        id: "ELMCP003",
        title: "MCP customization is ignored",
        messageFormat:
        "Handler '{0}' uses [McpHandler] but its [Handler] transports exclude MCP; remove [McpHandler] or include "
        + "HandlerTransports.Mcp for operation '{1}'",
        category: "Elarion.Mcp",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public sealed record ParameterDescription(string PropertyName, string Description);

    public sealed record Model(
        string MethodName,
        string HandlerNamespace,
        string RequestTypeFqn,
        string ResponseTypeFqn,
        string? ToolName,
        bool OnJsonRpc,
        bool OnMcp,
        string? Description,
        EquatableArray<ParameterDescription> Parameters,
        bool IsNameInferred
    );

    /// <summary>
    /// Emits one statement-style <c>Map</c> registration onto the neutral <paramref name="registryVar"/>
    /// (<c>HandlerDispatcher</c>) using the entry's (already module-resolved) operation name and transport flags.
    /// </summary>
    public static void AppendMapHandler(StringBuilder sb, Model entry, string indent, string registryVar) =>
        sb.AppendLine(
            $"{indent}{registryVar}.Map<{entry.RequestTypeFqn}, {entry.ResponseTypeFqn}>("
            + $"{Literal(entry.MethodName)}, {TransportsExpression(entry)});");

    /// <summary>The fully-qualified <c>HandlerTransports</c> flag expression for an entry's surfaces.</summary>
    public static string TransportsExpression(Model entry)
    {
        const string ns = "global::Elarion.Abstractions.HandlerTransports";
        if (entry.OnJsonRpc && entry.OnMcp)
            return $"{ns}.All";
        if (entry.OnMcp)
            return $"{ns}.Mcp";
        return $"{ns}.JsonRpc";
    }

    public static bool TryCreateModel(
        INamedTypeSymbol type,
        AttributeData attr,
        INamedTypeSymbol? mcpMethodType,
        INamedTypeSymbol? descriptionType,
        SymbolDisplayFormat fmt,
        Action<Diagnostic>? report,
        CancellationToken ct,
        out Model? model)
    {
        model = null;

        // [Handler] has two ctors: the parameterless one (name inferred) and [Handler(string name)] (explicit).
        var explicitName = attr.ConstructorArguments.Length > 0
            ? attr.ConstructorArguments[0].Value as string
            : null;
        var isNameInferred = string.IsNullOrEmpty(explicitName);
        var operationName = isNameInferred ? InferOperationName(type.Name) : explicitName!;

        var (onJsonRpc, onMcp) = ReadTransports(attr);

        ct.ThrowIfCancellationRequested();
        if (!HandlerShape.TryResolve(type, out var requestType, out var responseInner, out _))
        {
            report?.Invoke(Diagnostic.Create(
                MissingHandlerShape, type.Locations.FirstOrDefault() ?? Location.None, type.ToDisplayString()));
            return false;
        }

        var (toolName, hasMcpMethod) = ReadMcpMethod(type, mcpMethodType);
        if (hasMcpMethod && !onMcp)
        {
            report?.Invoke(Diagnostic.Create(
                McpCustomizationIgnored,
                Location.None,
                type.ToDisplayString(),
                operationName));
        }

        var description = GetDescription(type, descriptionType);
        var parameters = CollectParameterDescriptions(requestType, descriptionType);

        model = new Model(
            operationName,
            type.ContainingNamespace?.ToDisplayString() ?? string.Empty,
            requestType.ToDisplayString(fmt),
            responseInner.ToDisplayString(fmt),
            toolName,
            onJsonRpc,
            onMcp,
            description,
            parameters,
            isNameInferred);
        return true;
    }

    /// <summary>
    /// Infers the operation part of a name from a handler type name: strip a trailing
    /// Handler/Command/Query/Request, then camel-case the first character (CreateClient -> createClient).
    /// The caller prepends the owning module (e.g. clients.createClient).
    /// </summary>
    public static string InferOperationName(string typeName)
    {
        var name = typeName;
        foreach (var suffix in OperationNameSuffixes)
        {
            if (name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.Ordinal))
            {
                name = name.Substring(0, name.Length - suffix.Length);
                break;
            }
        }

        if (name.Length == 0)
            name = typeName;

        return char.IsUpper(name[0]) ? char.ToLowerInvariant(name[0]) + name.Substring(1) : name;
    }

    // HandlerTransports flags: JsonRpc = 1, Mcp = 2, All = 3 (default when the named argument is absent).
    private static (bool OnJsonRpc, bool OnMcp) ReadTransports(AttributeData attr)
    {
        var transports = 3;
        foreach (var named in attr.NamedArguments)
        {
            if (named.Key == "Transports" && named.Value.Value is int value)
            {
                transports = value;
                break;
            }
        }

        return ((transports & 1) != 0, (transports & 2) != 0);
    }

    private static (string? ToolName, bool HasMcpMethod) ReadMcpMethod(INamedTypeSymbol type, INamedTypeSymbol? mcpMethodType)
    {
        if (mcpMethodType is null)
            return (null, false);

        foreach (var attr in type.GetAttributes())
        {
            if (!SymbolEqualityComparer.Default.Equals(attr.AttributeClass, mcpMethodType))
                continue;

            foreach (var named in attr.NamedArguments)
            {
                if (named.Key == "ToolName" && named.Value.Value is string name && name.Length > 0)
                    return (name, true);
            }

            return (null, true);
        }

        return (null, false);
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

    private static EquatableArray<ParameterDescription> CollectParameterDescriptions(
        INamedTypeSymbol requestType,
        INamedTypeSymbol? descriptionType)
    {
        if (descriptionType is null)
            return EquatableArray<ParameterDescription>.Empty;

        var byParameterName = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var ctor in requestType.InstanceConstructors)
        {
            if (ctor.Parameters.Length == 1 &&
                SymbolEqualityComparer.Default.Equals(ctor.Parameters[0].Type, requestType))
                continue;

            foreach (var parameter in ctor.Parameters)
            {
                if (GetDescription(parameter, descriptionType) is { } desc)
                    byParameterName[parameter.Name] = desc;
            }
        }

        var result = new List<ParameterDescription>();
        foreach (var member in requestType.GetMembers())
        {
            if (member is not IPropertySymbol property ||
                property.IsStatic ||
                property.IsIndexer ||
                property.DeclaredAccessibility != Accessibility.Public)
                continue;

            var description = GetDescription(property, descriptionType)
                ?? (byParameterName.TryGetValue(property.Name, out var fromParameter) ? fromParameter : null);

            if (description is not null)
                result.Add(new ParameterDescription(property.Name, description));
        }

        return result.ToEquatableArray();
    }

    private static string Literal(string value) => SymbolDisplay.FormatLiteral(value, quote: true);
}
