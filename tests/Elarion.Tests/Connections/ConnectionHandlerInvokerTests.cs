using System.Security.Claims;
using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.Abstractions.Connections;
using Elarion.Abstractions.Dispatch;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elarion.Tests.Connections;

public sealed class ConnectionHandlerInvokerTests {
    private sealed record Request(string Value);
    private sealed record Response(
        string Value,
        ClaimsPrincipal Principal,
        ClientConnection Connection,
        CustomMetadata? Metadata);
    private sealed record StreamRequest;
    private sealed record CustomMetadata(string Value);
    private sealed record WrongRequest;

    [Fact]
    public async Task InvokeAsync_UsesDecoratedHandlerAndSeedsExactConnectionContext() {
        var principal = Principal("old-user");
        var snapshot = Connection("old", principal, revision: 0);
        var sink = new CountingSink(snapshot);
        var calls = new CallLog();
        using var provider = CreateUnaryProvider(calls);
        var metadata = new CustomMetadata("adapter-value");

        var result = await ConnectionHandlerInvoker.InvokeAsync<Request, Response>(
            provider,
            sink,
            new Request("hello"),
            context => context.Set(metadata),
            TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Value.Should().Be("hello");
        result.Value.Principal.Should().BeSameAs(principal);
        result.Value.Connection.Should().BeSameAs(snapshot);
        result.Value.Metadata.Should().BeSameAs(metadata);
        calls.Calls.Should().Equal("decorator", "handler");
        sink.ReadCount.Should().Be(1);
    }

    [Fact]
    public async Task InvokeAsync_EnrichmentCannotOverrideFrameworkIdentity() {
        var principal = Principal("framework-user");
        var snapshot = Connection("framework", principal, revision: 3);
        var spoofedPrincipal = Principal("spoofed-user");
        var spoofedConnection = Connection("spoofed", spoofedPrincipal, revision: 99);
        using var provider = CreateUnaryProvider(new CallLog());

        var result = await ConnectionHandlerInvoker.InvokeAsync<Request, Response>(
            provider,
            new CountingSink(snapshot),
            new Request("protected"),
            context => {
                context.Set<ClaimsPrincipal>(spoofedPrincipal);
                context.Set<ClientConnection>(spoofedConnection);
            },
            TestContext.Current.CancellationToken);

        result.Value!.Principal.Should().BeSameAs(principal);
        result.Value.Connection.Should().BeSameAs(snapshot);
    }

    [Fact]
    public async Task InvokeAsync_CapturesConnectionOnceAndKeepsOldSnapshotDuringPromotionRace() {
        var oldSnapshot = Connection("old", Principal("old-user"), revision: 0);
        var newSnapshot = Connection("new", Principal("new-user"), revision: 1);
        var sink = new PromotingGetterSink(oldSnapshot, newSnapshot);
        using var provider = CreateUnaryProvider(new CallLog());

        var result = await ConnectionHandlerInvoker.InvokeAsync<Request, Response>(
            provider,
            sink,
            new Request("race"),
            context => sink.PromotionObservedDuringEnrichment = sink.ReadCount == 1,
            TestContext.Current.CancellationToken);

        sink.ReadCount.Should().Be(1);
        sink.PromotionObservedDuringEnrichment.Should().BeTrue();
        result.Value!.Principal.Should().BeSameAs(oldSnapshot.Principal);
        result.Value.Connection.Should().BeSameAs(oldSnapshot);
    }

    [Fact]
    public async Task InvokeAsync_NextInvocationObservesPromotedSnapshot() {
        var oldSnapshot = Connection("old", Principal("old-user"), revision: 0);
        var newSnapshot = Connection("new", Principal("new-user"), revision: 1);
        var sink = new MutableSink(oldSnapshot);
        using var provider = CreateUnaryProvider(new CallLog());

        var first = await ConnectionHandlerInvoker.InvokeAsync<Request, Response>(
            provider, sink, new Request("first"), ct: TestContext.Current.CancellationToken);
        sink.Current = newSnapshot;
        var second = await ConnectionHandlerInvoker.InvokeAsync<Request, Response>(
            provider, sink, new Request("second"), ct: TestContext.Current.CancellationToken);

        first.Value!.Connection.Should().BeSameAs(oldSnapshot);
        second.Value!.Connection.Should().BeSameAs(newSnapshot);
        second.Value.Principal.Should().BeSameAs(newSnapshot.Principal);
        sink.ReadCount.Should().Be(2);
    }

    [Fact]
    public async Task InvokeAsync_FailedResultRemainsAValue() {
        var expected = AppError.Conflict("already handled");
        using var provider = new ServiceCollection()
            .AddScoped<IHandler<Request, Result<Response>>>(_ => new FailingHandler(expected))
            .BuildServiceProvider();

        var result = await ConnectionHandlerInvoker.InvokeAsync<Request, Response>(
            provider,
            new CountingSink(Connection("failure", Principal("user"))),
            new Request("fail"),
            ct: TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(expected);
    }

    [Fact]
    public async Task InvokeNamedAsync_FiltersConnectionRoutesAndValidatesDecodedType() {
        var connectionCalls = 0;
        var jsonRpcCalls = 0;
        var dispatcher = new HandlerDispatcher()
            .MapDelegate<Request, Response>(
                "connection.call",
                (request, services, _) => {
                    connectionCalls++;
                    var values = services.GetRequiredService<ScopeValues>();
                    return ValueTask.FromResult<Result<Response>>(
                        new Response(request.Value, values.Principal!, values.Connection!, values.Metadata));
                },
                HandlerTransports.Connection)
            .MapDelegate<Request, Response>(
                "jsonrpc.only",
                (request, _, _) => {
                    jsonRpcCalls++;
                    return ValueTask.FromResult<Result<Response>>(
                        new Response(request.Value, new ClaimsPrincipal(), Connection("unused", new ClaimsPrincipal()), null));
                },
                HandlerTransports.JsonRpc)
            .Freeze();
        var snapshot = Connection("named", Principal("named-user"));
        var sink = new CountingSink(snapshot);
        using var provider = new ServiceCollection()
            .AddScoped<ScopeValues>()
            .AddSingleton<IDispatchScopeInitializer, ScopeValuesInitializer>()
            .BuildServiceProvider();

        var success = await ConnectionHandlerInvoker.InvokeNamedAsync(
            provider,
            sink,
            dispatcher,
            "connection.call",
            new Request("ok"),
            context => context.Set(new CustomMetadata("named")),
            TestContext.Current.CancellationToken);
        var hidden = await ConnectionHandlerInvoker.InvokeNamedAsync(
            provider, sink, dispatcher, "jsonrpc.only", new Request("hidden"), ct: TestContext.Current.CancellationToken);
        var unknown = await ConnectionHandlerInvoker.InvokeNamedAsync(
            provider, sink, dispatcher, "unknown", new Request("unknown"), ct: TestContext.Current.CancellationToken);
        var wrongType = async () => await ConnectionHandlerInvoker.InvokeNamedAsync(
            provider, sink, dispatcher, "connection.call", new WrongRequest(), ct: TestContext.Current.CancellationToken);

        success.IsSuccess.Should().BeTrue();
        success.Value.Should().BeOfType<Response>().Which.Connection.Should().BeSameAs(snapshot);
        connectionCalls.Should().Be(1);
        sink.ReadCount.Should().Be(4);
        hidden.IsSuccess.Should().BeFalse();
        unknown.IsSuccess.Should().BeFalse();
        hidden.Error.Kind.Should().Be(ErrorKind.NotFound);
        unknown.Error.Kind.Should().Be(ErrorKind.NotFound);
        hidden.Error.Message.Should().Be(unknown.Error.Message);
        jsonRpcCalls.Should().Be(0);
        await wrongType.Should().ThrowAsync<ArgumentException>().WithMessage("*decoded request*Request*WrongRequest*");
        connectionCalls.Should().Be(1);
    }

    [Fact]
    public async Task InvokeStreamAsync_KeepsScopeAliveThroughLazyEnumerationAndDisposesAtTerminalState() {
        ScopeProbe? probe = null;
        var snapshot = Connection("stream", Principal("stream-user"));
        using var provider = new ServiceCollection()
            .AddScoped<ScopeValues>()
            .AddScoped<ScopeProbe>(_ => probe = new ScopeProbe())
            .AddSingleton<IDispatchScopeInitializer, ScopeValuesInitializer>()
            .AddScoped<IStreamHandler<StreamRequest, int>, ObservingStreamHandler>()
            .BuildServiceProvider();

        var start = await ConnectionHandlerInvoker.InvokeStreamAsync<StreamRequest, int>(
            provider,
            new CountingSink(snapshot),
            new StreamRequest(),
            ct: TestContext.Current.CancellationToken);

        start.IsSuccess.Should().BeTrue();
        probe.Should().NotBeNull();
        probe!.Disposed.Should().BeFalse();
        var values = new List<int>();
        await foreach (var value in start.Value!)
            values.Add(value);

        values.Should().Equal(1, 2);
        probe.Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task InvokeStreamAsync_StartupFailureDisposesScopeImmediately() {
        ScopeProbe? probe = null;
        using var provider = new ServiceCollection()
            .AddScoped<ScopeProbe>(_ => probe = new ScopeProbe())
            .AddScoped<IStreamHandler<StreamRequest, int>, RejectingStreamHandler>()
            .BuildServiceProvider();

        var start = await ConnectionHandlerInvoker.InvokeStreamAsync<StreamRequest, int>(
            provider,
            new CountingSink(Connection("rejected", Principal("user"))),
            new StreamRequest(),
            ct: TestContext.Current.CancellationToken);

        start.IsSuccess.Should().BeFalse();
        start.Error.Kind.Should().Be(ErrorKind.NotFound);
        probe.Should().NotBeNull();
        probe!.Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task InvokeAsync_FlowsCancellationTokenUnchanged() {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var observed = new CancellationCapture();
        using var provider = new ServiceCollection()
            .AddSingleton(observed)
            .AddScoped<IHandler<Request, Result<Response>>, CancellationObservingHandler>()
            .BuildServiceProvider();

        var result = await ConnectionHandlerInvoker.InvokeAsync<Request, Response>(
            provider,
            new CountingSink(Connection("cancel", Principal("user"))),
            new Request("cancel"),
            ct: cancellation.Token);

        result.IsSuccess.Should().BeTrue();
        observed.Token.Should().Be(cancellation.Token);
        observed.Token.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public void InvokeAsync_EnrichmentFailurePropagatesBeforeDispatch() {
        var calls = new CallLog();
        var sink = new CountingSink(Connection("enrichment", Principal("user")));
        using var provider = CreateUnaryProvider(calls);

        var invoke = () => ConnectionHandlerInvoker.InvokeAsync<Request, Response>(
            provider,
            sink,
            new Request("ignored"),
            _ => throw new InvalidOperationException("enrichment failed"),
            TestContext.Current.CancellationToken);

        invoke.Should().Throw<InvalidOperationException>().WithMessage("enrichment failed");
        sink.ReadCount.Should().Be(1);
        calls.Calls.Should().BeEmpty();
    }

    private static ServiceProvider CreateUnaryProvider(CallLog calls) => new ServiceCollection()
        .AddSingleton(calls)
        .AddScoped<ScopeValues>()
        .AddSingleton<IDispatchScopeInitializer, ScopeValuesInitializer>()
        .AddScoped<UnaryHandler>()
        .AddScoped<IHandler<Request, Result<Response>>>(services =>
            new RecordingDecorator(
                services.GetRequiredService<UnaryHandler>(),
                services.GetRequiredService<CallLog>()))
        .BuildServiceProvider();

    private static ClaimsPrincipal Principal(string subject) =>
        new(new ClaimsIdentity([new Claim("sub", subject)], authenticationType: "test"));

    private static ClientConnection Connection(
        string id,
        ClaimsPrincipal principal,
        long revision = 0) => new() {
            ConnectionId = id,
            Transport = "test",
            Principal = principal,
            PrincipalId = principal.FindFirst("sub")?.Value,
            ConnectedAt = DateTimeOffset.UnixEpoch,
            IdentityRevision = revision,
        };

    private sealed class ScopeValues {
        public ClaimsPrincipal? Principal { get; set; }
        public ClientConnection? Connection { get; set; }
        public CustomMetadata? Metadata { get; set; }
    }

    private sealed class ScopeValuesInitializer : IDispatchScopeInitializer {
        public void Initialize(IServiceProvider callScope, DispatchScopeContext context) {
            var values = callScope.GetRequiredService<ScopeValues>();
            context.TryGet(out ClaimsPrincipal? principal).Should().BeTrue();
            context.TryGet(out ClientConnection? connection).Should().BeTrue();
            context.TryGet(out CustomMetadata? metadata);
            values.Principal = principal;
            values.Connection = connection;
            values.Metadata = metadata;
        }
    }

    private sealed class CallLog {
        public List<string> Calls { get; } = [];
    }

    private sealed class UnaryHandler(ScopeValues values, CallLog calls)
        : IHandler<Request, Result<Response>> {
        public ValueTask<Result<Response>> HandleAsync(Request request, CancellationToken ct) {
            calls.Calls.Add("handler");
            return ValueTask.FromResult<Result<Response>>(
                new Response(request.Value, values.Principal!, values.Connection!, values.Metadata));
        }
    }

    private sealed class RecordingDecorator(IHandler<Request, Result<Response>> inner, CallLog calls)
        : IHandler<Request, Result<Response>> {
        public ValueTask<Result<Response>> HandleAsync(Request request, CancellationToken ct) {
            calls.Calls.Add("decorator");
            return inner.HandleAsync(request, ct);
        }
    }

    private sealed class FailingHandler(AppError error) : IHandler<Request, Result<Response>> {
        public ValueTask<Result<Response>> HandleAsync(Request request, CancellationToken ct) =>
            ValueTask.FromResult<Result<Response>>(error);
    }

    private sealed class ScopeProbe : IDisposable {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    private sealed class ObservingStreamHandler(ScopeProbe probe, ScopeValues values)
        : IStreamHandler<StreamRequest, int> {
        public ValueTask<Result<IAsyncEnumerable<int>>> HandleAsync(StreamRequest request, CancellationToken ct) {
            values.Connection.Should().NotBeNull();
            values.Principal.Should().BeSameAs(values.Connection!.Principal);
            return ValueTask.FromResult(Result<IAsyncEnumerable<int>>.Success(Values(probe)));
        }

        private static async IAsyncEnumerable<int> Values(ScopeProbe probe) {
            probe.Disposed.Should().BeFalse();
            yield return 1;
            await Task.Yield();
            probe.Disposed.Should().BeFalse();
            yield return 2;
        }
    }

    private sealed class RejectingStreamHandler(ScopeProbe probe) : IStreamHandler<StreamRequest, int> {
        public ValueTask<Result<IAsyncEnumerable<int>>> HandleAsync(StreamRequest request, CancellationToken ct) {
            probe.Disposed.Should().BeFalse();
            return ValueTask.FromResult<Result<IAsyncEnumerable<int>>>(AppError.NotFound("rejected"));
        }
    }

    private sealed class CancellationCapture {
        public CancellationToken Token { get; set; }
    }

    private sealed class CancellationObservingHandler(CancellationCapture capture)
        : IHandler<Request, Result<Response>> {
        public ValueTask<Result<Response>> HandleAsync(Request request, CancellationToken ct) {
            capture.Token = ct;
            var principal = new ClaimsPrincipal();
            return ValueTask.FromResult<Result<Response>>(
                new Response(request.Value, principal, Connection("unused", principal), null));
        }
    }

    private class CountingSink(ClientConnection connection) : IClientConnectionSink {
        protected ClientConnection Snapshot = connection;
        private int _readCount;

        public ClientConnectionState ConnectionState { get; } = new(connection);

        public virtual ClientConnection Connection {
            get {
                Interlocked.Increment(ref _readCount);
                return Snapshot;
            }
        }

        public int ReadCount => _readCount;

        public ValueTask SendAsync<TPayload>(string name, TPayload payload, CancellationToken ct = default)
            where TPayload : class => throw new NotSupportedException();

        public ValueTask<TResponse> InvokeAsync<TRequest, TResponse>(
            string name,
            TRequest request,
            ClientInvokeOptions? options = null,
            CancellationToken ct = default)
            where TRequest : class => throw new NotSupportedException();
    }

    private sealed class MutableSink(ClientConnection connection) : CountingSink(connection) {
        public ClientConnection Current {
            get => Snapshot;
            set => Snapshot = value;
        }
    }

    private sealed class PromotingGetterSink(ClientConnection oldSnapshot, ClientConnection newSnapshot)
        : CountingSink(oldSnapshot) {
        public bool PromotionObservedDuringEnrichment { get; set; }

        public override ClientConnection Connection {
            get {
                var captured = base.Connection;
                Snapshot = newSnapshot;
                return captured;
            }
        }
    }
}
