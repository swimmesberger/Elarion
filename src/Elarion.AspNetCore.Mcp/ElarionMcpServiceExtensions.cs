using System.Text.Json;
using Elarion.JsonRpc;
using Elarion.JsonRpc.Mcp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Elarion.AspNetCore.Mcp;

/// <summary>
/// Extension methods that expose an Elarion JSON-RPC dispatcher's handlers as an MCP server.
/// </summary>
/// <remarks>
/// The MCP surface is independent of the JSON-RPC HTTP endpoint: it shares only the <see cref="JsonRpcDispatcher"/>
/// singleton (registered via <see cref="JsonRpcDispatcherServiceExtensions.AddElarionJsonRpcDispatcher"/>) and never
/// requires <c>MapJsonRpc</c> to be called. Tools are built from the generated metadata table alone, so the
/// dispatcher is only touched at invocation time (resolved from DI).
/// </remarks>
public static class ElarionMcpServiceExtensions {
    /// <summary>
    /// Registers an MCP server (Streamable HTTP) whose tools are the methods in the generated
    /// <paramref name="metadata"/> table (excluding any marked <c>[McpMethod(Enabled = false)]</c>).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="metadata">The generated MCP metadata table (e.g. <c>RpcMethodMap.McpMetadata()</c>).</param>
    /// <param name="serializerOptions">
    /// The same serializer options the dispatcher uses; used here to build tool input schemas (property naming,
    /// type resolution) consistently with runtime (de)serialization.
    /// </param>
    /// <param name="configure">Configures the server identity and behavior. <see cref="ElarionMcpOptions.ServerName"/> is required.</param>
    /// <returns>The underlying <see cref="IMcpServerBuilder"/> for further composition (e.g. auth filters).</returns>
    public static IMcpServerBuilder AddElarionMcp(
        this IServiceCollection services,
        IRpcMcpMetadataSource metadata,
        JsonSerializerOptions serializerOptions,
        Action<ElarionMcpOptions> configure) {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(serializerOptions);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new ElarionMcpOptions();
        configure(options);
        if (string.IsNullOrWhiteSpace(options.ServerName)) {
            throw new InvalidOperationException(
                $"{nameof(ElarionMcpOptions)}.{nameof(ElarionMcpOptions.ServerName)} must be set.");
        }

        services.AddSingleton(options);

        var tools = BuildTools(metadata, serializerOptions, options);

        return services
            .AddMcpServer(server => server.ServerInfo = new Implementation {
                Name = options.ServerName,
                Version = options.ServerVersion,
            })
            .WithHttpTransport()
            .WithTools(tools.AsEnumerable());
    }

    /// <summary>
    /// Maps the MCP Streamable-HTTP endpoint at <see cref="ElarionMcpOptions.EndpointPath"/>. Returns the
    /// endpoint builder so the consumer can chain conventions (e.g. <c>RequireAuthorization()</c>).
    /// </summary>
    public static IEndpointConventionBuilder MapElarionMcp(this IEndpointRouteBuilder app) {
        var options = app.ServiceProvider.GetRequiredService<ElarionMcpOptions>();
        return app.MapMcp(options.EndpointPath);
    }

    internal static IReadOnlyList<McpServerTool> BuildTools(
        IRpcMcpMetadataSource metadata,
        JsonSerializerOptions serializerOptions,
        ElarionMcpOptions options) {
        var tools = new List<McpServerTool>();
        var toolNamesSeen = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var method in metadata.All) {
            if (!method.Enabled) {
                continue; // [McpMethod(Enabled = false)] keeps the method on JSON-RPC but off the MCP surface.
            }

            var toolName = method.ToolName is { Length: > 0 } overridden
                ? overridden
                : options.ToolNameTransform(method.MethodName);

            // Fail fast on a duplicate tool name (e.g. "a.b" and "a_b" both → "a_b" under the default transform)
            // rather than letting it surface as a confusing MCP handshake error.
            if (!toolNamesSeen.TryAdd(toolName, method.MethodName)) {
                throw new InvalidOperationException(
                    $"MCP tool name '{toolName}' is produced by both '{toolNamesSeen[toolName]}' and '{method.MethodName}'. " +
                    $"Disambiguate via [McpMethod(ToolName = ...)] or {nameof(ElarionMcpOptions)}.{nameof(ElarionMcpOptions.ToolNameTransform)}.");
            }

            var inputSchema = RpcMcpInputSchema.Build(method.RequestType, serializerOptions, method.Parameters);

            var protocolTool = new Tool {
                Name = toolName,
                Description = string.IsNullOrEmpty(method.Description) ? null : method.Description,
                InputSchema = inputSchema,
            };

            tools.Add(new ElarionMcpServerTool(method.MethodName, protocolTool, options.IncludeErrorDetails));
        }

        return tools;
    }
}
