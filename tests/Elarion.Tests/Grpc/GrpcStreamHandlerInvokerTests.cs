using System.Runtime.CompilerServices;
using System.Security.Claims;
using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.Abstractions.Dispatch;
using Elarion.Grpc;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elarion.Tests.Grpc;

public sealed class GrpcStreamHandlerInvokerTests {
    [Fact]
    public async Task InvokeServerStreamingAsync_SeedsBoundaryStateFlowsCancellationAndUsesDecoratedHandler() {
        using var cancellation = new CancellationTokenSource();
        var principal = new ClaimsPrincipal(
            new ClaimsIdentity([new Claim("sub", "grpc-stream-user")], "grpc"));
        var callContext = new TestServerCallContext(cancellation.Token);
        var capture = new ScopeCapture();
        var calls = new CallLog();
        StreamScope? streamScope = null;
        using var provider = new ServiceCollection()
            .AddSingleton(capture)
            .AddSingleton(calls)
            .AddScoped<StreamScope>(_ => streamScope = new StreamScope())
            .AddSingleton<IDispatchScopeInitializer, ScopeCaptureInitializer>()
            .AddScoped<InnerHandler>()
            .AddScoped<IStreamHandler<ApplicationRequest, ApplicationItem>>(sp =>
                new RecordingDecorator(sp.GetRequiredService<InnerHandler>(), sp.GetRequiredService<CallLog>()))
            .AddElarionGrpcTransport(_ => principal)
            .BuildServiceProvider();
        var invoker = provider.GetRequiredService<GrpcStreamHandlerInvoker>();

        await using var invocation = await invoker.InvokeServerStreamingAsync<ApplicationRequest, ApplicationItem>(
            new ApplicationRequest("input"),
            callContext);
        var values = new List<string>();
        await foreach (var item in invocation.WithCancellation(callContext.CancellationToken))
            values.Add(item.Value);

        values.Should().Equal("input-1", "input-2");
        capture.Principal.Should().BeSameAs(principal);
        capture.CallContext.Should().BeSameAs(callContext);
        capture.CancellationToken.Should().Be(cancellation.Token);
        calls.Calls.Should().Equal("decorator", "handler");
        streamScope!.Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task InvokeServerStreamingAsync_FailedStartupUsesRegisteredTranslator() {
        var translator = new RecordingTranslator();
        using var provider = new ServiceCollection()
            .AddSingleton<IAppErrorTranslator<RpcException>>(translator)
            .AddScoped<IStreamHandler<ApplicationRequest, ApplicationItem>, RejectingHandler>()
            .AddElarionGrpcTransport(_ => new ClaimsPrincipal())
            .BuildServiceProvider();
        var invoker = provider.GetRequiredService<GrpcStreamHandlerInvoker>();

        var start = async () => await invoker.InvokeServerStreamingAsync<ApplicationRequest, ApplicationItem>(
            new ApplicationRequest("ignored"),
            new TestServerCallContext(CancellationToken.None));

        var exception = await start.Should().ThrowAsync<RpcException>();
        exception.Which.Status.Detail.Should().Be("custom translator");
        translator.Translated.Should().Be(AppError.NotFound("not here"));
    }

    [Fact]
    public async Task InvokeServerStreamingAsync_MapsItemsAndWritesResponses() {
        using var provider = new ServiceCollection()
            .AddScoped<IStreamHandler<ApplicationRequest, ApplicationItem>, MappingHandler>()
            .AddElarionGrpcTransport(_ => new ClaimsPrincipal())
            .BuildServiceProvider();
        var invoker = provider.GetRequiredService<GrpcStreamHandlerInvoker>();
        var writer = new RecordingWriter();

        await invoker.InvokeServerStreamingAsync(
            new WireRequest("input"),
            writer,
            new TestServerCallContext(CancellationToken.None),
            static wire => new ApplicationRequest(wire.Value + "-request"),
            static (ApplicationItem item) => new WireItem(item.Value + "-response"));

        writer.Items.Should().Equal(
            new WireItem("input-request-1-response"),
            new WireItem("input-request-2-response"));
    }

    [Fact]
    public async Task InvokeServerStreamingAsync_PostStartFaultPropagatesWithoutRetranslatingIt() {
        var translator = new RecordingTranslator();
        using var provider = new ServiceCollection()
            .AddSingleton<IAppErrorTranslator<RpcException>>(translator)
            .AddScoped<IStreamHandler<ApplicationRequest, ApplicationItem>, FaultingHandler>()
            .AddElarionGrpcTransport(_ => new ClaimsPrincipal())
            .BuildServiceProvider();
        var invoker = provider.GetRequiredService<GrpcStreamHandlerInvoker>();
        var writer = new RecordingWriter();

        var stream = () => invoker.InvokeServerStreamingAsync(
            new WireRequest("input"),
            writer,
            new TestServerCallContext(CancellationToken.None),
            static wire => new ApplicationRequest(wire.Value),
            static (ApplicationItem item) => new WireItem(item.Value));

        await stream.Should().ThrowAsync<InvalidOperationException>().WithMessage("stream fault");
        writer.Items.Should().Equal(new WireItem("input-1"));
        translator.Translated.Should().BeNull();
    }

    [Fact]
    public async Task InvokeServerStreamingAsync_FlowsCallCancellationToEnumerationAndDisposesScope() {
        using var cancellation = new CancellationTokenSource();
        StreamScope? streamScope = null;
        using var provider = new ServiceCollection()
            .AddScoped<StreamScope>(_ => streamScope = new StreamScope())
            .AddScoped<IStreamHandler<ApplicationRequest, ApplicationItem>, WaitingHandler>()
            .AddElarionGrpcTransport(_ => new ClaimsPrincipal())
            .BuildServiceProvider();
        var invoker = provider.GetRequiredService<GrpcStreamHandlerInvoker>();
        await using var invocation = await invoker.InvokeServerStreamingAsync<ApplicationRequest, ApplicationItem>(
            new ApplicationRequest("ignored"),
            new TestServerCallContext(cancellation.Token));

        var enumerate = async () => {
            await foreach (var _ in invocation.WithCancellation(cancellation.Token)) {
            }
        };
        cancellation.Cancel();

        await enumerate.Should().ThrowAsync<OperationCanceledException>();
        streamScope!.Disposed.Should().BeTrue();
    }

    private sealed record WireRequest(string Value);

    private sealed record WireItem(string Value);

    private sealed record ApplicationRequest(string Value);

    private sealed record ApplicationItem(string Value);

    private sealed class ScopeCapture {
        public ClaimsPrincipal? Principal { get; set; }

        public ServerCallContext? CallContext { get; set; }

        public CancellationToken CancellationToken { get; set; }
    }

    private sealed class StreamScope : IDisposable {
        public bool Disposed { get; private set; }

        public void Dispose() {
            Disposed = true;
        }
    }

    private sealed class CallLog {
        public List<string> Calls { get; } = [];
    }

    private sealed class ScopeCaptureInitializer : IDispatchScopeInitializer {
        public void Initialize(IServiceProvider callScope, DispatchScopeContext context) {
            var capture = callScope.GetRequiredService<ScopeCapture>();
            context.TryGet(out ClaimsPrincipal? principal).Should().BeTrue();
            context.TryGet(out ServerCallContext? callContext).Should().BeTrue();
            capture.Principal = principal;
            capture.CallContext = callContext;
        }
    }

    private sealed class InnerHandler(ScopeCapture capture, CallLog calls, StreamScope streamScope)
        : IStreamHandler<ApplicationRequest, ApplicationItem> {
        public ValueTask<Result<IAsyncEnumerable<ApplicationItem>>> HandleAsync(
            ApplicationRequest request,
            CancellationToken ct) {
            calls.Calls.Add("handler");
            capture.CancellationToken = ct;
            return ValueTask.FromResult(
                Result<IAsyncEnumerable<ApplicationItem>>.Success(Values(request, streamScope)));
        }

        private static async IAsyncEnumerable<ApplicationItem> Values(
            ApplicationRequest request,
            StreamScope streamScope) {
            yield return new ApplicationItem(request.Value + "-1");
            await Task.Yield();
            streamScope.Disposed.Should().BeFalse();
            yield return new ApplicationItem(request.Value + "-2");
        }
    }

    private sealed class RecordingDecorator(
        IStreamHandler<ApplicationRequest, ApplicationItem> inner,
        CallLog calls) : IStreamHandler<ApplicationRequest, ApplicationItem> {
        public async ValueTask<Result<IAsyncEnumerable<ApplicationItem>>> HandleAsync(
            ApplicationRequest request,
            CancellationToken ct) {
            calls.Calls.Add("decorator");
            return await inner.HandleAsync(request, ct);
        }
    }

    private sealed class RejectingHandler : IStreamHandler<ApplicationRequest, ApplicationItem> {
        public ValueTask<Result<IAsyncEnumerable<ApplicationItem>>> HandleAsync(
            ApplicationRequest request,
            CancellationToken ct) {
            return ValueTask.FromResult<Result<IAsyncEnumerable<ApplicationItem>>>(AppError.NotFound("not here"));
        }
    }

    private sealed class MappingHandler : IStreamHandler<ApplicationRequest, ApplicationItem> {
        public ValueTask<Result<IAsyncEnumerable<ApplicationItem>>> HandleAsync(
            ApplicationRequest request,
            CancellationToken ct) {
            return ValueTask.FromResult(Result<IAsyncEnumerable<ApplicationItem>>.Success(Values(request)));
        }

        private static async IAsyncEnumerable<ApplicationItem> Values(ApplicationRequest request) {
            yield return new ApplicationItem(request.Value + "-1");
            await Task.Yield();
            yield return new ApplicationItem(request.Value + "-2");
        }
    }

    private sealed class WaitingHandler(StreamScope streamScope) : IStreamHandler<ApplicationRequest, ApplicationItem> {
        public ValueTask<Result<IAsyncEnumerable<ApplicationItem>>> HandleAsync(
            ApplicationRequest request,
            CancellationToken ct) {
            return ValueTask.FromResult(Result<IAsyncEnumerable<ApplicationItem>>.Success(Wait(streamScope)));
        }

        private static async IAsyncEnumerable<ApplicationItem> Wait(
            StreamScope streamScope,
            [EnumeratorCancellation] CancellationToken ct = default) {
            _ = streamScope;
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            yield break;
        }
    }

    private sealed class FaultingHandler : IStreamHandler<ApplicationRequest, ApplicationItem> {
        public ValueTask<Result<IAsyncEnumerable<ApplicationItem>>> HandleAsync(
            ApplicationRequest request,
            CancellationToken ct) {
            return ValueTask.FromResult(Result<IAsyncEnumerable<ApplicationItem>>.Success(Fault(request)));
        }

        private static async IAsyncEnumerable<ApplicationItem> Fault(ApplicationRequest request) {
            yield return new ApplicationItem(request.Value + "-1");
            await Task.Yield();
            throw new InvalidOperationException("stream fault");
#pragma warning disable CS0162
            yield return new ApplicationItem("");
#pragma warning restore CS0162
        }
    }

    private sealed class RecordingTranslator : IAppErrorTranslator<RpcException> {
        public AppError? Translated { get; private set; }

        public RpcException Translate(AppError error) {
            Translated = error;
            return new RpcException(new Status(StatusCode.Aborted, "custom translator"));
        }
    }

    private sealed class RecordingWriter : IServerStreamWriter<WireItem> {
        public WriteOptions? WriteOptions { get; set; }

        public List<WireItem> Items { get; } = [];

        public Task WriteAsync(WireItem message) {
            Items.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class TestServerCallContext(CancellationToken cancellationToken) : ServerCallContext {
        protected override string MethodCore => "/test.Service/Method";

        protected override string HostCore => "localhost";

        protected override string PeerCore => "ipv4:127.0.0.1:1";

        protected override DateTime DeadlineCore => DateTime.MaxValue;

        protected override Metadata RequestHeadersCore { get; } = [];

        protected override CancellationToken CancellationTokenCore => cancellationToken;

        protected override Metadata ResponseTrailersCore { get; } = [];

        protected override Status StatusCore { get; set; }

        protected override WriteOptions? WriteOptionsCore { get; set; }

        protected override AuthContext AuthContextCore { get; } = new("insecure", []);

        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) {
            throw new NotSupportedException();
        }

        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) {
            return Task.CompletedTask;
        }
    }
}
