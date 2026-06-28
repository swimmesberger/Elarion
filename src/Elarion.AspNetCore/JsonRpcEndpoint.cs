using System.Diagnostics;
using System.Text.Json;
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

        // inheritFrom: the request scope — initializers copy its already-built scoped state (current user, …)
        // into the call scope instead of rebuilding it.
        await using var scope = ctx.RequestServices.CreateDispatchScope(inheritFrom: ctx.RequestServices);
        var response = await dispatcher.DispatchAsync(
            request, scope.ServiceProvider, ctx.RequestAborted);

        if (IsNotification(request)) {
            ctx.Response.StatusCode = StatusCodes.Status204NoContent;
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
        var responses = await strategy.ExecuteAsync(
            requests,
            dispatcher,
            ctx.RequestServices,
            ctx.RequestAborted);

        if (responses.Count == 0) {
            ctx.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        ctx.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(ctx.Response.Body, responses, jsonOptions, ctx.RequestAborted);
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

        JsonRpcTelemetry.RecordRequest("_batch", statusCode, Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds);
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
