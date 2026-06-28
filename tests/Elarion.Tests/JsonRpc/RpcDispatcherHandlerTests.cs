using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.JsonRpc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elarion.Tests.JsonRpc;

/// <summary>
/// Runtime tests for the JSON-RPC adapter over the handler bus (<see cref="JsonRpcDispatcher"/> resolving an
/// <see cref="IHandler{TRequest,TResponse}"/> from the registry) and <see cref="AppErrorMapper"/>. These exercise
/// the glue the generated <c>RegisterHandlers</c> map depends on.
/// </summary>
public sealed class RpcDispatcherHandlerTests {
    private sealed record EchoCommand {
        public required string Name { get; init; }
    }

    private sealed record EchoResponse(string Greeting);

    private sealed class EchoHandler : IHandler<EchoCommand, Result<EchoResponse>> {
        public ValueTask<Result<EchoResponse>> HandleAsync(EchoCommand request, CancellationToken ct) =>
            request.Name switch {
                "missing" => ValueTask.FromResult<Result<EchoResponse>>(AppError.NotFound("client not found")),
                "dup" => ValueTask.FromResult<Result<EchoResponse>>(AppError.Conflict("already exists")),
                _ => ValueTask.FromResult<Result<EchoResponse>>(new EchoResponse($"Hello {request.Name}")),
            };
    }

    // The framework disables reflection-based serialization by default (AOT discipline); opt these
    // test options in explicitly so the dispatcher can (de)serialize the sample request type.
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    private static (JsonRpcDispatcher Dispatcher, IServiceProvider Services) Build() {
        var dispatcher = new JsonRpcDispatcher(Options)
            .Map<EchoCommand, EchoResponse>("echo")
            .Freeze();

        var services = new ServiceCollection()
            .AddScoped<IHandler<EchoCommand, Result<EchoResponse>>, EchoHandler>()
            .BuildServiceProvider();

        return (dispatcher, services);
    }

    private static JsonRpcRequest Request(object @params) =>
        new() {
            Jsonrpc = "2.0",
            Method = "echo",
            Params = JsonSerializer.SerializeToElement(@params, Options),
            Id = "1",
        };

    [Fact]
    public async Task Dispatch_SuccessfulHandler_ReturnsResponseValue() {
        var (dispatcher, services) = Build();
        await using var scope = services.CreateAsyncScope();

        var response = await dispatcher.DispatchAsync(
            Request(new { name = "World" }), scope.ServiceProvider, TestContext.Current.CancellationToken);

        response.Error.Should().BeNull();
        response.Result.Should().BeOfType<EchoResponse>()
            .Which.Greeting.Should().Be("Hello World");
    }

    [Theory]
    [InlineData("missing", -32001)] // ErrorKind.NotFound
    [InlineData("dup", -32002)]     // ErrorKind.Conflict
    public async Task Dispatch_HandlerReturnsAppError_MapsToJsonRpcErrorCode(string name, int expectedCode) {
        var (dispatcher, services) = Build();
        await using var scope = services.CreateAsyncScope();

        var response = await dispatcher.DispatchAsync(
            Request(new { name }), scope.ServiceProvider, TestContext.Current.CancellationToken);

        response.Result.Should().BeNull();
        response.Error.Should().NotBeNull();
        response.Error!.Code.Should().Be(expectedCode);
    }

    private sealed class FixedCodeErrorTranslator : IAppErrorTranslator<RpcError> {
        public RpcError Translate(AppError error) => new() { Code = -40404, Message = $"custom: {error.Message}" };
    }

    [Fact]
    public async Task Dispatch_RegisteredErrorTranslator_OverridesErrorCode() {
        var dispatcher = new JsonRpcDispatcher(Options).Map<EchoCommand, EchoResponse>("echo").Freeze();
        var services = new ServiceCollection()
            .AddScoped<IHandler<EchoCommand, Result<EchoResponse>>, EchoHandler>()
            .AddSingleton<IAppErrorTranslator<RpcError>, FixedCodeErrorTranslator>()
            .BuildServiceProvider();
        await using var scope = services.CreateAsyncScope();

        var response = await dispatcher.DispatchAsync(
            Request(new { name = "missing" }), scope.ServiceProvider, TestContext.Current.CancellationToken);

        response.Error.Should().NotBeNull();
        response.Error!.Code.Should().Be(-40404);
        response.Error.Message.Should().StartWith("custom:");
    }

    [Fact]
    public void AppErrorMapper_PreservesMessageAndData() {
        var data = new { field = "name" };
        var error = AppErrorMapper.ToRpcError(AppError.Validation("name is required", data));

        error.Code.Should().Be(-32602);
        error.Message.Should().Be("name is required");
        error.Data.Should().BeSameAs(data);
    }
}
