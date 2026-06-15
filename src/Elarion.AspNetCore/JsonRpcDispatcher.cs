using System.Collections.Frozen;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Elarion.AspNetCore;

/// <summary>
/// Dispatches incoming JSON-RPC 2.0 requests to registered method handlers.
/// Methods are registered via <see cref="Map{TRequest,TResponse}"/> with a delegate — fully
/// handler-framework agnostic and AOT-compatible.
/// Each dispatch creates an OpenTelemetry span following the
/// <see href="https://opentelemetry.io/docs/specs/semconv/rpc/json-rpc/">JSON-RPC semantic conventions</see>.
/// </summary>
/// <remarks>
/// The registration pattern mirrors the approach used by the MCP C# SDK's internal
/// <c>RequestHandlers</c>: a dictionary of method name → typed delegate, with STJ
/// deserialization/serialization handled at the boundary.
/// </remarks>
public sealed class JsonRpcDispatcher {
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly ILogger<JsonRpcDispatcher>? _logger;
    private readonly Dictionary<string, RpcMethodEntry> _building;
    private FrozenDictionary<string, RpcMethodEntry>? _frozen;

    /// <summary>Initializes the dispatcher with the JSON serializer options used for param deserialization.</summary>
    public JsonRpcDispatcher(JsonSerializerOptions jsonOptions, ILogger<JsonRpcDispatcher>? logger = null) {
        _jsonOptions = jsonOptions;
        // Mutable during registration; replaced with a FrozenDictionary by Freeze().
        _building = new Dictionary<string, RpcMethodEntry>(StringComparer.OrdinalIgnoreCase);
        _logger = logger;
    }

    public JsonRpcDispatcher(JsonRpcDispatcher other, ILogger<JsonRpcDispatcher>? logger = null) {
        _jsonOptions = other._jsonOptions;
        if (other._frozen is not null) {
            _frozen = other._frozen;
            _building = new Dictionary<string, RpcMethodEntry>(StringComparer.OrdinalIgnoreCase);
        } else {
            _building = new Dictionary<string, RpcMethodEntry>(other._building, StringComparer.OrdinalIgnoreCase);
        }
        _logger = logger ?? other._logger;
    }

    /// <summary>The serializer options this dispatcher uses for request deserialization and response serialization.</summary>
    public JsonSerializerOptions JsonOptions => _jsonOptions;

    private FrozenDictionary<string, RpcMethodEntry> Methods =>
        _frozen ?? throw new InvalidOperationException("Call Freeze() after registering all methods.");

    /// <summary>
    /// Registers a handler for the given JSON-RPC method name.
    /// The handler receives the deserialized request, a scoped <see cref="IServiceProvider"/>,
    /// and a <see cref="CancellationToken"/>, and returns a typed <see cref="RpcResult{T}"/>.
    /// The response type <typeparamref name="TResponse"/> is captured for schema export — no
    /// manual <c>responseType</c> parameter needed (mirrors ASP.NET Core TypedResults pattern).
    /// </summary>
    /// <typeparam name="TRequest">The request type deserialized from JSON-RPC params.</typeparam>
    /// <typeparam name="TResponse">The handler's success value type, captured for schema generation.</typeparam>
    /// <param name="methodName">The JSON-RPC method name (e.g., <c>"clients.create"</c>).</param>
    /// <param name="handler">The async handler delegate returning a typed result.</param>
    /// <returns>This dispatcher for fluent chaining.</returns>
    public JsonRpcDispatcher Map<TRequest, TResponse>(
        string methodName,
        Func<TRequest, IServiceProvider, CancellationToken, Task<RpcResult<TResponse>>> handler
    ) where TRequest : class {
        if (_frozen is not null) {
            throw new InvalidOperationException("Cannot register new methods after Freeze() has been called.");
        }
        _building[methodName] = new RpcMethodEntry(
            methodName,
            typeof(TRequest),
            typeof(TResponse),
            async (jsonParams, sp, ct) => {
                TRequest? request;
                if (jsonParams.HasValue && jsonParams.Value.ValueKind != JsonValueKind.Undefined) {
                    request = jsonParams.Value.Deserialize<TRequest>(_jsonOptions);
                } else {
                    request = Activator.CreateInstance<TRequest>();
                }

                if (request is null) {
                    return RpcResult.Failure(RpcError.InvalidParams("Could not construct request params"));
                }

                var typedResult = await handler(request, sp, ct);
                return typedResult.ToUntyped();
            }
        );

        return this;
    }

    /// <summary>
    /// Dispatches a single JSON-RPC request to the matching handler.
    /// Creates a <c>SERVER</c> span with OTel JSON-RPC semantic convention attributes.
    /// </summary>
    public async Task<JsonRpcResponse> DispatchAsync(
        JsonRpcRequest request,
        IServiceProvider scopeProvider,
        CancellationToken ct) {
        if (request.Jsonrpc != "2.0") {
            return JsonRpcResponse.InvalidRequest(request.Id);
        }

        if (!Methods.TryGetValue(request.Method, out var entry)) {
            return JsonRpcResponse.MethodNotFound(request.Id);
        }

        using var activity = JsonRpcTelemetry.Source.StartActivity(
            $"jsonrpc {request.Method}",
            ActivityKind.Server);

        if (activity?.IsAllDataRequested == true) {
            activity.SetTag("rpc.system.name", "jsonrpc");
            activity.SetTag("rpc.method", request.Method);
            activity.SetTag("jsonrpc.protocol.version", "2.0");

            if (request.Id is not null) {
                activity.SetTag("jsonrpc.request.id", request.Id);
            }
        }

        var startTimestamp = Stopwatch.GetTimestamp();

        _logger?.LogDebug("Dispatching JSON-RPC method {Method} (id={Id})", request.Method, request.Id);

        try {
            var result = await entry.InvokeAsync(request.Params, scopeProvider, ct);

            if (result.IsSuccess) {
                _logger?.LogDebug("JSON-RPC {Method} succeeded", request.Method);
                RecordMetrics(request.Method, "OK", startTimestamp);
                return JsonRpcResponse.Success(request.Id, result.Value);
            }

            var errorCode = result.Error.Code.ToString();
            _logger?.LogWarning(
                "JSON-RPC {Method} returned application error {Code}: {Message}",
                request.Method, result.Error.Code, result.Error.Message);
            if (activity?.IsAllDataRequested == true) {
                activity.SetTag("rpc.response.status_code", errorCode);
                activity.SetTag("error.type", errorCode);
                activity.SetStatus(ActivityStatusCode.Error, result.Error.Message);
            }

            RecordMetrics(request.Method, errorCode, startTimestamp);
            return JsonRpcResponse.FromError(request.Id, result.Error);
        } catch (JsonException ex) {
            _logger?.LogWarning(ex, "JSON-RPC {Method} — invalid params (deserialization failed)", request.Method);
            if (activity?.IsAllDataRequested == true) {
                activity.SetTag("rpc.response.status_code", "-32602");
                activity.SetTag("error.type", "-32602");
                activity.SetStatus(ActivityStatusCode.Error, "Invalid params");
            }

            RecordMetrics(request.Method, "-32602", startTimestamp);
            return JsonRpcResponse.FromError(request.Id, RpcError.InvalidParams("Invalid params: could not deserialize"));
        } catch (Exception ex) {
            _logger?.LogError(ex, "Unhandled exception dispatching JSON-RPC method {Method}", request.Method);
            activity?.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection {
                { "exception.type", ex.GetType().FullName },
                { "exception.message", ex.Message },
            }));
            if (activity?.IsAllDataRequested == true) {
                activity.SetTag("rpc.response.status_code", "-32603");
                activity.SetTag("error.type", ex.GetType().FullName ?? "_OTHER");
                activity.SetStatus(ActivityStatusCode.Error, ex.Message);
            }

            RecordMetrics(request.Method, "-32603", startTimestamp);
#if DEBUG
            // In DEBUG builds, include the actual exception message for easier debugging
            return JsonRpcResponse.FromError(request.Id, RpcError.InternalError($"Internal error: {ex.Message}"));
#else
            return JsonRpcResponse.FromError(request.Id, RpcError.InternalError("Internal error"));
#endif
        }
    }

    /// <summary>
    /// Seals the method registry into a <see cref="FrozenDictionary{TKey,TValue}"/> for
    /// faster lookups at dispatch time. Must be called once after all <see cref="Map{TRequest,TResponse}"/>
    /// registrations are complete, before the dispatcher handles any requests.
    /// </summary>
    public JsonRpcDispatcher Freeze() {
        _frozen = _building.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        _building.Clear(); // free memory, prevent further registrations
        _building.TrimExcess();
        return this;
    }

    /// <summary>Returns the set of registered RPC method names (for diagnostics/schema export).</summary>
    public IReadOnlyCollection<string> MethodNames => Methods.Keys;

    /// <summary>Returns the request type for a given RPC method name (for schema export).</summary>
    public Type? GetRequestType(string methodName) =>
        Methods.TryGetValue(methodName, out var entry) ? entry.RequestType : null;

    /// <summary>
    /// Returns all registered RPC methods with their request and response types.
    /// Used by schema export to ensure the same methods as the runtime dispatcher.
    /// </summary>
    public IReadOnlyList<(string MethodName, Type RequestType, Type ResponseType)> GetRegisteredMethods() =>
        Methods
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => (kv.Key, kv.Value.RequestType, kv.Value.ResponseType))
            .ToList();

    private static void RecordMetrics(string method, string statusCode, long startTimestamp) {
        var elapsed = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        var tags = new TagList {
            { "rpc.method", method },
            { "rpc.response.status_code", statusCode }
        };
        JsonRpcTelemetry.RequestCount.Add(1, tags);
        JsonRpcTelemetry.RequestDuration.Record(elapsed, tags);
    }

    /// <summary>Encapsulates the typed invocation logic for a single RPC method.</summary>
    private sealed record RpcMethodEntry(
        string MethodName,
        Type RequestType,
        Type ResponseType,
        Func<JsonElement?, IServiceProvider, CancellationToken, Task<RpcResult>> InvokeAsync);
}
