using System.Text.Json;
using Elarion.Abstractions.Dispatch;
using Elarion.JsonRpc;
using Elarion.JsonRpc.Mcp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Elarion.AspNetCore.Mcp;

/// <summary>
/// Extension methods that expose Elarion <c>[Handler]</c> operations as an MCP server.
/// </summary>
/// <remarks>
/// MCP is an adapter over the shared <see cref="HandlerDispatcher"/> (the named bus): its
/// <see cref="McpDispatcher"/> serves only the operations whose <c>[Handler(Transports = ...)]</c> includes
/// <c>Mcp</c>. This lets a handler be MCP-only (dispatchable here but absent from <c>/rpc</c> and the JSON-RPC
/// schema) or JSON-RPC-only, while a "both" handler is reachable from either surface — all from one registry.
/// <c>MapElarionJsonRpc</c> is never required.
/// </remarks>
public static class ElarionMcpServiceExtensions {
    /// <summary>
    /// Registers an MCP server (Streamable HTTP) whose tools are the MCP-exposed operations in the generated
    /// <paramref name="metadata"/> table, backed by an <see cref="McpDispatcher"/> over the shared
    /// <see cref="HandlerDispatcher"/> built from <paramref name="registerHandlers"/>
    /// (e.g. <c>ElarionBootstrapper.RegisterHandlers</c>).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="metadata">The generated MCP metadata table (e.g. <c>configuration.GetMcpMetadata()</c>).</param>
    /// <param name="serializerOptions">
    /// The same serializer options the dispatcher uses; used here to build tool input schemas and by the MCP
    /// dispatcher for params/result handling.
    /// </param>
    /// <param name="registerHandlers">
    /// The generated registration delegate that maps the operations onto the shared registry and returns it
    /// (e.g. <c>ElarionBootstrapper.RegisterHandlers</c>). Pass the same delegate to <c>AddElarionJsonRpc</c> to
    /// share one registry.
    /// </param>
    /// <param name="configure">Configures the server identity and behavior. <see cref="ElarionMcpOptions.ServerName"/> is required.</param>
    /// <returns>The underlying <see cref="IMcpServerBuilder"/> for further composition (e.g. auth filters).</returns>
    public static IMcpServerBuilder AddElarionMcp(
        this IServiceCollection services,
        IRpcMcpMetadataSource metadata,
        JsonSerializerOptions serializerOptions,
        Func<HandlerDispatcher, HandlerDispatcher> registerHandlers,
        Action<ElarionMcpOptions> configure) {
        ArgumentNullException.ThrowIfNull(serializerOptions);
        ArgumentNullException.ThrowIfNull(registerHandlers);

        services.AddElarionHandlerDispatcher(registerHandlers);
        return services.AddElarionMcpCore(metadata, serializerOptions, configure);
    }

    /// <summary>
    /// Registers an MCP server using a <paramref name="registerHandlers"/> delegate that also receives the
    /// application <see cref="IConfiguration"/> — the setup for a module-based host, where
    /// <c>ElarionBootstrapper.RegisterHandlers</c> maps only the operations of enabled modules. Pair it with
    /// <c>configuration.GetMcpMetadata()</c> for the gated tool table.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="metadata">The (already gated) MCP metadata table, e.g. <c>builder.Configuration.GetMcpMetadata()</c>.</param>
    /// <param name="serializerOptions">The serializer options used for tool input schemas and the MCP dispatcher.</param>
    /// <param name="registerHandlers">The registration delegate (e.g. <c>ElarionBootstrapper.RegisterHandlers</c>).</param>
    /// <param name="configure">Configures the server identity and behavior. <see cref="ElarionMcpOptions.ServerName"/> is required.</param>
    /// <returns>The underlying <see cref="IMcpServerBuilder"/> for further composition (e.g. auth filters).</returns>
    public static IMcpServerBuilder AddElarionMcp(
        this IServiceCollection services,
        IRpcMcpMetadataSource metadata,
        JsonSerializerOptions serializerOptions,
        Func<HandlerDispatcher, IConfiguration, HandlerDispatcher> registerHandlers,
        Action<ElarionMcpOptions> configure) {
        ArgumentNullException.ThrowIfNull(serializerOptions);
        ArgumentNullException.ThrowIfNull(registerHandlers);

        services.AddElarionHandlerDispatcher(
            (dispatcher, sp) => registerHandlers(dispatcher, sp.GetRequiredService<IConfiguration>()));
        return services.AddElarionMcpCore(metadata, serializerOptions, configure);
    }

    private static IMcpServerBuilder AddElarionMcpCore(
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
        services.AddSingleton(sp => new McpDispatcher(sp.GetRequiredService<HandlerDispatcher>(), serializerOptions));

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
                    $"Disambiguate via [McpHandler(ToolName = ...)] or {nameof(ElarionMcpOptions)}.{nameof(ElarionMcpOptions.ToolNameTransform)}.");
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
