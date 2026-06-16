using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using AwesomeAssertions;
using Elarion.AspNetCore;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Elarion.Tests.Services;

public sealed class JsonRpcTelemetryTests {
    [Fact]
    public async Task HandleRpc_BatchRequest_EmitsTraceSpanForEveryItem() {
        using var activities = new ActivityCollector(JsonRpcTelemetry.ActivitySourceName);
        using var meters = new MeterCollector(JsonRpcTelemetry.MeterName);
        var jsonOptions = CreateJsonOptions();
        var dispatcher = new JsonRpcDispatcher(jsonOptions)
            .Map<TestRequest, TestResponse>(
                "test.echo",
                static (request, _, _) => Task.FromResult(RpcResult<TestResponse>.Success(new TestResponse {
                    Value = request.Value
                })))
            .Freeze();
        await using var provider = CreateProvider(dispatcher, jsonOptions);
        var body = """
            [
              { "jsonrpc": "2.0", "method": "test.echo", "params": { "value": "ok" }, "id": "1" },
              { "jsonrpc": "2.0", "method": "test.missing", "id": "2" },
              { "jsonrpc": "2.0", "id": "missing-method" },
              { "jsonrpc": "1.0", "method": "test.echo" },
              { "jsonrpc": "2.0", "method": "test.echo", "params": { "value": "null-id" }, "id": null }
            ]
            """;
        var context = CreateContext(provider, body);

        await JsonRpcEndpoint.HandleRpc(context);

        var spans = activities.Activities;
        spans.Should().Contain(activity =>
            activity.DisplayName == "jsonrpc test.echo" &&
            Equals(activity.GetTag("jsonrpc.batch.index"), 0) &&
            Equals(activity.GetTag("rpc.response.status_code"), "OK"));
        spans.Should().Contain(activity =>
            activity.DisplayName == "jsonrpc _unregistered" &&
            Equals(activity.GetTag("jsonrpc.batch.index"), 1) &&
            Equals(activity.GetTag("rpc.response.status_code"), "-32601"));
        spans.Should().Contain(activity =>
            Equals(activity.GetTag("jsonrpc.batch.index"), 2) &&
            Equals(activity.GetTag("rpc.response.status_code"), "-32600"));
        spans.Should().Contain(activity =>
            Equals(activity.GetTag("jsonrpc.batch.index"), 3) &&
            Equals(activity.GetTag("rpc.response.status_code"), "-32600") &&
            Equals(activity.GetTag("jsonrpc.protocol.version"), "_invalid"));
        spans.Should().Contain(activity =>
            Equals(activity.GetTag("jsonrpc.batch.index"), 4) &&
            Equals(activity.GetTag("rpc.response.status_code"), "OK"));
        meters.Measurements.Should().Contain(measurement =>
            measurement.InstrumentName == "rpc.server.request.count" &&
            measurement.HasTag("rpc.system.name", "jsonrpc") &&
            measurement.HasTag("rpc.method", "test.echo") &&
            measurement.HasTag("rpc.response.status_code", "OK"));
        context.Response.Body.Position = 0;
        using var response = await JsonDocument.ParseAsync(
            context.Response.Body,
            cancellationToken: TestContext.Current.CancellationToken);
        response.RootElement.GetArrayLength().Should().Be(5);
        var responseIds = response.RootElement
            .EnumerateArray()
            .Select(element => element.GetProperty("id"))
            .Where(id => id.ValueKind == JsonValueKind.String)
            .Select(id => id.GetString())
            .ToArray();
        responseIds.Should().Contain("missing-method");
    }

    [Fact]
    public async Task HandleRpc_ParseError_EmitsTraceSpanAndMetric() {
        using var activities = new ActivityCollector(JsonRpcTelemetry.ActivitySourceName);
        using var meters = new MeterCollector(JsonRpcTelemetry.MeterName);
        var jsonOptions = CreateJsonOptions();
        await using var provider = CreateProvider(new JsonRpcDispatcher(jsonOptions).Freeze(), jsonOptions);
        var context = CreateContext(provider, "{");

        await JsonRpcEndpoint.HandleRpc(context);

        activities.Activities.Should().Contain(activity =>
            activity.DisplayName == "jsonrpc parse" &&
            Equals(activity.GetTag("rpc.response.status_code"), "-32700"));
        meters.Measurements.Should().Contain(measurement =>
            measurement.InstrumentName == "rpc.server.request.count" &&
            measurement.HasTag("rpc.method", "_parse") &&
            measurement.HasTag("rpc.response.status_code", "-32700"));
    }

    [Fact]
    public async Task HandleRpc_NumericId_EchoesNumericResponseId() {
        var jsonOptions = CreateJsonOptions();
        var dispatcher = new JsonRpcDispatcher(jsonOptions)
            .Map<TestRequest, TestResponse>(
                "test.echo",
                static (request, _, _) => Task.FromResult(RpcResult<TestResponse>.Success(new TestResponse {
                    Value = request.Value
                })))
            .Freeze();
        await using var provider = CreateProvider(dispatcher, jsonOptions);
        var context = CreateContext(provider, """{ "jsonrpc": "2.0", "method": "test.echo", "params": { "value": "ok" }, "id": 42 }""");

        await JsonRpcEndpoint.HandleRpc(context);

        context.Response.Body.Position = 0;
        using var response = await JsonDocument.ParseAsync(
            context.Response.Body,
            cancellationToken: TestContext.Current.CancellationToken);
        var id = response.RootElement.GetProperty("id");
        id.ValueKind.Should().Be(JsonValueKind.Number);
        id.GetInt32().Should().Be(42);
    }

    [Fact]
    public async Task HandleRpc_NotificationOnlyBatch_ReturnsNoContentButStillTracesCall() {
        using var activities = new ActivityCollector(JsonRpcTelemetry.ActivitySourceName);
        var jsonOptions = CreateJsonOptions();
        var dispatcher = new JsonRpcDispatcher(jsonOptions)
            .Map<TestRequest, TestResponse>(
                "test.echo",
                static (request, _, _) => Task.FromResult(RpcResult<TestResponse>.Success(new TestResponse {
                    Value = request.Value
                })))
            .Freeze();
        await using var provider = CreateProvider(dispatcher, jsonOptions);
        var context = CreateContext(provider, """
            [
              { "jsonrpc": "2.0", "method": "test.echo", "params": { "value": "notify" } }
            ]
            """);

        await JsonRpcEndpoint.HandleRpc(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status204NoContent);
        context.Response.Body.Length.Should().Be(0);
        activities.Activities.Should().Contain(activity =>
            activity.DisplayName == "jsonrpc test.echo" &&
            Equals(activity.GetTag("jsonrpc.batch.index"), 0) &&
            Equals(activity.GetTag("rpc.response.status_code"), "OK"));
    }

    [Fact]
    public async Task HandleRpc_SingleNotification_ReturnsNoContentButStillTracesCall() {
        using var activities = new ActivityCollector(JsonRpcTelemetry.ActivitySourceName);
        var jsonOptions = CreateJsonOptions();
        var dispatcher = new JsonRpcDispatcher(jsonOptions)
            .Map<TestRequest, TestResponse>(
                "test.echo",
                static (request, _, _) => Task.FromResult(RpcResult<TestResponse>.Success(new TestResponse {
                    Value = request.Value
                })))
            .Freeze();
        await using var provider = CreateProvider(dispatcher, jsonOptions);
        var context = CreateContext(provider, """{ "jsonrpc": "2.0", "method": "test.echo", "params": { "value": "notify" } }""");

        await JsonRpcEndpoint.HandleRpc(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status204NoContent);
        context.Response.Body.Length.Should().Be(0);
        activities.Activities.Should().Contain(activity =>
            activity.DisplayName == "jsonrpc test.echo" &&
            Equals(activity.GetTag("rpc.response.status_code"), "OK"));
    }

    [Fact]
    public void JsonRpcRequest_ShouldSendResponse_DistinguishesAbsentIdFromExplicitNullId() {
        var notification = JsonSerializer.Deserialize<JsonRpcRequest>(
            """{ "jsonrpc": "2.0", "method": "test.echo" }""",
            CreateJsonOptions());
        var explicitNullId = JsonSerializer.Deserialize<JsonRpcRequest>(
            """{ "jsonrpc": "2.0", "method": "test.echo", "id": null }""",
            CreateJsonOptions());

        notification!.ShouldSendResponse.Should().BeFalse();
        explicitNullId!.ShouldSendResponse.Should().BeTrue();
    }

    [Fact]
    public void JsonRpcJsonContext_SerializesNullResponseId() {
        var json = JsonSerializer.Serialize(
            JsonRpcResponse.InvalidRequest(null),
            JsonRpcJsonContext.Default.JsonRpcResponse);

        json.Should().Contain("\"id\":null");
    }

    private static ServiceProvider CreateProvider(JsonRpcDispatcher dispatcher, JsonSerializerOptions jsonOptions) {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.ClearProviders());
        services.AddJsonRpc(options => options.SerializerOptions = jsonOptions);
        services.AddSingleton(dispatcher);
        return services.BuildServiceProvider();
    }

    private static DefaultHttpContext CreateContext(IServiceProvider provider, string body) {
        var context = new DefaultHttpContext {
            RequestServices = provider
        };
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static JsonSerializerOptions CreateJsonOptions() =>
        new() {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver()
        };

    private sealed record TestRequest {
        public required string Value { get; init; }
    }

    private sealed record TestResponse {
        public required string Value { get; init; }
    }
}
