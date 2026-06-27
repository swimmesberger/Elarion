using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Elarion.JsonRpc;
using Elarion.JsonRpc.Mcp;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Elarion.AspNetCore.Mcp;

/// <summary>
/// An <see cref="McpServerTool"/> that proxies a single registered JSON-RPC method through the live
/// <see cref="JsonRpcDispatcher"/> via <see cref="RpcToolInvoker"/>.
/// </summary>
/// <remarks>
/// The dispatcher is resolved from DI at invocation time so the configured singleton (with logging) is used.
/// <see cref="RpcToolInvoker"/> owns the per-call service scope, so scoped services (e.g. a pooled EF Core
/// <c>DbContext</c>) are managed exactly as on the JSON-RPC endpoint.
/// </remarks>
internal sealed class ElarionMcpServerTool : McpServerTool {
    private readonly string _methodName;
    private readonly bool _includeErrorDetails;

    /// <inheritdoc />
    public override Tool ProtocolTool { get; }

    /// <inheritdoc />
    public override IReadOnlyList<object> Metadata { get; }

    internal ElarionMcpServerTool(string methodName, Tool protocolTool, bool includeErrorDetails) {
        _methodName = methodName;
        ProtocolTool = protocolTool;
        _includeErrorDetails = includeErrorDetails;
        Metadata = [];
    }

    /// <inheritdoc />
    public override async ValueTask<CallToolResult> InvokeAsync(
        RequestContext<CallToolRequestParams> request,
        CancellationToken cancellationToken = default) {
        // request.Services is the application root provider (not request-scoped); RpcToolInvoker scopes per call.
        var services = request.Services
            ?? throw new InvalidOperationException("MCP request has no service provider.");
        var dispatcher = services.GetRequiredService<McpDispatcher>().Inner;
        var jsonOptions = dispatcher.JsonOptions;

        // Convert the MCP argument dictionary into a JSON object for the dispatcher. The document is kept alive
        // for the whole dispatch call (deserialization happens during DispatchAsync), then disposed.
        var arguments = request.Params?.Arguments;
        using var argumentsDocument = arguments is { Count: > 0 }
            ? JsonSerializer.SerializeToDocument(arguments, jsonOptions)
            : null;

        // Capture the authenticated principal (populated by the Streamable-HTTP transport) so the per-call
        // scope's ICurrentUser resolves — RpcToolInvoker scopes from the app root, not the request scope.
        var context = new DispatchScopeContext();
        if (request.User is { } user) {
            context.Set<ClaimsPrincipal>(user);
        }

        var result = await RpcToolInvoker.InvokeAsync(
            dispatcher, _methodName, argumentsDocument?.RootElement, services, context, cancellationToken);

        return ToCallToolResult(result, jsonOptions);
    }

    internal CallToolResult ToCallToolResult(RpcToolResult result, JsonSerializerOptions jsonOptions) {
        if (!result.IsError) {
            return new CallToolResult {
                Content = [new TextContentBlock { Text = result.Text }],
            };
        }

        var callResult = new CallToolResult {
            IsError = true,
            Content = [new TextContentBlock { Text = result.Text }],
        };

        if (_includeErrorDetails) {
            // Build the node manually so no resolver is needed for a wrapper type; the error data is serialized
            // by its runtime type with the dispatcher's options — consistent with the JSON-RPC response path.
            var details = new JsonObject();
            if (result.ErrorCode is { } code) {
                details["code"] = code;
            }

            if (result.ErrorData is { } data) {
                details["data"] = JsonSerializer.SerializeToNode(data, data.GetType(), jsonOptions);
            }

            // CallToolResult.StructuredContent is a JsonElement; clone so it owns its memory after the document is disposed.
            using var detailsDocument = JsonSerializer.SerializeToDocument(details, jsonOptions);
            callResult.StructuredContent = detailsDocument.RootElement.Clone();
        }

        return callResult;
    }
}
