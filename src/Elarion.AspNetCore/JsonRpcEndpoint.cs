using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using Elarion.Abstractions.Dispatch;
using Elarion.JsonRpc;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Elarion.AspNetCore;

/// <summary>
/// ASP.NET Core Minimal API endpoint that handles JSON-RPC 2.0 requests.
/// Supports both single requests and batch arrays per the spec.
/// </summary>
public static class JsonRpcEndpoint {
    /// <summary>
    /// Handles an incoming HTTP request as a JSON-RPC 2.0 call.
    /// Detects whether the body is a single request object or a batch array.
    /// </summary>
    public static async Task HandleRpc(HttpContext ctx) {
        var options = ctx.RequestServices.GetRequiredService<IOptions<JsonRpcOptions>>().Value;
        var dispatcher = ctx.RequestServices.GetRequiredService<JsonRpcDispatcher>();
        var logger = ctx.RequestServices.GetRequiredService<ILogger<JsonRpcDispatcher>>();
        var jsonOptions = dispatcher.JsonOptions;

        JsonDocument doc;
        var parseStartTimestamp = Stopwatch.GetTimestamp();
        try {
            doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
        } catch (JsonException ex) {
            logger.LogWarning(ex, "JSON-RPC parse error — request body is not valid JSON");
            using var activity = JsonRpcTelemetry.Source.StartActivity("jsonrpc parse", ActivityKind.Server);
            JsonRpcDispatcher.RecordEndpointError(activity, "_parse", "-32700", "Parse error", "parse", parseStartTimestamp);
            ctx.Response.ContentType = "application/json";
            await JsonSerializer.SerializeAsync(
                ctx.Response.Body,
                JsonRpcResponse.ParseError(),
                jsonOptions,
                ctx.RequestAborted);
            return;
        }

        using (doc) {
            if (doc.RootElement.ValueKind == JsonValueKind.Array) {
                await HandleBatch(ctx, doc, dispatcher, jsonOptions, options, logger);
            } else {
                await HandleSingle(ctx, doc, dispatcher, jsonOptions, logger);
            }
        }
    }

    private static async Task HandleSingle(
        HttpContext ctx,
        JsonDocument doc,
        JsonRpcDispatcher dispatcher,
        JsonSerializerOptions jsonOptions,
        ILogger logger) {
        JsonRpcRequest? request;
        try {
            request = doc.RootElement.Deserialize<JsonRpcRequest>(jsonOptions);
        } catch (JsonException ex) {
            logger.LogWarning(ex, "JSON-RPC invalid request — could not deserialize request envelope");
            RecordEndpointInvalidRequest("_invalid", "envelope-deserialization");
            ctx.Response.ContentType = "application/json";
            await JsonSerializer.SerializeAsync(
                ctx.Response.Body,
                JsonRpcResponse.InvalidRequest(CreateInvalidEnvelopeRequest(doc.RootElement)),
                jsonOptions,
                ctx.RequestAborted);
            return;
        }

        if (request is null) {
            RecordEndpointInvalidRequest("_invalid", "empty-envelope");
            ctx.Response.ContentType = "application/json";
            await JsonSerializer.SerializeAsync(
                ctx.Response.Body,
                JsonRpcResponse.InvalidRequest((string?)null),
                jsonOptions,
                ctx.RequestAborted);
            return;
        }

        JsonRpcResponse response;
        await using (var scope = ctx.RequestServices.CreateDispatchScope(CreateDispatchContext(ctx, includeIdempotencyKey: true))) {
            try {
                response = await dispatcher.DispatchAsync(
                    request, scope.ServiceProvider, ctx.RequestAborted);
            } catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested) {
                // The client disconnected mid-dispatch. Bail quietly — do not write a response into the
                // aborted request and do not surface this as an error (expected cancellation).
                logger.LogDebug("JSON-RPC request aborted by the client before completion");
                return;
            }
        }

        if (IsNotification(request)) {
            ctx.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        // The request may have been aborted after dispatch but before we write; skip writing into a dead request.
        if (ctx.RequestAborted.IsCancellationRequested) {
            logger.LogDebug("JSON-RPC request aborted by the client before the response was written");
            return;
        }

        ctx.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(ctx.Response.Body, response, jsonOptions, ctx.RequestAborted);
    }

    private static async Task HandleBatch(
        HttpContext ctx,
        JsonDocument doc,
        JsonRpcDispatcher dispatcher,
        JsonSerializerOptions jsonOptions,
        JsonRpcOptions options,
        ILogger logger) {
        using var batchActivity = JsonRpcTelemetry.Source.StartActivity("jsonrpc batch", ActivityKind.Server);

        // The HTTP Idempotency-Key header applies to the whole HTTP request, so it is ambiguous for a batch: it
        // would (incorrectly) key every distinct operation in the batch to the same key, replaying the first
        // item's stored response for the rest. Reject it — a batch must carry a per-item key via each request's
        // own params._meta (the batch-correct, per-call location).
        if (HasHttpIdempotencyKey(ctx)) {
            logger.LogWarning(
                "JSON-RPC batch rejected: the HTTP {Header} header is ambiguous for a batch; carry a per-item key at each request's params._meta instead",
                Abstractions.Idempotency.IdempotencyKeyNames.HttpHeader);
            RecordBatchError(batchActivity, 0, "400", "Idempotency-Key not allowed for batches", "batch-idempotency-key");
            await WriteBatchIdempotencyKeyRejection(ctx, jsonOptions);
            return;
        }

        var array = doc.RootElement;
        var count = array.GetArrayLength();

        if (batchActivity?.IsAllDataRequested == true) {
            batchActivity.SetTag("rpc.system.name", "jsonrpc");
            batchActivity.SetTag("jsonrpc.batch.size", count);
        }

        if (count == 0) {
            RecordBatchError(batchActivity, count, "-32600", "Invalid request", "empty-batch");
            ctx.Response.ContentType = "application/json";
            await JsonSerializer.SerializeAsync(
                ctx.Response.Body,
                JsonRpcResponse.InvalidRequest((string?)null),
                jsonOptions,
                ctx.RequestAborted);
            return;
        }

        if (count > options.MaxBatchSize) {
            logger.LogWarning("JSON-RPC batch size {Count} exceeds limit {Max}", count, options.MaxBatchSize);
            if (batchActivity?.IsAllDataRequested == true) {
                batchActivity.SetTag("jsonrpc.batch.max_size", options.MaxBatchSize);
            }

            RecordBatchError(batchActivity, count, "-32600", "Batch too large", "batch-too-large");
            ctx.Response.ContentType = "application/json";
            await JsonSerializer.SerializeAsync(
                ctx.Response.Body,
                JsonRpcResponse.FromError((string?)null, new RpcError { Code = -32600, Message = $"Batch too large. Max {options.MaxBatchSize}" }),
                jsonOptions,
                ctx.RequestAborted);
            return;
        }

        var requests = new List<JsonRpcRequest>(count);
        var index = 0;
        foreach (var element in array.EnumerateArray()) {
            JsonRpcRequest? req;
            try {
                req = element.Deserialize<JsonRpcRequest>(jsonOptions);
            } catch (JsonException ex) {
                logger.LogWarning(ex, "JSON-RPC batch item — could not deserialize request envelope, using empty request");
                req = null;
            }

            if (req is null) {
                var id = ExtractResponseId(element);
                requests.Add(new JsonRpcRequest {
                    Jsonrpc = "2.0",
                    Method = "",
                    Params = null,
                    Id = id.Value,
                    HasId = id.HasId,
                    IdKind = id.Kind,
                    IdRaw = id.Raw,
                    BatchIndex = index,
                    BatchSize = count,
                    IsInvalidEnvelope = true,
                    ForceResponse = true
                });
            } else {
                requests.Add(req with {
                    BatchIndex = index,
                    BatchSize = count,
                    ForceResponse = IsInvalidBatchEnvelope(req)
                });
            }

            index++;
        }

        var strategy = ctx.RequestServices.GetRequiredService<IBatchExecutionStrategy>();
        List<JsonRpcResponse> responses;
        try {
            responses = await strategy.ExecuteAsync(
                requests,
                dispatcher,
                ctx.RequestServices,
                CreateDispatchContext(ctx),
                ctx.RequestAborted);
        } catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested) {
            // The client disconnected mid-batch. Bail quietly (expected cancellation, no response written).
            logger.LogDebug("JSON-RPC batch aborted by the client before completion");
            return;
        }

        if (responses.Count == 0) {
            ctx.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        // Skip writing into a request the client already aborted.
        if (ctx.RequestAborted.IsCancellationRequested) {
            logger.LogDebug("JSON-RPC batch aborted by the client before the response was written");
            return;
        }

        ctx.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(ctx.Response.Body, responses, jsonOptions, ctx.RequestAborted);
    }

    // Captures the request-boundary state (the authenticated principal) so the current-user initializer can
    // seed it into each per-call child scope — child scopes do not inherit the request scope's scoped
    // services, so ICurrentUser/authorization would otherwise be unset. (MCP captures RequestContext.User the
    // same way; the snapshot materializes lazily, so seeding per call costs no extra parsing.)
    private static DispatchScopeContext CreateDispatchContext(HttpContext ctx, bool includeIdempotencyKey = false) {
        var context = new DispatchScopeContext();
        context.Set<ClaimsPrincipal>(ctx.User);

        // Single-call sugar: accept the HTTP Idempotency-Key header for a JSON-RPC-over-HTTP request. The
        // canonical per-call location is params._meta (correct for batches); the header applies to the whole
        // HTTP request, so it is only meaningful for a single (non-batch) call — the batch path rejects the
        // header outright (it would key every distinct operation to the same value) and never sets this flag.
        if (includeIdempotencyKey &&
            ctx.Request.Headers.TryGetValue(Abstractions.Idempotency.IdempotencyKeyNames.HttpHeader, out var key) &&
            key.Count > 0 && !string.IsNullOrWhiteSpace(key[0])) {
            context.Set(new Abstractions.Idempotency.IdempotencyKey(key[0]!));
        }

        return context;
    }

    private static bool HasHttpIdempotencyKey(HttpContext ctx) =>
        ctx.Request.Headers.TryGetValue(Abstractions.Idempotency.IdempotencyKeyNames.HttpHeader, out var key) &&
        key.Count > 0 && !string.IsNullOrWhiteSpace(key[0]);

    private static async Task WriteBatchIdempotencyKeyRejection(HttpContext ctx, JsonSerializerOptions jsonOptions) {
        // Reject with HTTP 400 and a JSON-RPC error envelope (Invalid Request, -32600). A JSON-RPC error is written
        // rather than a ProblemDetails so it serializes through the same JSON-RPC context every other envelope on
        // this endpoint uses (staying AOT-strict), and clients that speak JSON-RPC get a shape they can parse.
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        ctx.Response.ContentType = "application/json";
        var error = new RpcError {
            Code = -32600,
            Message =
                $"The HTTP {Abstractions.Idempotency.IdempotencyKeyNames.HttpHeader} header is not allowed on a JSON-RPC batch: " +
                "it applies to the whole request and cannot key the batch's distinct operations. Carry a per-item key at each " +
                $"request's params._meta.{Abstractions.Idempotency.IdempotencyKeyNames.MetaKey} instead.",
        };
        await JsonSerializer.SerializeAsync(
            ctx.Response.Body, JsonRpcResponse.FromError((string?)null, error), jsonOptions, ctx.RequestAborted);
    }

    private static void RecordEndpointInvalidRequest(string method, string phase) {
        var startTimestamp = Stopwatch.GetTimestamp();
        using var activity = JsonRpcTelemetry.Source.StartActivity("jsonrpc invalid", ActivityKind.Server);
        JsonRpcDispatcher.RecordEndpointError(activity, method, "-32600", "Invalid request", phase, startTimestamp);
    }

    private static void RecordBatchError(Activity? batchActivity, int count, string statusCode, string description, string phase) {
        var startTimestamp = Stopwatch.GetTimestamp();
        if (batchActivity?.IsAllDataRequested == true) {
            batchActivity.SetTag("rpc.response.status_code", statusCode);
            batchActivity.SetTag("jsonrpc.outcome", "error");
            batchActivity.SetTag("jsonrpc.error.phase", phase);
            batchActivity.SetTag("error.type", statusCode);
            batchActivity.SetStatus(ActivityStatusCode.Error, description);
        }

        JsonRpcTelemetry.RecordRequest("_batch", statusCode, Stopwatch.GetElapsedTime(startTimestamp));
    }

    private static bool IsInvalidBatchEnvelope(JsonRpcRequest request) =>
        request.Jsonrpc != "2.0" || string.IsNullOrWhiteSpace(request.Method);

    private static bool IsNotification(JsonRpcRequest request) =>
        !request.ShouldSendResponse &&
        request.Jsonrpc == "2.0" &&
        !string.IsNullOrWhiteSpace(request.Method);

    private static JsonRpcRequest CreateInvalidEnvelopeRequest(JsonElement element) {
        var id = ExtractResponseId(element);
        return new JsonRpcRequest {
            Jsonrpc = "2.0",
            Method = "",
            Id = id.Value,
            HasId = id.HasId,
            IdKind = id.Kind,
            IdRaw = id.Raw,
            IsInvalidEnvelope = true,
            ForceResponse = true
        };
    }

    private static JsonRpcIdInfo ExtractResponseId(JsonElement element) {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("id", out var id)) {
            return JsonRpcIdInfo.Missing;
        }

        return id.ValueKind switch {
            JsonValueKind.String => JsonRpcIdInfo.String(id.GetString()),
            JsonValueKind.Number => JsonRpcIdInfo.Number(id.GetRawText()),
            JsonValueKind.Null => JsonRpcIdInfo.Null,
            _ => JsonRpcIdInfo.Null
        };
    }
}
