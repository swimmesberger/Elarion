using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Elarion.Generators;

/// <summary>
/// Shared discovery for <c>[Elarion.Abstractions.RpcMethod]</c> handlers (including MCP metadata) and statement-style
/// <c>MapHandler</c> emission, consumed by <see cref="AppModuleDiscoveryGenerator"/> for the module-grouped,
/// feature-flag-gated registration (the only transport-wiring path).
/// </summary>
internal static class RpcMethodEmission
{
    public const string RpcMethodAttributeMetadataName = "Elarion.Abstractions.RpcMethodAttribute";
    private const string McpMethodAttributeMetadataName = "Elarion.Abstractions.McpMethodAttribute";
    private const string DescriptionAttributeMetadataName = "System.ComponentModel.DescriptionAttribute";

    public static readonly DiagnosticDescriptor UnmatchedModule = new(
        id: "ELRPC001",
        title: "RPC method handler is not in any module",
        messageFormat:
        "Handler '{0}' is annotated with [RpcMethod] but its namespace is not under any [AppModule]; it will be "
        + "registered unconditionally (not gated by a module feature flag)",
        category: "Elarion.JsonRpc",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor McpCustomizationIgnored = new(
        id: "ELMCP003",
        title: "MCP customization is ignored",
        messageFormat:
        "Handler '{0}' uses [McpMethod] but its [RpcMethod] transports exclude MCP; remove [McpMethod] or include "
        + "RpcTransports.Mcp for method '{1}'",
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
        IReadOnlyList<ParameterDescription> Parameters
    );

    /// <summary>Emits a single statement-style <c>MapHandler</c> registration onto <paramref name="dispatcherVar"/>.</summary>
    public static void AppendMapHandler(StringBuilder sb, Model entry, string indent, string dispatcherVar) =>
        sb.AppendLine(
            $"{indent}{dispatcherVar}.MapHandler<{entry.RequestTypeFqn}, {entry.ResponseTypeFqn}>({Literal(entry.MethodName)});");

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

        if (attr.ConstructorArguments.Length == 0)
            return false;

        if (attr.ConstructorArguments[0].Value is not string methodName)
            return false;

        var (onJsonRpc, onMcp) = ReadTransports(attr);

        INamedTypeSymbol? requestType = null;
        INamedTypeSymbol? responseType = null;

        foreach (var member in type.GetTypeMembers())
        {
            ct.ThrowIfCancellationRequested();

            if (member.Name is "Command" or "Query")
                requestType = member;
            else if (member.Name == "Response")
                responseType = member;
        }

        if (requestType is null || responseType is null)
            return false;

        var (toolName, hasMcpMethod) = ReadMcpMethod(type, mcpMethodType);
        if (hasMcpMethod && !onMcp)
        {
            report?.Invoke(Diagnostic.Create(
                McpCustomizationIgnored,
                Location.None,
                type.ToDisplayString(),
                methodName));
        }

        var description = GetDescription(type, descriptionType);
        var parameters = CollectParameterDescriptions(requestType, descriptionType);

        model = new Model(
            methodName,
            type.ContainingNamespace?.ToDisplayString() ?? string.Empty,
            requestType.ToDisplayString(fmt),
            responseType.ToDisplayString(fmt),
            toolName,
            onJsonRpc,
            onMcp,
            description,
            parameters);
        return true;
    }

    // RpcTransports flags: JsonRpc = 1, Mcp = 2, All = 3 (default when the named argument is absent).
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

    private static IReadOnlyList<ParameterDescription> CollectParameterDescriptions(
        INamedTypeSymbol requestType,
        INamedTypeSymbol? descriptionType)
    {
        if (descriptionType is null)
            return [];

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

        return result;
    }

    private static string Literal(string value) => SymbolDisplay.FormatLiteral(value, quote: true);
}
