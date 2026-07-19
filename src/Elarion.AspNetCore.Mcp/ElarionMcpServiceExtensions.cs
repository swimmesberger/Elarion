using System.Text.Json;
using Elarion.Abstractions.Dispatch;
using Elarion.Abstractions.Serialization;
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
        Func<HandlerDispatcher, HandlerDispatcher> registerHandlers,
        Action<ElarionMcpOptions> configure) {
        ArgumentNullException.ThrowIfNull(registerHandlers);

        services.AddElarionHandlerDispatcher(registerHandlers);
        return services.AddElarionMcpCore(metadata, configure);
    }

    /// <summary>
    /// Registers an MCP server using a <paramref name="registerHandlers"/> delegate that also receives the
    /// application <see cref="IConfiguration"/> — the setup for a module-based host, where
    /// <c>ElarionBootstrapper.RegisterHandlers</c> maps only the operations of enabled modules. Pair it with
    /// <c>configuration.GetMcpMetadata()</c> for the gated tool table.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="metadata">The (already gated) MCP metadata table, e.g. <c>builder.Configuration.GetMcpMetadata()</c>.</param>
    /// <param name="registerHandlers">The registration delegate (e.g. <c>ElarionBootstrapper.RegisterHandlers</c>).</param>
    /// <param name="configure">Configures the server identity and behavior. <see cref="ElarionMcpOptions.ServerName"/> is required.</param>
    /// <returns>The underlying <see cref="IMcpServerBuilder"/> for further composition (e.g. auth filters).</returns>
    public static IMcpServerBuilder AddElarionMcp(
        this IServiceCollection services,
        IRpcMcpMetadataSource metadata,
        Func<HandlerDispatcher, IConfiguration, HandlerDispatcher> registerHandlers,
        Action<ElarionMcpOptions> configure) {
        ArgumentNullException.ThrowIfNull(registerHandlers);

        services.AddElarionHandlerDispatcher((dispatcher, sp) =>
            registerHandlers(dispatcher, sp.GetRequiredService<IConfiguration>()));
        return services.AddElarionMcpCore(metadata, configure);
    }

    private static IMcpServerBuilder AddElarionMcpCore(
        this IServiceCollection services,
        IRpcMcpMetadataSource metadata,
        Action<ElarionMcpOptions> configure) {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new ElarionMcpOptions();
        configure(options);
        if (string.IsNullOrWhiteSpace(options.ServerName))
            throw new InvalidOperationException(
                $"{nameof(ElarionMcpOptions)}.{nameof(ElarionMcpOptions.ServerName)} must be set.");

        services.AddElarionJson();
        services.AddSingleton(options);
        services.AddSingleton(sp => new McpDispatcher(
            sp.GetRequiredService<HandlerDispatcher>(),
            sp.GetRequiredService<IElarionJsonSerialization>().Options));

        var builder = services
            .AddMcpServer(server => server.ServerInfo = new Implementation {
                Name = options.ServerName,
                Version = options.ServerVersion
            })
            .WithHttpTransport();

        // Tool input schemas are built lazily from the canonical serializer options so they see every JSON
        // contributor (modules, the JSON-RPC envelope context) — which are only fully composed once the container
        // is built. Duplicate tool names are still detected eagerly here.
        var toolNamesSeen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var method in metadata.All) {
            var toolName = method.ToolName is { Length: > 0 } overridden
                ? overridden
                : options.ToolNameTransform(method.MethodName);
            if (!toolNamesSeen.TryAdd(toolName, method.MethodName))
                throw new InvalidOperationException(
                    $"MCP tool name '{toolName}' is produced by both '{toolNamesSeen[toolName]}' and '{method.MethodName}'. " +
                    $"Disambiguate via [McpHandler(ToolName = ...)] or {nameof(ElarionMcpOptions)}.{nameof(ElarionMcpOptions.ToolNameTransform)}.");

            var capturedMethod = method;
            var capturedName = toolName;
            services.AddSingleton<McpServerTool>(sp =>
                BuildTool(capturedMethod, capturedName, sp.GetRequiredService<IElarionJsonSerialization>().Options,
                    options));
        }

        return builder;
    }

    /// <summary>
    /// Maps the MCP Streamable-HTTP endpoint at <see cref="ElarionMcpOptions.EndpointPath"/>. Returns the
    /// endpoint builder so the consumer can chain conventions (e.g. <c>RequireAuthorization()</c>).
    /// </summary>
    public static IEndpointConventionBuilder MapElarionMcp(this IEndpointRouteBuilder app) {
        var options = app.ServiceProvider.GetRequiredService<ElarionMcpOptions>();
        return app.MapMcp(options.EndpointPath);
    }

    /// <summary>Builds a single MCP tool (name + input schema) for one operation.</summary>
    internal static McpServerTool BuildTool(
        RpcMcpMethodMetadata method,
        string toolName,
        JsonSerializerOptions serializerOptions,
        ElarionMcpOptions options) {
        var inputSchema = RpcMcpInputSchema.Build(method.RequestType, serializerOptions, method.Parameters);

        var protocolTool = new Tool {
            Name = toolName,
            Description = string.IsNullOrEmpty(method.Description) ? null : method.Description,
            InputSchema = inputSchema
        };

        return new ElarionMcpServerTool(method.MethodName, protocolTool, options.IncludeErrorDetails);
    }

    /// <summary>
    /// Builds the full MCP tool list eagerly with the given serializer options. The DI registration path defers this
    /// per tool (see <c>AddElarionMcpCore</c>); this overload stays for schema export and tests.
    /// </summary>
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
            if (!toolNamesSeen.TryAdd(toolName, method.MethodName))
                throw new InvalidOperationException(
                    $"MCP tool name '{toolName}' is produced by both '{toolNamesSeen[toolName]}' and '{method.MethodName}'. " +
                    $"Disambiguate via [McpHandler(ToolName = ...)] or {nameof(ElarionMcpOptions)}.{nameof(ElarionMcpOptions.ToolNameTransform)}.");

            tools.Add(BuildTool(method, toolName, serializerOptions, options));
        }

        return tools;
    }
}
