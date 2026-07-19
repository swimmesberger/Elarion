using System.Security.Claims;
using AwesomeAssertions;
using Elarion.Abstractions.Connections;
using Elarion.Connections;
using Elarion.Connections.Simulation;
using Elarion.Connections.Tcp;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elarion.Tests.Connections;

/// <summary>
/// Covers the default invoke timeout (<see cref="ElarionConnectionsOptions.DefaultInvokeTimeout"/>): the
/// per-call &gt; kernel-default &gt; unbounded layering, the sink-side normalization every adapter applies
/// before its codec sees the options, and the kernel options registration.
/// </summary>
public sealed class ClientInvokeTimeoutTests {
    [Fact]
    public async Task DefaultTimeout_BoundsAnInvokeWhoseResponderNeverAnswers() {
        var ct = TestContext.Current.CancellationToken;
        var connection = new SimulatedClientConnection {
            DefaultInvokeTimeout = TimeSpan.FromMilliseconds(150),
            InvokeResponder = (_, _) => new ValueTask<object>(new TaskCompletionSource<object>().Task)
        };

        var invoke = async () => await connection.InvokeAsync<string, string>("status.get", "q", ct: ct);

        await invoke.Should().ThrowAsync<TimeoutException>();
    }

    [Fact]
    public async Task PerCallTimeout_ShorterThanTheDefault_Wins() {
        var ct = TestContext.Current.CancellationToken;
        var connection = new SimulatedClientConnection {
            DefaultInvokeTimeout = TimeSpan.FromMinutes(5),
            InvokeResponder = (_, _) => new ValueTask<object>(new TaskCompletionSource<object>().Task)
        };

        var invoke = async () => await connection.InvokeAsync<string, string>(
            "status.get", "q", new ClientInvokeOptions { Timeout = TimeSpan.FromMilliseconds(150) }, ct);

        // Only the per-call 150 ms timer exists — the five-minute default would hang the test instead.
        await invoke.Should().ThrowAsync<TimeoutException>();
    }

    [Fact]
    public async Task PerCallTimeout_LongerThanTheDefault_Wins() {
        var ct = TestContext.Current.CancellationToken;
        var gate = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = new SimulatedClientConnection {
            DefaultInvokeTimeout = TimeSpan.FromMilliseconds(50),
            InvokeResponder = (_, _) => new ValueTask<object>(gate.Task)
        };

        var invoke = connection.InvokeAsync<string, string>(
            "status.get", "q", new ClientInvokeOptions { Timeout = TimeSpan.FromSeconds(30) }, ct).AsTask();

        // Well past the 50 ms default: had it applied, the invoke would already be faulted. With the
        // per-call timeout in force no short timer exists at all, so this is load-insensitive.
        await Task.Delay(300, ct);
        gate.SetResult("ready");

        (await invoke).Should().Be("ready");
    }

    [Fact]
    public async Task NullDefault_LeavesTheInvokeUnbounded() {
        var ct = TestContext.Current.CancellationToken;
        var gate = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = new SimulatedClientConnection {
            DefaultInvokeTimeout = null,
            InvokeResponder = (_, _) => new ValueTask<object>(gate.Task)
        };

        var invoke = connection.InvokeAsync<string, string>("status.get", "q", ct: ct).AsTask();

        await Task.Delay(250, ct);
        invoke.IsCompleted.Should().BeFalse();
        gate.SetResult("late");

        (await invoke).Should().Be("late");
    }

    [Fact]
    public async Task ResponsiveInvoke_IsUnaffectedByTheDefault() {
        var ct = TestContext.Current.CancellationToken;
        var connection = new SimulatedClientConnection {
            InvokeResponder = (name, request) => ValueTask.FromResult<object>($"{name}:{request}:ack")
        };

        // The double ships the same default real adapters get from the kernel options.
        connection.DefaultInvokeTimeout.Should().Be(TimeSpan.FromSeconds(30));
        (await connection.InvokeAsync<string, string>("start", "now", ct: ct)).Should().Be("start:now:ack");
    }

    [Fact]
    public void WithDefaultTimeout_LayersPerCallOverDefaultOverUnbounded() {
        var defaultTimeout = TimeSpan.FromSeconds(30);
        var perCall = new ClientInvokeOptions { Timeout = TimeSpan.FromSeconds(5) };
        var noTimeout = new ClientInvokeOptions();

        // No options → the default materializes.
        ((ClientInvokeOptions?)null).WithDefaultTimeout(defaultTimeout)!.Timeout.Should().Be(defaultTimeout);
        // Options without a timeout → cloned with the default; the caller's instance stays untouched.
        noTimeout.WithDefaultTimeout(defaultTimeout)!.Timeout.Should().Be(defaultTimeout);
        noTimeout.Timeout.Should().BeNull();
        // A per-call timeout always wins — including the explicit per-call escape to unbounded.
        perCall.WithDefaultTimeout(defaultTimeout).Should().BeSameAs(perCall);
        var unbounded = new ClientInvokeOptions { Timeout = Timeout.InfiniteTimeSpan };
        unbounded.WithDefaultTimeout(defaultTimeout).Should().BeSameAs(unbounded);
        // A null default changes nothing.
        ((ClientInvokeOptions?)null).WithDefaultTimeout(null).Should().BeNull();
        noTimeout.WithDefaultTimeout(null).Should().BeSameAs(noTimeout);
    }

    [Fact]
    public void AddElarionConnections_ComposesConfigureOntoOneInstance_AndValidates() {
        var services = new ServiceCollection();
        services.AddElarionConnections(o => o.DefaultInvokeTimeout = TimeSpan.FromSeconds(10));
        services.AddElarionConnections();

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<ElarionConnectionsOptions>()
            .DefaultInvokeTimeout.Should().Be(TimeSpan.FromSeconds(10));

        var invalid = () => new ServiceCollection()
            .AddElarionConnections(o => o.DefaultInvokeTimeout = TimeSpan.FromSeconds(-1));
        invalid.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task TcpSink_ResolvesTheDefaultIntoOptions_BeforeTheCodecSeesThem() {
        var ct = TestContext.Current.CancellationToken;
        await using var provider = new ServiceCollection().AddElarionConnections().BuildServiceProvider();
        var registry = provider.GetRequiredService<IClientConnectionRegistry>();

        await using var link = InMemoryTcpLink.Start(new TimeoutEchoHandler(), registry,
            o => o.Framer = new DelimitedTcpFramer((byte)'\n'));
        (await link.Client.ReceiveTextAsync(ct)).Should().Be("challenge");
        await link.Client.SendTextAsync("device:sim-1", ct);
        (await link.Client.ReceiveTextAsync(ct)).Should().Be("welcome");
        var sink = await link.ServerConnection.WaitAsync(ct);

        // No per-call timeout → the codec observes the kernel's shipped 30 s default in options.
        (await sink.InvokeAsync<string, string>("timeout.echo", "q", ct: ct))
            .Should().Be(TimeSpan.FromSeconds(30).ToString());
        // A per-call timeout reaches the codec untouched.
        (await sink.InvokeAsync<string, string>(
                "timeout.echo", "q", new ClientInvokeOptions { Timeout = TimeSpan.FromSeconds(5) }, ct))
            .Should().Be(TimeSpan.FromSeconds(5).ToString());
    }

    /// <summary>In-socket challenge/response + a codec that answers invokes with the timeout it saw in
    /// options — the probe for sink-side normalization.</summary>
    private sealed class TimeoutEchoHandler : TcpConnectionHandler {
        public override async ValueTask<ClientConnectionTicket?> AuthenticateAsync(
            TcpHandshakeContext handshake, CancellationToken ct) {
            await handshake.SendTextAsync("challenge", ct);
            var reply = await handshake.ReceiveTextAsync(ct);
            if (reply is null || !reply.StartsWith("device:", StringComparison.Ordinal)) return null;

            await handshake.SendTextAsync("welcome", ct);
            return new ClientConnectionTicket {
                Principal = new ClaimsPrincipal(new ClaimsIdentity("device")),
                PrincipalId = reply["device:".Length..]
            };
        }

        public override IClientConnectionProtocol CreateProtocol(TcpClientConnection connection) {
            return new TimeoutEchoProtocol();
        }
    }

    private sealed class TimeoutEchoProtocol : IClientConnectionProtocol {
        public ValueTask OnBinaryAsync(ReadOnlyMemory<byte> message, CancellationToken ct) {
            return ValueTask.CompletedTask;
        }

        public ValueTask<TResponse> InvokeAsync<TRequest, TResponse>(
            string name, TRequest request, ClientInvokeOptions? options, CancellationToken ct)
            where TRequest : class {
            return ValueTask.FromResult((TResponse)(object)(options?.Timeout?.ToString() ?? "none"));
        }
    }
}
