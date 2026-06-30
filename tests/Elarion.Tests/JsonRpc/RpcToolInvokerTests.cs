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

/// <summary>Tests for <see cref="RpcToolInvoker"/> — the neutral, scope-owning dispatch shim.</summary>
public sealed class RpcToolInvokerTests {
    private sealed record EchoCommand {
        public required string Name { get; init; }
    }

    private sealed record EchoResponse(string Greeting);

    private sealed class EchoHandler : IHandler<EchoCommand, Result<EchoResponse>> {
        public ValueTask<Result<EchoResponse>> HandleAsync(EchoCommand request, CancellationToken ct) =>
            request.Name == "missing"
                ? ValueTask.FromResult<Result<EchoResponse>>(AppError.NotFound("client not found"))
                : ValueTask.FromResult<Result<EchoResponse>>(new EchoResponse($"Hello {request.Name}"));
    }

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    private static (HandlerDispatcher Dispatcher, IServiceProvider Services) Build() {
        var dispatcher = new JsonRpcDispatcher(Options).MapHandler<EchoCommand, EchoResponse>("echo").Freeze();
        var services = new ServiceCollection()
            .AddScoped<IHandler<EchoCommand, Result<EchoResponse>>, EchoHandler>()
            .BuildServiceProvider();
        return (dispatcher.Registry, services);
    }

    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value, Options);

    [Fact]
    public async Task InvokeAsync_Success_ReturnsSerializedResult() {
        var (dispatcher, services) = Build();

        // Pass the root provider; the invoker creates its own per-call scope to resolve the scoped handler.
        var result = await RpcToolInvoker.InvokeAsync(
            dispatcher, HandlerTransports.All, "echo", Args(new { name = "World" }), services, Options,
            ct: TestContext.Current.CancellationToken);

        result.IsError.Should().BeFalse();
        using var doc = JsonDocument.Parse(result.Text);
        doc.RootElement.GetProperty("greeting").GetString().Should().Be("Hello World");
    }

    [Fact]
    public async Task InvokeAsync_HandlerError_ReturnsErrorMessageAndCode() {
        var (dispatcher, services) = Build();

        var result = await RpcToolInvoker.InvokeAsync(
            dispatcher, HandlerTransports.All, "echo", Args(new { name = "missing" }), services, Options,
            ct: TestContext.Current.CancellationToken);

        result.IsError.Should().BeTrue();
        result.Text.Should().Be("client not found");
        result.ErrorCode.Should().Be(-32001);
    }

    [Fact]
    public async Task InvokeAsync_UnknownMethod_ReturnsMethodNotFound() {
        var (dispatcher, services) = Build();

        var result = await RpcToolInvoker.InvokeAsync(
            dispatcher, HandlerTransports.All, "does.not.exist", null, services, Options,
            ct: TestContext.Current.CancellationToken);

        result.IsError.Should().BeTrue();
        result.ErrorCode.Should().Be(-32601);
    }
}
