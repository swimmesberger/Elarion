using System.Text.Json;
using Elarion.Abstractions;
using Elarion.Abstractions.Dispatch;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Elarion.JsonRpc.Mcp;

/// <summary>
/// The transport-neutral outcome of invoking a handler operation as a tool. Adapters (e.g. the MCP
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
/// Invokes a registered operation through the shared <see cref="HandlerDispatcher"/> as a tool call, owning the
/// per-invocation service scope. Centralizing the scope here keeps the invariant out of protocol adapters.
/// </summary>
public static class RpcToolInvoker {
    /// <summary>
    /// Dispatches <paramref name="methodName"/> (restricted to <paramref name="transport"/>) with the given
    /// arguments. A fresh <see cref="IServiceScope"/> is created from <paramref name="rootServices"/> per call so
    /// scoped services (e.g. a pooled EF Core <c>DbContext</c>) are managed correctly, and every registered
    /// <see cref="IDispatchScopeInitializer"/> seeds that scope from <paramref name="context"/>.
    /// </summary>
    /// <param name="dispatcher">The shared registry to route through.</param>
    /// <param name="transport">The transport surface to restrict routing to (e.g. <see cref="HandlerTransports.Mcp"/>).</param>
    /// <param name="methodName">The operation name to invoke.</param>
    /// <param name="arguments">The operation arguments as a JSON object, or <see langword="null"/> for none.</param>
    /// <param name="rootServices">The application root service provider; a per-call scope is created from it.</param>
    /// <param name="serializerOptions">The options used to (de)serialize arguments and the result.</param>
    /// <param name="context">The values captured at the call boundary (e.g. the authenticated principal), or <see langword="null"/>.</param>
    /// <param name="ct">A cancellation token flowed into the handler.</param>
    /// <remarks>
    /// Unexpected handler exceptions are logged through the host's <see cref="ILoggerFactory"/> (resolved from
    /// <paramref name="rootServices"/>, like the invoker's other collaborators) before being mapped to a JSON-RPC
    /// internal error — never propagated raw to the transport SDK.
    /// </remarks>
    public static async Task<RpcToolResult> InvokeAsync(
        HandlerDispatcher dispatcher,
        HandlerTransports transport,
        string methodName,
        JsonElement? arguments,
        IServiceProvider rootServices,
        JsonSerializerOptions serializerOptions,
        DispatchScopeContext? context = null,
        CancellationToken ct = default) {
        if (!dispatcher.TryGetRoute(methodName, transport, out var route)) {
            return new RpcToolResult { IsError = true, Text = $"Method not found: {methodName}", ErrorCode = -32601 };
        }

        await using var scope = rootServices.CreateDispatchScope(context);

        object? requestObject;
        try {
            requestObject = arguments is { ValueKind: not JsonValueKind.Undefined } args
                ? args.Deserialize(serializerOptions.GetTypeInfo(route.RequestType))
                : Activator.CreateInstance(route.RequestType);
        } catch (JsonException ex) {
            return new RpcToolResult { IsError = true, Text = $"Invalid params: {ex.Message}", ErrorCode = -32602 };
        }

        if (requestObject is null) {
            return new RpcToolResult { IsError = true, Text = "Could not construct request params", ErrorCode = -32602 };
        }

        // Per-call idempotency key from the tool arguments' _meta (the batch-correct, transport-neutral location).
        if (route.Idempotent && JsonRpcDispatcher.TryReadMetaIdempotencyKey(arguments, out var idempotencyKey)) {
            scope.ServiceProvider.GetService<Elarion.Abstractions.Idempotency.IIdempotencyKeySeed>()?.Seed(idempotencyKey);
        }

        var logger = rootServices.GetService<ILoggerFactory>()?.CreateLogger(typeof(RpcToolInvoker));

        Result<object> result;
        try {
            result = await route.InvokeAsync(requestObject, scope.ServiceProvider, ct).ConfigureAwait(false);
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            // Expected cancellation (e.g. the client disconnected). Rethrow so the transport bails quietly; do
            // not log it as an error or map it to a fabricated internal error.
            logger?.LogDebug("Tool {Method} canceled by the caller", methodName);
            throw;
        } catch (Exception ex) {
            // An unexpected handler exception must not propagate raw to the MCP SDK. Log it and map it to a
            // JSON-RPC internal error (-32603), consistent with JsonRpcDispatcher's unexpected-exception path.
            logger?.LogError(ex, "Unhandled exception invoking tool {Method}", methodName);
            return new RpcToolResult { IsError = true, Text = "Internal error", ErrorCode = -32603 };
        }

        if (!result.IsSuccess) {
            var translator = scope.ServiceProvider.GetService<IAppErrorTranslator<RpcError>>()
                ?? JsonRpcAppErrorTranslator.Default;
            var rpcError = translator.Translate(result.Error);
            return new RpcToolResult {
                IsError = true,
                Text = rpcError.Message,
                ErrorCode = rpcError.Code,
                ErrorData = rpcError.Data,
            };
        }

        // Serialize by the runtime result type, resolving its contract through the configured (source-gen)
        // resolver so this stays reflection-free and Native-AOT-safe.
        var text = result.Value is { } value
            ? JsonSerializer.Serialize(value, serializerOptions.GetTypeInfo(value.GetType()))
            : "{}";

        return new RpcToolResult { IsError = false, Text = text };
    }
}
