using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.Abstractions.Idempotency;
using Elarion.AspNetCore;
using Elarion.Idempotency;
using Elarion.JsonRpc;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Elarion.Tests.AspNetCore;

/// <summary>
/// Regression for H5: the single HTTP <c>Idempotency-Key</c> header applies to the whole request, so it must not
/// be spread across the distinct operations of a JSON-RPC batch (which would replay the first item's stored
/// response for the rest). The endpoint rejects such a batch with a 400 ProblemDetails and applies the header only
/// to a single (non-batch) call.
/// </summary>
public sealed class BatchIdempotencyKeyTests {
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web) {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    [Fact]
    public async Task Batch_WithIdempotencyKeyHeader_RejectedWith400() {
        await using var provider = BuildProvider();
        var context = CreateContext(
            provider,
            """
            [
              { "jsonrpc": "2.0", "method": "things.create", "params": {}, "id": 1 },
              { "jsonrpc": "2.0", "method": "things.get", "params": {}, "id": 2 }
            ]
            """,
            idempotencyKey: "shared-key");

        await JsonRpcEndpoint.HandleRpc(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        using var doc = ReadResponse(context);
        doc.RootElement.GetProperty("error").GetProperty("code").GetInt32().Should().Be(-32600);
        doc.RootElement.GetProperty("error").GetProperty("message").GetString()
            .Should().Contain(IdempotencyKeyNames.HttpHeader);
    }

    [Fact]
    public async Task Batch_WithoutIdempotencyKeyHeader_ProcessesNormally() {
        await using var provider = BuildProvider();
        var context = CreateContext(
            provider,
            """
            [
              { "jsonrpc": "2.0", "method": "things.create", "params": {}, "id": 1 },
              { "jsonrpc": "2.0", "method": "things.get", "params": {}, "id": 2 }
            ]
            """,
            idempotencyKey: null);

        await JsonRpcEndpoint.HandleRpc(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        using var doc = ReadResponse(context);
        doc.RootElement.GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task Single_WithIdempotencyKeyHeader_SeedsScopeKey() {
        await using var provider = BuildProvider();
        var context = CreateContext(
            provider,
            """{ "jsonrpc": "2.0", "method": "things.create", "params": {}, "id": 1 }""",
            idempotencyKey: "single-key");

        await JsonRpcEndpoint.HandleRpc(context);

        context.Response.StatusCode.Should().NotBe(StatusCodes.Status400BadRequest);
        using var doc = ReadResponse(context);
        doc.RootElement.GetProperty("result").GetProperty("key").GetString().Should().Be("single-key");
    }

    private sealed record ProbeCommand;

    private sealed record ProbeResponse(string Key);

    private static ServiceProvider BuildProvider() {
        var dispatcher = new JsonRpcDispatcher(SerializerOptions)
            .MapDelegate<ProbeCommand, ProbeResponse>(
                "things.create",
                (_, sp, _) => {
                    sp.GetRequiredService<IIdempotencyKeyAccessor>().TryGetKey(out var key);
                    return new ValueTask<Result<ProbeResponse>>(
                        Result<ProbeResponse>.Success(new ProbeResponse(key ?? "<none>")));
                },
                idempotent: true)
            .MapDelegate<ProbeCommand, ProbeResponse>(
                "things.get",
                (_, _, _) => new ValueTask<Result<ProbeResponse>>(
                    Result<ProbeResponse>.Success(new ProbeResponse("got"))))
            .Freeze();

        var services = new ServiceCollection();
        services.AddLogging(builder => builder.ClearProviders());
        services.AddElarionJsonRpc();
        services.AddSingleton(dispatcher);
        services.AddElarionIdempotency();
        return services.BuildServiceProvider();
    }

    private static DefaultHttpContext CreateContext(IServiceProvider provider, string body, string? idempotencyKey) {
        var context = new DefaultHttpContext {
            RequestServices = provider,
        };
        if (idempotencyKey is not null) {
            context.Request.Headers[IdempotencyKeyNames.HttpHeader] = idempotencyKey;
        }

        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static JsonDocument ReadResponse(HttpContext context) {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8);
        return JsonDocument.Parse(reader.ReadToEnd());
    }
}
