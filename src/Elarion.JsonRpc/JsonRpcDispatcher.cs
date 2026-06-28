using System.Diagnostics;
using System.Text.Json;
using Elarion.Abstractions;
using Elarion.Abstractions.Dispatch;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Elarion.JsonRpc;

/// <summary>
/// The JSON-RPC 2.0 <b>transport adapter</b> over the transport-neutral <see cref="HandlerDispatcher"/> (the named
/// bus). It owns the JSON-RPC concerns — envelope validation, JSON param deserialization, <see cref="Result{T}"/> →
/// <see cref="RpcError"/> mapping, and OpenTelemetry spans (per the
/// <see href="https://opentelemetry.io/docs/specs/semconv/rpc/json-rpc/">JSON-RPC semantic conventions</see>) —
/// and serves only the operations flagged <see cref="HandlerTransports.JsonRpc"/>. Routing and handler invocation
/// (the full decorator pipeline) live in the shared registry, so MCP and other transports reuse the same routes.
/// </summary>
public sealed class JsonRpcDispatcher {
    private readonly HandlerDispatcher _registry;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly ILogger<JsonRpcDispatcher>? _logger;

    /// <summary>
    /// Initializes a dispatcher over its own fresh <see cref="HandlerDispatcher"/> — for manual wiring or tests.
    /// Register operations with <see cref="MapDelegate{TRequest,TResponse}"/> or <see cref="Map{TRequest,TResponse}"/>.
    /// </summary>
    public JsonRpcDispatcher(JsonSerializerOptions jsonOptions, ILogger<JsonRpcDispatcher>? logger = null)
        : this(new HandlerDispatcher(), jsonOptions, logger) {
    }

    /// <summary>Initializes a dispatcher over a shared <see cref="HandlerDispatcher"/> (the host path).</summary>
    public JsonRpcDispatcher(
        HandlerDispatcher registry, JsonSerializerOptions jsonOptions, ILogger<JsonRpcDispatcher>? logger = null) {
        ArgumentNullException.ThrowIfNull(registry);
        _registry = registry;
        _jsonOptions = jsonOptions;
        _logger = logger;
    }

    /// <summary>The serializer options this dispatcher uses for request deserialization and response serialization.</summary>
    public JsonSerializerOptions JsonOptions => _jsonOptions;

    /// <summary>The underlying transport-neutral registry (shared with other transports).</summary>
    public HandlerDispatcher Registry => _registry;

    /// <summary>Registers a DI-resolved handler on the underlying registry (convenience forwarder).</summary>
    public JsonRpcDispatcher Map<TRequest, TResponse>(
        string methodName, HandlerTransports transports = HandlerTransports.All)
        where TRequest : class {
        _registry.Map<TRequest, TResponse>(methodName, transports);
        return this;
    }

    /// <summary>Registers a delegate-backed handler on the underlying registry (convenience forwarder, for tests/manual wiring).</summary>
    public JsonRpcDispatcher MapDelegate<TRequest, TResponse>(
        string methodName,
        Func<TRequest, IServiceProvider, CancellationToken, ValueTask<Result<TResponse>>> handler,
        HandlerTransports transports = HandlerTransports.All)
        where TRequest : class {
        _registry.MapDelegate(methodName, handler, transports);
        return this;
    }

    /// <summary>
    /// Dispatches a single JSON-RPC request to the matching JSON-RPC-flagged route.
    /// Creates a <c>SERVER</c> span with OTel JSON-RPC semantic convention attributes.
    /// </summary>
    public async Task<JsonRpcResponse> DispatchAsync(
        JsonRpcRequest request,
        IServiceProvider scopeProvider,
        CancellationToken ct) {
        var startTimestamp = Stopwatch.GetTimestamp();

        if (request.IsInvalidEnvelope) {
            using var invalidEnvelopeActivity = StartRequestActivity(request, "_invalid");
            RecordError(invalidEnvelopeActivity, "_invalid", "-32600", "Invalid request", "invalid-envelope", startTimestamp);
            return JsonRpcResponse.InvalidRequest(request);
        }

        if (string.IsNullOrWhiteSpace(request.Method)) {
            using var missingMethodActivity = StartRequestActivity(request, "_invalid");
            RecordError(missingMethodActivity, "_invalid", "-32600", "Invalid request", "missing-method", startTimestamp);
            return JsonRpcResponse.InvalidRequest(request);
        }

        if (request.Jsonrpc != "2.0") {
            using var invalidProtocolActivity = StartRequestActivity(request, "_invalid");
            RecordError(invalidProtocolActivity, "_invalid", "-32600", "Invalid request", "invalid-protocol", startTimestamp);
            return JsonRpcResponse.InvalidRequest(request);
        }

        if (!_registry.TryGetRoute(request.Method, HandlerTransports.JsonRpc, out var route)) {
            using var unregisteredMethodActivity = StartRequestActivity(request, "_unregistered");
            RecordError(unregisteredMethodActivity, "_unregistered", "-32601", "Method not found", "method-not-found", startTimestamp);
            return JsonRpcResponse.MethodNotFound(request);
        }

        var method = route.Name;
        using var activity = StartRequestActivity(request, method);

        _logger?.LogDebug("Dispatching JSON-RPC method {Method} (id={Id})", request.Method, request.Id);

        try {
            object? requestObject;
            if (request.Params is { ValueKind: not JsonValueKind.Undefined } paramsElement) {
                requestObject = paramsElement.Deserialize(route.RequestType, _jsonOptions);
            } else {
                requestObject = Activator.CreateInstance(route.RequestType);
            }

            if (requestObject is null) {
                RecordError(activity, method, "-32602", "Invalid params", "invalid-params", startTimestamp);
                return JsonRpcResponse.FromError(request, RpcError.InvalidParams("Could not construct request params"));
            }

            var result = await route.InvokeAsync(requestObject, scopeProvider, ct).ConfigureAwait(false);

            if (result.IsSuccess) {
                _logger?.LogDebug("JSON-RPC {Method} succeeded", request.Method);
                RecordSuccess(activity, method, startTimestamp);
                return JsonRpcResponse.Success(request, result.Value);
            }

            var translator = scopeProvider.GetService<IAppErrorTranslator<RpcError>>() ?? JsonRpcAppErrorTranslator.Default;
            var rpcError = translator.Translate(result.Error);
            var errorCode = rpcError.Code.ToString();
            _logger?.LogWarning(
                "JSON-RPC {Method} returned application error {Code}: {Message}",
                request.Method, rpcError.Code, rpcError.Message);
            RecordError(activity, method, errorCode, rpcError.Message, "application-error", startTimestamp);
            return JsonRpcResponse.FromError(request, rpcError);
        } catch (JsonException ex) {
            _logger?.LogWarning(ex, "JSON-RPC {Method} — invalid params (deserialization failed)", request.Method);
            RecordError(activity, method, "-32602", "Invalid params", "invalid-params", startTimestamp);
            return JsonRpcResponse.FromError(request, RpcError.InvalidParams("Invalid params: could not deserialize"));
        } catch (Exception ex) {
            _logger?.LogError(ex, "Unhandled exception dispatching JSON-RPC method {Method}", request.Method);
            activity?.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection {
                { "exception.type", ex.GetType().FullName },
                { "exception.message", ex.Message },
            }));
            if (activity?.IsAllDataRequested == true) {
                activity.SetTag("rpc.response.status_code", "-32603");
                activity.SetTag("error.type", "-32603");
                activity.SetTag("jsonrpc.error.phase", "dispatch");
                activity.SetStatus(ActivityStatusCode.Error, ex.Message);
            }

            JsonRpcTelemetry.RecordRequest(method, "-32603", Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds);
#if DEBUG
            // In DEBUG builds, include the actual exception message for easier debugging
            return JsonRpcResponse.FromError(request, RpcError.InternalError($"Internal error: {ex.Message}"));
#else
            return JsonRpcResponse.FromError(request, RpcError.InternalError("Internal error"));
#endif
        }
    }

    /// <summary>Freezes the underlying registry; must be called once after all registrations and before dispatch.</summary>
    public JsonRpcDispatcher Freeze() {
        _registry.Freeze();
        return this;
    }

    /// <summary>Returns the JSON-RPC-exposed method names (for diagnostics/schema export).</summary>
    public IReadOnlyCollection<string> MethodNames =>
        _registry.RoutesFor(HandlerTransports.JsonRpc).Select(static route => route.Name).ToArray();

    /// <summary>Returns the request type for a given JSON-RPC method name (for schema export).</summary>
    public Type? GetRequestType(string methodName) =>
        _registry.TryGetRoute(methodName, HandlerTransports.JsonRpc, out var route) ? route.RequestType : null;

    /// <summary>
    /// Returns all JSON-RPC-exposed methods with their request and response types.
    /// Used by schema export to ensure the same methods as the runtime dispatcher.
    /// </summary>
    public IReadOnlyList<(string MethodName, Type RequestType, Type ResponseType)> GetRegisteredMethods() =>
        _registry.RoutesFor(HandlerTransports.JsonRpc)
            .OrderBy(static route => route.Name, StringComparer.Ordinal)
            .Select(static route => (route.Name, route.RequestType, route.ResponseType))
            .ToList();

    internal static void RecordEndpointError(Activity? activity, string method, string statusCode, string description, string phase, long startTimestamp) =>
        RecordEndpointErrorCore(activity, JsonRpcTelemetry.NormalizeMethod(method), statusCode, description, phase, startTimestamp);

    private static void RecordEndpointErrorCore(Activity? activity, string method, string statusCode, string description, string phase, long startTimestamp) {
        if (activity?.IsAllDataRequested == true) {
            activity.SetTag("rpc.system.name", "jsonrpc");
            activity.SetTag("rpc.method", method);
        }

        RecordError(activity, method, statusCode, description, phase, startTimestamp);
    }

    private static Activity? StartRequestActivity(JsonRpcRequest request, string method) {
        var activity = JsonRpcTelemetry.Source.StartActivity(
            $"jsonrpc {method}",
            ActivityKind.Server);
        SetCommonActivityTags(activity, request, method);
        return activity;
    }

    private static void SetCommonActivityTags(Activity? activity, JsonRpcRequest request, string method) {
        if (activity?.IsAllDataRequested != true) {
            return;
        }

        activity.SetTag("rpc.system.name", "jsonrpc");
        activity.SetTag("rpc.method", method);
        activity.SetTag("jsonrpc.protocol.version", request.Jsonrpc == "2.0" ? "2.0" : "_invalid");
        if (request.Id is not null) {
            activity.SetTag("jsonrpc.request.id", request.Id);
        }

        if (request.BatchIndex is { } batchIndex) {
            activity.SetTag("jsonrpc.batch.index", batchIndex);
        }

        if (request.BatchSize is { } batchSize) {
            activity.SetTag("jsonrpc.batch.size", batchSize);
        }
    }

    private static void RecordSuccess(Activity? activity, string method, long startTimestamp) {
        if (activity?.IsAllDataRequested == true) {
            activity.SetTag("rpc.response.status_code", "OK");
            activity.SetTag("jsonrpc.outcome", "success");
        }

        RecordMetrics(method, "OK", startTimestamp);
    }

    private static void RecordError(Activity? activity, string method, string statusCode, string description, string phase, long startTimestamp) {
        if (activity?.IsAllDataRequested == true) {
            activity.SetTag("rpc.response.status_code", statusCode);
            activity.SetTag("jsonrpc.outcome", "error");
            activity.SetTag("jsonrpc.error.phase", phase);
            activity.SetTag("error.type", statusCode);
            activity.SetStatus(ActivityStatusCode.Error, description);
        }

        RecordMetrics(method, statusCode, startTimestamp);
    }

    private static void RecordMetrics(string method, string statusCode, long startTimestamp) {
        var elapsed = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        JsonRpcTelemetry.RecordRequest(method, statusCode, elapsed);
    }
}
