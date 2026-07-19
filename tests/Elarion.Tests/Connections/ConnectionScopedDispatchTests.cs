using System.Security.Claims;
using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.Abstractions.Connections;
using Elarion.Abstractions.Dispatch;
using Elarion.Abstractions.Idempotency;
using Elarion.Abstractions.Identity;
using Elarion.Abstractions.Pipeline;
using Elarion.Idempotency;
using Elarion.Identity;
using Elarion.Pipeline;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Elarion.Tests.Connections;

/// <summary>
/// The opt-in <see cref="ConnectionDispatchScopeMode.PerConnection"/> mode: one reused dispatch scope per
/// connection, re-seeded per message — scoped instances persist across messages, per-message state (identity,
/// enrichment, idempotency key) must not.
/// </summary>
public sealed class ConnectionScopedDispatchTests {
    private static readonly ConnectionHandlerInvokerOptions PerConnection =
        new() { ScopeMode = ConnectionDispatchScopeMode.PerConnection };

    private sealed record Request(string Value) : IQuery<Request, Response>;

    private sealed record Response(ScopedProbe Probe, IReadOnlyList<string> Roles, string? IdempotencyKey,
        CustomMetadata? Metadata);

    private sealed record CustomMetadata(string Value);

    private sealed record StreamRequest;

    [Fact]
    public async Task PerConnectionMode_ReusesOneScopeAcrossMessages() {
        using var provider = CreateProvider();
        await using var invoker = new ConnectionHandlerInvoker(provider, Sink("user-1"), PerConnection);

        var first = await invoker.InvokeAsync(new Request("a"), TestContext.Current.CancellationToken);
        var second = await invoker.InvokeAsync(new Request("b"), TestContext.Current.CancellationToken);

        second.Value!.Probe.Should().BeSameAs(first.Value!.Probe);
    }

    [Fact]
    public async Task PerMessageMode_StillCreatesAFreshScopePerMessage() {
        using var provider = CreateProvider();
        await using var invoker = new ConnectionHandlerInvoker(provider, Sink("user-1"));

        var first = await invoker.InvokeAsync(new Request("a"), TestContext.Current.CancellationToken);
        var second = await invoker.InvokeAsync(new Request("b"), TestContext.Current.CancellationToken);

        second.Value!.Probe.Should().NotBeSameAs(first.Value!.Probe);
    }

    [Fact]
    public async Task PerConnectionMode_ObservesIdentityPromotionOnTheNextMessage() {
        using var provider = CreateProvider();
        var sink = new MutableSink(Connection("c1", Principal("user-1", "guest")));
        await using var invoker = new ConnectionHandlerInvoker(provider, sink, PerConnection);

        var beforePromotion = await invoker.InvokeAsync(new Request("a"), TestContext.Current.CancellationToken);
        sink.Current = Connection("c1", Principal("user-1", "admin"), revision: 1);
        var afterPromotion = await invoker.InvokeAsync(new Request("b"), TestContext.Current.CancellationToken);

        beforePromotion.Value!.Roles.Should().BeEquivalentTo("guest");
        // The regression this guards: the reused ClaimsPrincipalCurrentUser lazily caches roles/claims, and
        // re-seeding with the promoted principal must drop that cache, not serve the pre-promotion view.
        afterPromotion.Value!.Roles.Should().BeEquivalentTo("admin");
    }

    [Fact]
    public async Task PerConnectionMode_EnrichmentEntriesDoNotLeakIntoTheNextMessage() {
        using var provider = CreateProvider();
        await using var invoker = new ConnectionHandlerInvoker(provider, Sink("user-1"), PerConnection);

        var enriched = await invoker.InvokeAsync(
            new Request("a"),
            context => context.Set(new CustomMetadata("only-message-a")),
            TestContext.Current.CancellationToken);
        var plain = await invoker.InvokeAsync(new Request("b"), TestContext.Current.CancellationToken);

        enriched.Value!.Metadata!.Value.Should().Be("only-message-a");
        plain.Value!.Metadata.Should().BeNull();
    }

    [Fact]
    public async Task PerConnectionMode_IdempotencyKeyDoesNotLeakIntoTheNextMessage() {
        using var provider = CreateProvider();
        await using var invoker = new ConnectionHandlerInvoker(provider, Sink("user-1"), PerConnection);

        var keyed = await invoker.InvokeAsync(
            new Request("a"),
            context => context.Set(new IdempotencyKey("key-a")),
            TestContext.Current.CancellationToken);
        var keyless = await invoker.InvokeAsync(new Request("b"), TestContext.Current.CancellationToken);

        keyed.Value!.IdempotencyKey.Should().Be("key-a");
        keyless.Value!.IdempotencyKey.Should().BeNull();
    }

    [Fact]
    public async Task DisposeAsync_DisposesTheConnectionScopeAndRejectsFurtherDispatch() {
        using var provider = CreateProvider();
        var invoker = new ConnectionHandlerInvoker(provider, Sink("user-1"), PerConnection);

        var result = await invoker.InvokeAsync(new Request("a"), TestContext.Current.CancellationToken);
        var probe = result.Value!.Probe;
        probe.Disposed.Should().BeFalse();

        await invoker.DisposeAsync();

        probe.Disposed.Should().BeTrue();
        var dispatchAfterDispose = async () =>
            await invoker.InvokeAsync(new Request("b"), TestContext.Current.CancellationToken);
        await dispatchAfterDispose.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task PerConnectionMode_WarnsExactlyOncePerHandlerTypeForTransactionalPipeline() {
        var log = new CapturingLoggerFactory();
        using var provider = CreateProvider(services => {
            services.AddSingleton<ILoggerFactory>(log);
            // The generated shape: the handler's metadata singleton keyed by request type, its pipeline
            // reporting the transaction decorator the generator attached.
            services.Add(ServiceDescriptor.KeyedSingleton(
                typeof(Request),
                new HandlerMetadata(
                    typeof(ProbeHandler), typeof(Request), typeof(Result<Response>),
                    static () => [new PipelineStep(typeof(TransactionDecorator<,>), Conditional: false)])));
        });
        await using var invoker = new ConnectionHandlerInvoker(provider, Sink("user-1"), PerConnection);

        await invoker.InvokeAsync(new Request("a"), TestContext.Current.CancellationToken);
        await invoker.InvokeAsync(new Request("b"), TestContext.Current.CancellationToken);

        log.Warnings.Should().ContainSingle().Which.Should().Contain(nameof(ProbeHandler));
    }

    [Fact]
    public async Task PerMessageMode_NeverWarnsForTransactionalPipeline() {
        var log = new CapturingLoggerFactory();
        using var provider = CreateProvider(services => {
            services.AddSingleton<ILoggerFactory>(log);
            services.Add(ServiceDescriptor.KeyedSingleton(
                typeof(Request),
                new HandlerMetadata(
                    typeof(ProbeHandler), typeof(Request), typeof(Result<Response>),
                    static () => [new PipelineStep(typeof(TransactionDecorator<,>), Conditional: false)])));
        });
        await using var invoker = new ConnectionHandlerInvoker(provider, Sink("user-1"));

        await invoker.InvokeAsync(new Request("a"), TestContext.Current.CancellationToken);

        log.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task PerConnectionMode_StreamsStillOwnTheirOwnScope() {
        ScopedProbe? streamProbe = null;
        using var provider = CreateProvider(services =>
            services.AddScoped<IStreamHandler<StreamRequest, int>>(sp => {
                streamProbe = sp.GetRequiredService<ScopedProbe>();
                return new CountingStreamHandler();
            }));
        await using var invoker = new ConnectionHandlerInvoker(provider, Sink("user-1"), PerConnection);

        var unary = await invoker.InvokeAsync(new Request("a"), TestContext.Current.CancellationToken);
        var start = await invoker.InvokeStreamAsync<StreamRequest, int>(
            new StreamRequest(), TestContext.Current.CancellationToken);

        start.IsSuccess.Should().BeTrue();
        var values = new List<int>();
        await foreach (var value in start.Value!)
            values.Add(value);

        values.Should().Equal(1, 2);
        // The stream ran in its own scope (own probe instance) and disposed it at terminal enumeration; the
        // connection scope's probe lives on.
        streamProbe.Should().NotBeSameAs(unary.Value!.Probe);
        streamProbe!.Disposed.Should().BeTrue();
        unary.Value!.Probe.Disposed.Should().BeFalse();
    }

    [Fact]
    public async Task PerConnectionMode_NamedDispatchReusesTheConnectionScope() {
        var dispatcher = new HandlerDispatcher()
            .MapDelegate<Request, ScopedProbe>(
                "probe.get",
                (_, services, _) => ValueTask.FromResult<Result<ScopedProbe>>(
                    services.GetRequiredService<ScopedProbe>()),
                HandlerTransports.Connection)
            .Freeze();
        using var provider = CreateProvider();
        await using var invoker = new ConnectionHandlerInvoker(provider, Sink("user-1"), PerConnection);

        var first = await invoker.InvokeNamedAsync(
            dispatcher, "probe.get", new Request("a"), TestContext.Current.CancellationToken);
        var second = await invoker.InvokeNamedAsync(
            dispatcher, "probe.get", new Request("b"), TestContext.Current.CancellationToken);

        second.Value.Should().BeSameAs(first.Value);
    }

    private static ServiceProvider CreateProvider(Action<IServiceCollection>? configure = null) {
        var services = new ServiceCollection()
            .AddScoped<ScopedProbe>()
            .AddScoped<MessageValues>()
            .AddSingleton<IDispatchScopeInitializer, MessageValuesInitializer>()
            .AddScoped<IHandler<Request, Result<Response>>, ProbeHandler>()
            .AddElarionClaimsCurrentUser(o => o.RoleClaimType = "role")
            .AddElarionIdempotency();
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    private static SimulatedSink Sink(string subject) {
        return new SimulatedSink(Connection("c1", Principal(subject, "guest")));
    }

    private static ClaimsPrincipal Principal(string subject, string role) {
        return new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", subject), new Claim("role", role)], "test"));
    }

    private static ClientConnection Connection(string id, ClaimsPrincipal principal, long revision = 0) {
        return new ClientConnection {
            ConnectionId = id,
            Transport = "test",
            Principal = principal,
            PrincipalId = principal.FindFirst("sub")?.Value,
            ConnectedAt = DateTimeOffset.UnixEpoch,
            IdentityRevision = revision
        };
    }

    private sealed class ScopedProbe : IDisposable {
        public bool Disposed { get; private set; }

        public void Dispose() {
            Disposed = true;
        }
    }

    private sealed class MessageValues {
        public CustomMetadata? Metadata { get; set; }
    }

    /// <summary>Always assigns (null clears) — the same overwrite-per-message contract the framework
    /// initializers follow so a reused scope never carries the previous message's value.</summary>
    private sealed class MessageValuesInitializer : IDispatchScopeInitializer {
        public void Initialize(IServiceProvider callScope, DispatchScopeContext context) {
            context.TryGet<CustomMetadata>(out var metadata);
            callScope.GetRequiredService<MessageValues>().Metadata = metadata;
        }
    }

    private sealed class ProbeHandler(
        ScopedProbe probe,
        ICurrentUser user,
        IIdempotencyKeyAccessor idempotency,
        MessageValues messageValues) : IHandler<Request, Result<Response>> {
        public ValueTask<Result<Response>> HandleAsync(Request request, CancellationToken ct) {
            idempotency.TryGetKey(out var key);
            return ValueTask.FromResult<Result<Response>>(
                new Response(probe, [.. user.Roles], key, messageValues.Metadata));
        }
    }

    private sealed class CountingStreamHandler : IStreamHandler<StreamRequest, int> {
        public ValueTask<Result<IAsyncEnumerable<int>>> HandleAsync(StreamRequest request, CancellationToken ct) {
            return ValueTask.FromResult(Result<IAsyncEnumerable<int>>.Success(Items()));
        }

        private static async IAsyncEnumerable<int> Items() {
            yield return 1;
            await Task.Yield();
            yield return 2;
        }
    }

    private sealed class SimulatedSink(ClientConnection connection) : IClientConnectionSink {
        public ClientConnectionState ConnectionState { get; } = new(connection);

        public ClientConnection Connection => ConnectionState.Current;

        public ValueTask SendAsync<TPayload>(string name, TPayload payload, CancellationToken ct = default)
            where TPayload : class {
            throw new NotSupportedException();
        }

        public ValueTask<TResponse> InvokeAsync<TRequest, TResponse>(
            string name, TRequest request, ClientInvokeOptions? options = null, CancellationToken ct = default)
            where TRequest : class {
            throw new NotSupportedException();
        }
    }

    private sealed class MutableSink(ClientConnection connection) : IClientConnectionSink {
        public ClientConnectionState ConnectionState { get; } = new(connection);

        public ClientConnection Current { get; set; } = connection;

        public ClientConnection Connection => Current;

        public ValueTask SendAsync<TPayload>(string name, TPayload payload, CancellationToken ct = default)
            where TPayload : class {
            throw new NotSupportedException();
        }

        public ValueTask<TResponse> InvokeAsync<TRequest, TResponse>(
            string name, TRequest request, ClientInvokeOptions? options = null, CancellationToken ct = default)
            where TRequest : class {
            throw new NotSupportedException();
        }
    }

    private sealed class CapturingLoggerFactory : ILoggerFactory {
        public List<string> Warnings { get; } = [];

        public ILogger CreateLogger(string categoryName) {
            return new CapturingLogger(this);
        }

        public void AddProvider(ILoggerProvider provider) {
        }

        public void Dispose() {
        }

        private sealed class CapturingLogger(CapturingLoggerFactory owner) : ILogger {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull {
                return null;
            }

            public bool IsEnabled(LogLevel logLevel) {
                return true;
            }

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) {
                if (logLevel == LogLevel.Warning)
                    owner.Warnings.Add(formatter(state, exception));
            }
        }
    }
}
