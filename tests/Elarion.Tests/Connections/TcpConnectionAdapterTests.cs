using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Text;
using System.Threading.Channels;
using AwesomeAssertions;
using Elarion.Abstractions.Connections;
using Elarion.Connections;
using Elarion.Connections.Tcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Elarion.Tests.Connections;

/// <summary>
/// Covers the TCP adapter: framer mechanics (partial delivery, multiple messages per chunk, noise skip),
/// the listener end-to-end over real loopback sockets (framed challenge/response handshake, echo, registry
/// lifecycle), the dialer with reconnect after the peer drops, and the idle hook firing a protocol
/// keepalive on a silent link.
/// </summary>
public sealed class TcpConnectionAdapterTests {
    [Fact]
    public void LengthPrefixedFramer_HandlesPartialAndBackToBackMessages() {
        var framer = new LengthPrefixedTcpFramer();
        var writer = new ArrayBufferWriter<byte>();
        framer.WriteMessage(new TcpFramedMessage(TcpMessageKind.Binary, new byte[] { 1, 2, 3 }), writer);
        framer.WriteMessage(new TcpFramedMessage(TcpMessageKind.Binary, new byte[] { 9 }), writer);
        var wire = writer.WrittenMemory;

        framer.TryReadMessage(wire[..3], out _, out _).Should().BeFalse();
        framer.TryReadMessage(wire[..6], out _, out _).Should().BeFalse();

        framer.TryReadMessage(wire, out var consumed, out var first).Should().BeTrue();
        consumed.Should().Be(7);
        first.Payload.ToArray().Should().Equal(1, 2, 3);

        framer.TryReadMessage(wire[consumed..], out var consumedSecond, out var second).Should().BeTrue();
        consumedSecond.Should().Be(5);
        second.Payload.ToArray().Should().Equal(9);
    }

    [Fact]
    public void DelimitedFramer_SkipsNoiseBeforeStart_AndRoundTrips() {
        var framer = new DelimitedTextTcpFramer(end: (byte)'>', start: (byte)'<');
        var wire = Encoding.UTF8.GetBytes("garbage<hello>");

        framer.TryReadMessage(wire, out var consumed, out var message).Should().BeTrue();
        consumed.Should().Be(wire.Length);
        Encoding.UTF8.GetString(message.Payload.Span).Should().Be("hello");

        var writer = new ArrayBufferWriter<byte>();
        framer.WriteMessage(new TcpFramedMessage(TcpMessageKind.Text, Encoding.UTF8.GetBytes("out")), writer);
        Encoding.UTF8.GetString(writer.WrittenSpan).Should().Be("<out>");
    }

    [Fact]
    public async Task Listener_HandshakeRegistryEchoAndTeardown() {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartListenerHostAsync(ct);
        using var client = await host.ConnectAsync(ct);
        var stream = client.GetStream();

        (await ReadLineAsync(stream, ct)).Should().Be("challenge");
        await WriteLineAsync(stream, "device:tcp-1", ct);
        (await ReadLineAsync(stream, ct)).Should().Be("welcome");

        var connected = await host.Observer.Connected.Task.WaitAsync(ct);
        connected.PrincipalId.Should().Be("tcp-1");
        connected.Transport.Should().Be("tcp");
        host.Registry.GetForPrincipal("tcp-1").Should().ContainSingle();

        await WriteLineAsync(stream, "ping", ct);
        (await ReadLineAsync(stream, ct)).Should().Be("echo:ping");

        client.Close();
        await host.Observer.Disconnected.Task.WaitAsync(ct);
        host.Registry.Connections.Should().BeEmpty();
    }

    [Fact]
    public async Task Listener_RejectedHandshake_RegistersNothing() {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartListenerHostAsync(ct);
        using var client = await host.ConnectAsync(ct);
        var stream = client.GetStream();

        (await ReadLineAsync(stream, ct)).Should().Be("challenge");
        await WriteLineAsync(stream, "intruder", ct);

        (await ReadLineAsync(stream, ct)).Should().BeNull();
        host.Registry.Connections.Should().BeEmpty();
    }

    [Fact]
    public async Task Listener_IdleHook_SendsProtocolKeepaliveOnSilentLink() {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartListenerHostAsync(ct, idleTimeout: TimeSpan.FromMilliseconds(50));
        using var client = await host.ConnectAsync(ct);
        var stream = client.GetStream();

        (await ReadLineAsync(stream, ct)).Should().Be("challenge");
        await WriteLineAsync(stream, "device:tcp-idle", ct);
        (await ReadLineAsync(stream, ct)).Should().Be("welcome");
        await host.Observer.Connected.Task.WaitAsync(ct);

        // Send nothing: the idle window elapses and the codec's OnIdleAsync pushes its keepalive.
        (await ReadLineAsync(stream, ct)).Should().Be("idle-poll");
    }

    [Fact]
    public async Task PerConnectionSettings_ServeDifferentFramingsOnOneEndpoint() {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartListenerHostAsync<MultiFramingHandler>(ct);

        // First connection: inherits the endpoint's newline framing, tagged per-connection.
        using (var lines = await host.ConnectAsync(ct)) {
            var stream = lines.GetStream();
            (await ReadLineAsync(stream, ct)).Should().Be("challenge");
            await WriteLineAsync(stream, "device:lines-1", ct);
            (await ReadLineAsync(stream, ct)).Should().Be("welcome");
            (await host.Observer.Connected.Task.WaitAsync(ct)).Transport.Should().Be("tcp-lines");

            await WriteLineAsync(stream, "ping", ct);
            (await ReadLineAsync(stream, ct)).Should().Be("echo:ping");
        }

        await host.Observer.Disconnected.Task.WaitAsync(ct);
        host.Observer.Reset();

        // Second connection, same endpoint: the handler's per-connection settings switch it to '|' framing
        // (the binding-configuration shape — the framer applies from the very first handshake byte).
        using var pipes = await host.ConnectAsync(ct);
        var pipeStream = pipes.GetStream();
        (await ReadUntilAsync(pipeStream, (byte)'|', ct)).Should().Be("challenge");
        await pipeStream.WriteAsync(Encoding.UTF8.GetBytes("device:pipes-1|"), ct);
        (await ReadUntilAsync(pipeStream, (byte)'|', ct)).Should().Be("welcome");
        (await host.Observer.Connected.Task.WaitAsync(ct)).Transport.Should().Be("tcp-pipes");

        await pipeStream.WriteAsync(Encoding.UTF8.GetBytes("ping|"), ct);
        (await ReadUntilAsync(pipeStream, (byte)'|', ct)).Should().Be("echo:ping");
    }

    [Fact]
    public async Task Dialer_ConnectsAuthenticatesAndReconnectsAfterPeerDrop() {
        var ct = TestContext.Current.CancellationToken;
        var device = new TcpListener(IPAddress.Loopback, 0);
        device.Start();
        var port = ((IPEndPoint)device.LocalEndpoint).Port;

        var observer = new AwaitableConnectionObserver();
        var inbound = Channel.CreateUnbounded<string>();
        var services = new ServiceCollection();
        services.AddElarionConnections();
        services.AddSingleton(observer);
        services.AddSingleton<IClientConnectionObserver>(sp => sp.GetRequiredService<AwaitableConnectionObserver>());
        services.AddSingleton(new DialerHandler(inbound.Writer));
        services.AddElarionTcpConnectionDialer<DialerHandler>(o => {
            o.Host = "127.0.0.1";
            o.Port = port;
            o.Framer = new DelimitedTextTcpFramer(end: (byte)'\n');
            o.ReconnectMinDelay = TimeSpan.FromMilliseconds(50);
            o.ReconnectMaxDelay = TimeSpan.FromMilliseconds(200);
        });
        var provider = services.BuildServiceProvider();
        var hosted = provider.GetServices<IHostedService>().ToArray();
        foreach (var service in hosted) {
            await service.StartAsync(ct);
        }

        try {
            // First session: the dialer initiates, the handler introduces itself, the device tickets it.
            using (var session = await device.AcceptTcpClientAsync(ct)) {
                var stream = session.GetStream();
                (await ReadLineAsync(stream, ct)).Should().Be("hello");
                await WriteLineAsync(stream, "ticket:dev-9", ct);
                (await observer.Connected.Task.WaitAsync(ct)).PrincipalId.Should().Be("dev-9");

                await WriteLineAsync(stream, "reading:42", ct);
                (await inbound.Reader.ReadAsync(ct)).Should().Be("reading:42");
            }

            // The device dropped the link: the dialer unregisters and redials with backoff.
            await observer.Disconnected.Task.WaitAsync(ct);
            observer.Reset();
            using var second = await device.AcceptTcpClientAsync(ct);
            var secondStream = second.GetStream();
            (await ReadLineAsync(secondStream, ct)).Should().Be("hello");
            await WriteLineAsync(secondStream, "ticket:dev-9", ct);
            (await observer.Connected.Task.WaitAsync(ct)).PrincipalId.Should().Be("dev-9");
        }
        finally {
            foreach (var service in hosted) {
                await service.StopAsync(CancellationToken.None);
            }
            await provider.DisposeAsync();
            device.Stop();
        }
    }

    private static Task<TcpTestHost> StartListenerHostAsync(CancellationToken ct, TimeSpan? idleTimeout = null) =>
        StartListenerHostAsync<ChallengeTcpHandler>(ct, idleTimeout);

    private static async Task<TcpTestHost> StartListenerHostAsync<THandler>(
        CancellationToken ct, TimeSpan? idleTimeout = null)
        where THandler : TcpConnectionHandler, new() {
        var boundEndPoint = new TaskCompletionSource<IPEndPoint>(TaskCreationOptions.RunContinuationsAsynchronously);
        var services = new ServiceCollection();
        services.AddElarionConnections();
        services.AddSingleton<THandler>();
        services.AddSingleton<AwaitableConnectionObserver>();
        services.AddSingleton<IClientConnectionObserver>(sp => sp.GetRequiredService<AwaitableConnectionObserver>());
        services.AddElarionTcpConnectionListener<THandler>(o => {
            o.ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0);
            o.Framer = new DelimitedTextTcpFramer(end: (byte)'\n');
            o.IdleTimeout = idleTimeout;
            o.OnListening = boundEndPoint.SetResult;
        });
        var provider = services.BuildServiceProvider();
        var hosted = provider.GetServices<IHostedService>().ToArray();
        foreach (var service in hosted) {
            await service.StartAsync(ct);
        }

        return new TcpTestHost(provider, hosted, await boundEndPoint.Task.WaitAsync(ct));
    }

    private static async Task WriteLineAsync(NetworkStream stream, string line, CancellationToken ct) =>
        await stream.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"), ct);

    private static Task<string?> ReadLineAsync(NetworkStream stream, CancellationToken ct) =>
        ReadUntilAsync(stream, (byte)'\n', ct);

    private static async Task<string?> ReadUntilAsync(NetworkStream stream, byte delimiter, CancellationToken ct) {
        var buffer = new List<byte>();
        var single = new byte[1];
        while (true) {
            var read = await stream.ReadAsync(single.AsMemory(), ct);
            if (read == 0) {
                return null;
            }

            if (single[0] == delimiter) {
                return Encoding.UTF8.GetString(buffer.ToArray());
            }

            buffer.Add(single[0]);
        }
    }

    private sealed class TcpTestHost(
        ServiceProvider provider, IReadOnlyList<IHostedService> hosted, IPEndPoint endPoint) : IAsyncDisposable {
        public IClientConnectionRegistry Registry => provider.GetRequiredService<IClientConnectionRegistry>();

        public AwaitableConnectionObserver Observer => provider.GetRequiredService<AwaitableConnectionObserver>();

        public async Task<TcpClient> ConnectAsync(CancellationToken ct) {
            var client = new TcpClient();
            await client.ConnectAsync(endPoint, ct);
            return client;
        }

        public async ValueTask DisposeAsync() {
            foreach (var service in hosted) {
                await service.StopAsync(CancellationToken.None);
            }
            await provider.DisposeAsync();
        }
    }

    /// <summary>Listener-side: in-socket challenge/response + an echo codec with an idle keepalive.</summary>
    private sealed class ChallengeTcpHandler : TcpConnectionHandler {
        public override async ValueTask<ClientConnectionTicket?> AuthenticateAsync(
            TcpHandshakeContext handshake, CancellationToken ct) {
            await handshake.SendTextAsync("challenge", ct);
            var reply = await handshake.ReceiveTextAsync(ct);
            if (reply is null || !reply.StartsWith("device:", StringComparison.Ordinal)) {
                return null;
            }

            await handshake.SendTextAsync("welcome", ct);
            return new ClientConnectionTicket {
                Principal = new ClaimsPrincipal(new ClaimsIdentity(authenticationType: "device")),
                PrincipalId = reply["device:".Length..],
            };
        }

        public override IClientConnectionProtocol CreateProtocol(TcpClientConnection connection) =>
            new EchoTcpProtocol(connection);
    }

    private sealed class EchoTcpProtocol(TcpClientConnection connection) : IClientConnectionProtocol {
        public ValueTask OnTextAsync(string message, CancellationToken ct) =>
            connection.SendTextAsync("echo:" + message, ct);

        public ValueTask OnIdleAsync(CancellationToken ct) =>
            connection.SendTextAsync("idle-poll", ct);
    }

    /// <summary>One endpoint, two wire framings: the first connection keeps the endpoint defaults, every
    /// later one is switched to '|' delimiters via per-connection settings.</summary>
    private sealed class MultiFramingHandler : TcpConnectionHandler {
        private int _connections;

        public override ValueTask<TcpConnectionSettings?> ConfigureConnectionAsync(
            TcpConnectionPeer peer, CancellationToken ct) {
            var settings = Interlocked.Increment(ref _connections) == 1
                ? new TcpConnectionSettings { Transport = "tcp-lines" }
                : new TcpConnectionSettings {
                    Framer = new DelimitedTextTcpFramer(end: (byte)'|'),
                    Transport = "tcp-pipes",
                };
            return ValueTask.FromResult<TcpConnectionSettings?>(settings);
        }

        public override async ValueTask<ClientConnectionTicket?> AuthenticateAsync(
            TcpHandshakeContext handshake, CancellationToken ct) {
            await handshake.SendTextAsync("challenge", ct);
            var reply = await handshake.ReceiveTextAsync(ct);
            if (reply is null || !reply.StartsWith("device:", StringComparison.Ordinal)) {
                return null;
            }

            await handshake.SendTextAsync("welcome", ct);
            return new ClientConnectionTicket {
                Principal = new ClaimsPrincipal(new ClaimsIdentity(authenticationType: "device")),
                PrincipalId = reply["device:".Length..],
            };
        }

        public override IClientConnectionProtocol CreateProtocol(TcpClientConnection connection) =>
            new EchoTcpProtocol(connection);
    }

    /// <summary>Dialer-side: introduces itself and expects a ticket line from the device.</summary>
    private sealed class DialerHandler(ChannelWriter<string> inbound) : TcpConnectionHandler {
        public override async ValueTask<ClientConnectionTicket?> AuthenticateAsync(
            TcpHandshakeContext handshake, CancellationToken ct) {
            await handshake.SendTextAsync("hello", ct);
            var reply = await handshake.ReceiveTextAsync(ct);
            if (reply is null || !reply.StartsWith("ticket:", StringComparison.Ordinal)) {
                return null;
            }

            return new ClientConnectionTicket {
                Principal = new ClaimsPrincipal(new ClaimsIdentity(authenticationType: "device")),
                PrincipalId = reply["ticket:".Length..],
            };
        }

        public override IClientConnectionProtocol CreateProtocol(TcpClientConnection connection) =>
            new RecordingTcpProtocol(inbound);
    }

    private sealed class RecordingTcpProtocol(ChannelWriter<string> inbound) : IClientConnectionProtocol {
        public ValueTask OnTextAsync(string message, CancellationToken ct) => inbound.WriteAsync(message, ct);
    }

    private sealed class AwaitableConnectionObserver : IClientConnectionObserver {
        private TaskCompletionSource<ClientConnection> _connected = NewSource();
        private TaskCompletionSource<ClientConnection> _disconnected = NewSource();

        public TaskCompletionSource<ClientConnection> Connected => _connected;

        public TaskCompletionSource<ClientConnection> Disconnected => _disconnected;

        public void Reset() {
            _connected = NewSource();
            _disconnected = NewSource();
        }

        public ValueTask OnConnectedAsync(IClientConnectionSink connection, CancellationToken ct = default) {
            _connected.TrySetResult(connection.Connection);
            return ValueTask.CompletedTask;
        }

        public ValueTask OnDisconnectedAsync(ClientConnection connection, CancellationToken ct = default) {
            _disconnected.TrySetResult(connection);
            return ValueTask.CompletedTask;
        }

        private static TaskCompletionSource<ClientConnection> NewSource() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
