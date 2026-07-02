using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.Abstractions.Dispatch;
using Elarion.JsonRpc;
using Elarion.JsonRpc.Mcp;
using Elarion.Tests.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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

    private sealed record ThrowCommand {
        public required string Name { get; init; }
    }

    private sealed class ThrowHandler : IHandler<ThrowCommand, Result<EchoResponse>> {
        public ValueTask<Result<EchoResponse>> HandleAsync(ThrowCommand request, CancellationToken ct) =>
            request.Name == "cancel"
                ? throw new OperationCanceledException()
                : throw new InvalidOperationException("boom: secret internal detail");
    }

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    private static (HandlerDispatcher Dispatcher, IServiceProvider Services) Build() {
        var dispatcher = new JsonRpcDispatcher(Options)
            .Map<EchoCommand, EchoResponse>("echo")
            .Map<ThrowCommand, EchoResponse>("throw")
            .Freeze();
        var services = new ServiceCollection()
            .AddScoped<IHandler<EchoCommand, Result<EchoResponse>>, EchoHandler>()
            .AddScoped<IHandler<ThrowCommand, Result<EchoResponse>>, ThrowHandler>()
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
    public async Task InvokeAsync_HandlerThrows_MapsToInternalErrorAndHidesDetail() {
        var (dispatcher, services) = Build();

        var result = await RpcToolInvoker.InvokeAsync(
            dispatcher, HandlerTransports.All, "throw", Args(new { name = "explode" }), services, Options,
            ct: TestContext.Current.CancellationToken);

        result.IsError.Should().BeTrue();
        result.ErrorCode.Should().Be(-32603);
        result.Text.Should().Be("Internal error");
        result.Text.Should().NotContain("secret internal detail");
    }

    [Fact]
    public async Task InvokeAsync_CallerCanceled_RethrowsWithoutFabricatingError() {
        var (dispatcher, services) = Build();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await RpcToolInvoker.InvokeAsync(
            dispatcher, HandlerTransports.All, "throw", Args(new { name = "cancel" }), services, Options,
            ct: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task InvokeAsync_HandlerThrows_LogsThroughHostLoggerFactory() {
        var dispatcher = new JsonRpcDispatcher(Options)
            .Map<ThrowCommand, EchoResponse>("throw")
            .Freeze();
        var provider = new CapturingLoggerProvider();
        var services = new ServiceCollection()
            .AddScoped<IHandler<ThrowCommand, Result<EchoResponse>>, ThrowHandler>()
            .AddLogging(logging => logging.AddProvider(provider))
            .BuildServiceProvider();

        var result = await RpcToolInvoker.InvokeAsync(
            dispatcher.Registry, HandlerTransports.All, "throw", Args(new { name = "explode" }), services, Options,
            ct: TestContext.Current.CancellationToken);

        result.IsError.Should().BeTrue();
        provider.Errors.Should().ContainSingle().Which.Should().Contain("throw");
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider {
        public ConcurrentQueue<string> Errors { get; } = new();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(Errors);

        public void Dispose() {
        }

        private sealed class CapturingLogger(ConcurrentQueue<string> errors) : ILogger {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) {
                if (logLevel == LogLevel.Error)
                    errors.Enqueue(formatter(state, exception));
            }
        }
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

    [Fact]
    public async Task InvokeAsync_Success_EmitsMcpSpanAndMetrics() {
        using var activities = new ActivityCollector(JsonRpcTelemetry.ActivitySourceName);
        using var meters = new MeterCollector(JsonRpcTelemetry.MeterName);
        var (dispatcher, services) = Build();

        await RpcToolInvoker.InvokeAsync(
            dispatcher, HandlerTransports.All, "echo", Args(new { name = "World" }), services, Options,
            ct: TestContext.Current.CancellationToken);

        var activity = activities.Activities.Should()
            .ContainSingle(a => a.OperationName == "mcp echo").Subject;
        activity.GetTag("rpc.system.name").Should().Be("mcp");
        activity.GetTag("rpc.method").Should().Be("echo");
        activity.GetTag("rpc.response.status_code").Should().Be("OK");

        meters.Measurements.Should().Contain(m =>
            m.InstrumentName == "rpc.server.request.count" &&
            m.HasTag("rpc.system.name", "mcp") &&
            m.HasTag("rpc.method", "echo") &&
            m.HasTag("rpc.response.status_code", "OK"));
    }

    [Fact]
    public async Task InvokeAsync_HandlerError_EmitsErroredSpanWithCode() {
        using var activities = new ActivityCollector(JsonRpcTelemetry.ActivitySourceName);
        var (dispatcher, services) = Build();

        await RpcToolInvoker.InvokeAsync(
            dispatcher, HandlerTransports.All, "echo", Args(new { name = "missing" }), services, Options,
            ct: TestContext.Current.CancellationToken);

        var activity = activities.Activities.Should()
            .ContainSingle(a => a.OperationName == "mcp echo" && a.Status == ActivityStatusCode.Error).Subject;
        activity.GetTag("rpc.response.status_code").Should().Be("-32001");
    }

    [Fact]
    public async Task InvokeAsync_UnknownMethod_EmitsBoundedSentinelTelemetry() {
        using var activities = new ActivityCollector(JsonRpcTelemetry.ActivitySourceName);
        using var meters = new MeterCollector(JsonRpcTelemetry.MeterName);
        var (dispatcher, services) = Build();

        await RpcToolInvoker.InvokeAsync(
            dispatcher, HandlerTransports.All, "does.not.exist", null, services, Options,
            ct: TestContext.Current.CancellationToken);

        var activity = activities.Activities.Should()
            .ContainSingle(a => a.OperationName == "mcp _unregistered").Subject;
        activity.GetTag("rpc.response.status_code").Should().Be("-32601");
        activity.Status.Should().Be(ActivityStatusCode.Error);

        meters.Measurements.Should().Contain(m =>
            m.InstrumentName == "rpc.server.request.count" &&
            m.HasTag("rpc.system.name", "mcp") &&
            m.HasTag("rpc.method", "_unregistered") &&
            m.HasTag("rpc.response.status_code", "-32601"));
    }
}
