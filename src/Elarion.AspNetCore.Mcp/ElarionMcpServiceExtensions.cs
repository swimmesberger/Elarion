using System.Text.Json;
using Elarion.JsonRpc;
using Elarion.JsonRpc.Mcp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Elarion.AspNetCore.Mcp;

/// <summary>
/// Extension methods that expose Elarion <c>[RpcMethod]</c> handlers as an MCP server.
/// </summary>
/// <remarks>
/// The MCP surface is independent of the JSON-RPC HTTP endpoint: it owns a dedicated
/// <see cref="McpDispatcher"/> (a separate <see cref="JsonRpcDispatcher"/> instance) built from the methods whose
/// <c>[RpcMethod(Transports = ...)]</c> includes <c>Mcp</c>. This lets a handler be MCP-only (dispatchable here but
/// absent from <c>/rpc</c> and the JSON-RPC schema) or JSON-RPC-only, while a handler on both surfaces is
/// registered in both dispatchers. <c>MapJsonRpc</c> is never required.
/// </remarks>
public static class ElarionMcpServiceExtensions {
    /// <summary>
    /// Registers an MCP server (Streamable HTTP) whose tools are the MCP-exposed methods in the generated
    /// <paramref name="metadata"/> table, backed by a dedicated <see cref="McpDispatcher"/> built from
    /// <paramref name="registerMcp"/> (e.g. <c>ModuleBootstrapper.RegisterMcpMethods</c>).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="metadata">The generated MCP metadata table (e.g. <c>configuration.GetMcpMetadata()</c>).</param>
    /// <param name="serializerOptions">
    /// The same serializer options the dispatcher uses; used here to build tool input schemas and by the MCP
    /// dispatcher for params/result handling.
    /// </param>
    /// <param name="registerMcp">
    /// The generated MCP registration delegate that maps the MCP-exposed handlers onto the dispatcher and returns it
    /// (e.g. <c>ModuleBootstrapper.RegisterMcpMethods</c>).
    /// </param>
    /// <param name="configure">Configures the server identity and behavior. <see cref="ElarionMcpOptions.ServerName"/> is required.</param>
    /// <returns>The underlying <see cref="IMcpServerBuilder"/> for further composition (e.g. auth filters).</returns>
    public static IMcpServerBuilder AddElarionMcp(
        this IServiceCollection services,
        IRpcMcpMetadataSource metadata,
        JsonSerializerOptions serializerOptions,
        Func<JsonRpcDispatcher, JsonRpcDispatcher> registerMcp,
        Action<ElarionMcpOptions> configure) {
        ArgumentNullException.ThrowIfNull(serializerOptions);
        ArgumentNullException.ThrowIfNull(registerMcp);

        return services.AddElarionMcpCore(
            metadata,
            serializerOptions,
            sp => new McpDispatcher(
                registerMcp(new JsonRpcDispatcher(serializerOptions, sp.GetService<ILogger<JsonRpcDispatcher>>()))
                    .Freeze()),
            configure);
    }

    /// <summary>
    /// Registers an MCP server using a <paramref name="registerMcp"/> delegate that also receives the application
    /// <see cref="IConfiguration"/> — the setup for a module-based host, where
    /// <c>ModuleBootstrapper.RegisterMcpMethods</c> maps only the MCP-exposed methods of enabled modules. Pair it
    /// with <c>configuration.GetMcpMetadata()</c> for the gated tool table.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="metadata">The (already gated) MCP metadata table, e.g. <c>builder.Configuration.GetMcpMetadata()</c>.</param>
    /// <param name="serializerOptions">The serializer options used for tool input schemas and the MCP dispatcher.</param>
    /// <param name="registerMcp">The registration delegate (e.g. <c>ModuleBootstrapper.RegisterMcpMethods</c>).</param>
    /// <param name="configure">Configures the server identity and behavior. <see cref="ElarionMcpOptions.ServerName"/> is required.</param>
    /// <returns>The underlying <see cref="IMcpServerBuilder"/> for further composition (e.g. auth filters).</returns>
    public static IMcpServerBuilder AddElarionMcp(
        this IServiceCollection services,
        IRpcMcpMetadataSource metadata,
        JsonSerializerOptions serializerOptions,
        Func<JsonRpcDispatcher, IConfiguration, JsonRpcDispatcher> registerMcp,
        Action<ElarionMcpOptions> configure) {
        ArgumentNullException.ThrowIfNull(serializerOptions);
        ArgumentNullException.ThrowIfNull(registerMcp);

        return services.AddElarionMcpCore(
            metadata,
            serializerOptions,
            sp => new McpDispatcher(
                registerMcp(
                        new JsonRpcDispatcher(serializerOptions, sp.GetService<ILogger<JsonRpcDispatcher>>()),
                        sp.GetRequiredService<IConfiguration>())
                    .Freeze()),
            configure);
    }

    private static IMcpServerBuilder AddElarionMcpCore(
        this IServiceCollection services,
        IRpcMcpMetadataSource metadata,
        JsonSerializerOptions serializerOptions,
        Func<IServiceProvider, McpDispatcher> dispatcherFactory,
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
        services.AddSingleton(dispatcherFactory);

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
