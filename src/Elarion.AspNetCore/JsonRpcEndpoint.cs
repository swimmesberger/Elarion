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
        try {
            doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
        } catch (JsonException ex) {
            logger.LogWarning(ex, "JSON-RPC parse error — request body is not valid JSON");
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
            ctx.Response.ContentType = "application/json";
            await JsonSerializer.SerializeAsync(
                ctx.Response.Body,
                JsonRpcResponse.InvalidRequest(null),
                jsonOptions,
                ctx.RequestAborted);
            return;
        }

        if (request is null) {
            ctx.Response.ContentType = "application/json";
            await JsonSerializer.SerializeAsync(
                ctx.Response.Body,
                JsonRpcResponse.InvalidRequest(null),
                jsonOptions,
                ctx.RequestAborted);
            return;
        }

        await using var scope = ctx.RequestServices.CreateAsyncScope();
        var response = await dispatcher.DispatchAsync(
            request, scope.ServiceProvider, ctx.RequestAborted);

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
        using var batchActivity = JsonRpcTelemetry.Source.StartActivity("jsonrpc batch");

        var array = doc.RootElement;
        var count = array.GetArrayLength();

        if (batchActivity?.IsAllDataRequested == true) {
            batchActivity.SetTag("batch.size", count);
        }

        if (count == 0) {
            ctx.Response.ContentType = "application/json";
            await JsonSerializer.SerializeAsync(
                ctx.Response.Body,
                JsonRpcResponse.InvalidRequest(null),
                jsonOptions,
                ctx.RequestAborted);
            return;
        }

        if (count > options.MaxBatchSize) {
            logger.LogWarning("JSON-RPC batch size {Count} exceeds limit {Max}", count, options.MaxBatchSize);
            ctx.Response.ContentType = "application/json";
            await JsonSerializer.SerializeAsync(
                ctx.Response.Body,
                JsonRpcResponse.FromError(null, new RpcError { Code = -32600, Message = $"Batch too large. Max {options.MaxBatchSize}" }),
                jsonOptions,
                ctx.RequestAborted);
            return;
        }

        var requests = new List<JsonRpcRequest>(count);
        foreach (var element in array.EnumerateArray()) {
            JsonRpcRequest? req;
            try {
                req = element.Deserialize<JsonRpcRequest>(jsonOptions);
            } catch (JsonException ex) {
                logger.LogWarning(ex, "JSON-RPC batch item — could not deserialize request envelope, using empty request");
                req = null;
            }

            if (req is null) {
                requests.Add(new JsonRpcRequest { Jsonrpc = "2.0", Method = "", Params = null, Id = null });
            } else {
                requests.Add(req);
            }
        }

        var strategy = ctx.RequestServices.GetRequiredService<IBatchExecutionStrategy>();
        var responses = await strategy.ExecuteAsync(
            requests,
            dispatcher,
            ctx.RequestServices,
            ctx.RequestAborted);

        ctx.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(ctx.Response.Body, responses, jsonOptions, ctx.RequestAborted);
    }
}
