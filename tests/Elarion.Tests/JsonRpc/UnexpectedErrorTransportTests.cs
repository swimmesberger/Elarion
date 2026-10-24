using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.Abstractions.Dispatch;
using Elarion.JsonRpc;
using Elarion.JsonRpc.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elarion.Tests.JsonRpc;

/// <summary>
/// The JSON-RPC and MCP transports answer an exception that escaped the handler pipeline with the configured
/// <see cref="UnexpectedErrorOptions"/>: the message, the code <c>internal</c>, and the reference in its own field.
/// </summary>
public sealed class UnexpectedErrorTransportTests {
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    private sealed record ExplodeCommand {
        public required string Name { get; init; }
    }

    private sealed record ExplodeResponse(string Name);

    private static (JsonRpcDispatcher Dispatcher, ServiceProvider Services) Build(
        Action<UnexpectedErrorOptions>? configure = null) {
        // The delegate stands in for any part of the pipeline — handler or decorator — since the transport's catch
        // sits outside all of it.
        var dispatcher = new JsonRpcDispatcher(Options)
            .MapDelegate<ExplodeCommand, ExplodeResponse>(
                "orders.explode",
                (_, _, _) => throw new InvalidOperationException("connection string leaked"))
            .Freeze();
        var services = new ServiceCollection();
        if (configure is not null) services.ConfigureElarionUnexpectedErrors(configure);

        return (dispatcher, services.BuildServiceProvider());
    }

    private static JsonRpcRequest Request() {
        return new JsonRpcRequest {
            Jsonrpc = "2.0",
            Method = "orders.explode",
            Params = JsonSerializer.SerializeToElement(new { name = "x" }, Options),
            Id = "1"
        };
    }

    [Fact]
    public async Task JsonRpc_AnswersTheConfiguredMessageAndReference() {
        var (dispatcher, services) = Build(o => {
            o.Message = context => $"Unexpected failure in {context.Operation} ({context.Transport}).";
            o.Reference = _ => "REF-42";
        });
        await using var _ = services;
        await using var scope = services.CreateAsyncScope();

        var response = await dispatcher.DispatchAsync(
            Request(), scope.ServiceProvider, TestContext.Current.CancellationToken);

        response.Error!.Code.Should().Be(-32603);
        response.Error.Message.Should().Be("Unexpected failure in orders.explode (jsonrpc).");
        response.Error.Data.Code.Should().Be(ErrorCodes.Internal);
        response.Error.Data.Reference.Should().Be("REF-42");
    }

    [Fact]
    public async Task JsonRpc_DefaultsHideTheExceptionAndStillCarryAReference() {
        var (dispatcher, services) = Build();
        await using var _ = services;
        await using var scope = services.CreateAsyncScope();

        var response = await dispatcher.DispatchAsync(
            Request(), scope.ServiceProvider, TestContext.Current.CancellationToken);

        response.Error!.Message.Should().Be(UnexpectedErrorOptions.DefaultMessage);
        response.Error.Data.Reference.Should().NotBeNullOrWhiteSpace();

        // The reference is its own wire member of error.data, never folded into the message.
        var wire = JsonSerializer.Serialize(response, JsonRpcJsonContext.Default.JsonRpcResponse);
        using var document = JsonDocument.Parse(wire);
        var data = document.RootElement.GetProperty("error").GetProperty("data");
        data.GetProperty("code").GetString().Should().Be("internal");
        data.GetProperty("reference").GetString().Should().Be(response.Error.Data.Reference);
        wire.Should().NotContain("connection string leaked");
    }

    [Fact]
    public void ErrorDataWithoutReference_OmitsTheMember() {
        var wire = JsonSerializer.Serialize(
            new RpcErrorData { Code = ErrorCodes.NotFound }, JsonRpcJsonContext.Default.RpcErrorData);

        wire.Should().Be("""{"code":"not_found"}""");
    }

    [Fact]
    public async Task McpTool_AnswersTheConfiguredMessageAndReference() {
        var (dispatcher, services) = Build(o => {
            o.Message = context => $"Unexpected failure in {context.Operation} ({context.Transport}).";
            o.Reference = _ => "REF-7";
        });
        await using var _ = services;

        var result = await RpcToolInvoker.InvokeAsync(
            dispatcher.Registry, HandlerTransports.All, "orders.explode",
            JsonSerializer.SerializeToElement(new { name = "x" }, Options), services, Options,
            ct: TestContext.Current.CancellationToken);

        result.IsError.Should().BeTrue();
        result.ErrorCode.Should().Be(-32603);
        result.Text.Should().Be("Unexpected failure in orders.explode (mcp).");
        result.ErrorData!.Code.Should().Be(ErrorCodes.Internal);
        result.ErrorData.Reference.Should().Be("REF-7");
    }
}
