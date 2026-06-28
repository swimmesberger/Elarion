using System.Text.Json;
using Elarion.Abstractions.Dispatch;
using Microsoft.Extensions.DependencyInjection;

namespace Elarion.JsonRpc.Mcp;

/// <summary>
/// The transport-neutral outcome of invoking a JSON-RPC method as a tool. Adapters (e.g. the MCP
/// <c>CallToolResult</c> mapper) project this onto their protocol's result type.
/// </summary>
public readonly record struct RpcToolResult {
    /// <summary>Whether the invocation failed.</summary>
    public required bool IsError { get; init; }

    /// <summary>On success, the JSON-serialized handler result; on error, the error message.</summary>
    public required string Text { get; init; }

    /// <summary>The JSON-RPC error code, when <see cref="IsError"/> is <see langword="true"/>.</summary>
    public int? ErrorCode { get; init; }

    /// <summary>Optional structured error data, when present. Adapters serialize it with the dispatcher's options.</summary>
    public object? ErrorData { get; init; }
}

/// <summary>
/// Invokes a registered JSON-RPC method through a <see cref="JsonRpcDispatcher"/> as a tool call, owning the
/// per-invocation service scope. Centralizing the scope here keeps the invariant out of protocol adapters.
/// </summary>
public static class RpcToolInvoker {
    /// <summary>
    /// Dispatches <paramref name="methodName"/> with the given arguments. A fresh
    /// <see cref="IServiceScope"/> is created from <paramref name="rootServices"/> per call so scoped
    /// services (e.g. a pooled EF Core <c>DbContext</c>) are managed correctly — mirroring the JSON-RPC
    /// endpoint — and every registered <see cref="IDispatchScopeInitializer"/> seeds that scope from
    /// <paramref name="context"/>.
    /// </summary>
    /// <param name="dispatcher">The frozen dispatcher to route through.</param>
    /// <param name="methodName">The JSON-RPC method name to invoke.</param>
    /// <param name="arguments">The method arguments as a JSON object, or <see langword="null"/> for none.</param>
    /// <param name="rootServices">The application root service provider; a per-call scope is created from it.</param>
    /// <param name="context">The values captured at the call boundary (e.g. the authenticated principal), or <see langword="null"/>.</param>
    /// <param name="ct">A cancellation token flowed into the handler.</param>
    public static async Task<RpcToolResult> InvokeAsync(
        JsonRpcDispatcher dispatcher,
        string methodName,
        JsonElement? arguments,
        IServiceProvider rootServices,
        DispatchScopeContext? context = null,
        CancellationToken ct = default) {
        var request = new JsonRpcRequest {
            Jsonrpc = "2.0",
            Method = methodName,
            Params = arguments,
            Id = null,
        };

        await using var scope = rootServices.CreateDispatchScope(context);
        var response = await dispatcher.DispatchAsync(request, scope.ServiceProvider, ct);

        if (response.Error is { } error) {
            return new RpcToolResult {
                IsError = true,
                Text = error.Message,
                ErrorCode = error.Code,
                ErrorData = error.Data,
            };
        }

        // Serialize by the runtime result type so the dispatcher's source-gen contracts apply.
        var text = response.Result is { } result
            ? JsonSerializer.Serialize(result, result.GetType(), dispatcher.JsonOptions)
            : "{}";

        return new RpcToolResult { IsError = false, Text = text };
    }
}
