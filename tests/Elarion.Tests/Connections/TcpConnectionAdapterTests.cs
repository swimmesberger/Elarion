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
using Elarion.Connections.Simulation;
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
        framer.WriteMessage(new byte[] { 1, 2, 3 }, writer);
        framer.WriteMessage(new byte[] { 9 }, writer);
        var wire = writer.WrittenMemory;

        framer.TryReadMessage(wire[..3], out _, out _).Should().BeFalse();
        framer.TryReadMessage(wire[..6], out _, out _).Should().BeFalse();

        framer.TryReadMessage(wire, out var consumed, out var first).Should().BeTrue();
        consumed.Should().Be(7);
        first.ToArray().Should().Equal(1, 2, 3);

        framer.TryReadMessage(wire[consumed..], out var consumedSecond, out var second).Should().BeTrue();
        consumedSecond.Should().Be(5);
        second.ToArray().Should().Equal(9);
    }

    [Fact]
    public void DelimitedFramer_SkipsNoiseBeforeStart_AndRoundTrips() {
        var framer = new DelimitedTcpFramer(end: (byte)'>', start: (byte)'<');
        var wire = Encoding.UTF8.GetBytes("garbage<hello>");

        framer.TryReadMessage(wire, out var consumed, out var message).Should().BeTrue();
        consumed.Should().Be(wire.Length);
        Encoding.UTF8.GetString(message.Span).Should().Be("hello");

        var writer = new ArrayBufferWriter<byte>();
        framer.WriteMessage(Encoding.UTF8.GetBytes("out"), writer);
        Encoding.UTF8.GetString(writer.WrittenSpan).Should().Be("<out>");
    }

    [Fact]
    public void DelimitedFramer_WriteMessage_RejectsPayloadContainingTheEndDelimiter() {
        var framer = new DelimitedTcpFramer(end: (byte)'\n');
        var writer = new ArrayBufferWriter<byte>();
        var payload = Encoding.UTF8.GetBytes("first\nsecond");

        // Silently emitting the delimiter would make the peer parse one message as two — fail loud instead.
        var write = () => framer.WriteMessage(payload, writer);
        write.Should().Throw<ArgumentException>().WithMessage("*end delimiter*");
        writer.WrittenCount.Should().Be(0);
    }

    [Fact]
    public void DelimitedFramer_WriteMessage_RejectsPayloadContainingTheStartDelimiter() {
        var framer = new DelimitedTcpFramer(end: (byte)'>', start: (byte)'<');
        var writer = new ArrayBufferWriter<byte>();
        var payload = Encoding.UTF8.GetBytes("tele<gram");

        var write = () => framer.WriteMessage(payload, writer);
        write.Should().Throw<ArgumentException>().WithMessage("*start delimiter*");
        writer.WrittenCount.Should().Be(0);
    }

    [Fact]
    public void DelimitedFramer_ConsumesNoiseEvenWithoutACompleteMessage() {
        var framer = new DelimitedTcpFramer(end: (byte)'>', start: (byte)'<');

        // Pure noise, no start byte: fully consumed so it can never accumulate against the size cap.
        var noise = Encoding.UTF8.GetBytes("static......");
        framer.TryReadMessage(noise, out var consumed, out _).Should().BeFalse();
        consumed.Should().Be(noise.Length);

        // Noise followed by an incomplete message: the noise is dropped, the partial message kept.
        var partial = Encoding.UTF8.GetBytes("zzz<hel");
        framer.TryReadMessage(partial, out consumed, out _).Should().BeFalse();
        consumed.Should().Be(3);
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

        var connected = (await host.Observer.Connected.Task.WaitAsync(ct)).Connection;
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
    public async Task TinyInitialBuffers_GrowThroughTheEchoRoundTrip() {
        var ct = TestContext.Current.CancellationToken;
        // Deliberately smaller than the handshake lines and the payload: exercises the read-buffer growth
        // and the send-buffer growth paths that the defaults normally hide.
        await using var host = await StartListenerHostAsync<ChallengeTcpHandler>(ct, configure: o => {
            o.InitialReadBufferBytes = 4;
            o.InitialSendBufferBytes = 4;
        });
        using var client = await host.ConnectAsync(ct);
        var stream = client.GetStream();

        (await ReadLineAsync(stream, ct)).Should().Be("challenge");
        await WriteLineAsync(stream, "device:tcp-grow", ct);
        (await ReadLineAsync(stream, ct)).Should().Be("welcome");
        await host.Observer.Connected.Task.WaitAsync(ct);

        var payload = new string('y', 500);
        await WriteLineAsync(stream, payload, ct);
        (await ReadLineAsync(stream, ct)).Should().Be("echo:" + payload);
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
            (await host.Observer.Connected.Task.WaitAsync(ct)).Connection.Transport.Should().Be("tcp-lines");

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
        (await host.Observer.Connected.Task.WaitAsync(ct)).Connection.Transport.Should().Be("tcp-pipes");

        await pipeStream.WriteAsync(Encoding.UTF8.GetBytes("ping|"), ct);
        (await ReadUntilAsync(pipeStream, (byte)'|', ct)).Should().Be("echo:ping");
    }

    [Fact]
    public async Task Codec_OnClosedAsync_RunsWithNullReasonOnCleanClose() {
        var ct = TestContext.Current.CancellationToken;
        await using var provider = new ServiceCollection().AddElarionConnections().BuildServiceProvider();
        var registry = provider.GetRequiredService<IClientConnectionRegistry>();
        var handler = new ClosureRecordingHandler();

        await using (var link = InMemoryTcpLink.Start(handler, registry,
                         o => o.Framer = new DelimitedTcpFramer(end: (byte)'\n'))) {
            (await link.Client.ReceiveTextAsync(ct)).Should().Be("challenge");
            await link.Client.SendTextAsync("device:closed-1", ct);
            (await link.Client.ReceiveTextAsync(ct)).Should().Be("welcome");
            await link.ServerConnection.WaitAsync(ct);
        }

        // Disposing the link closes the client end: the server observes EOF — a clean close.
        var (connection, reason) = await handler.Protocol!.Closed.Task.WaitAsync(ct);
        connection.PrincipalId.Should().Be("closed-1");
        reason.Should().BeNull();
    }

    [Fact]
    public async Task Codec_OnClosedAsync_ReceivesTheTerminatingFailure_AndItsOwnThrowNeverBreaksTeardown() {
        var ct = TestContext.Current.CancellationToken;
        await using var provider = new ServiceCollection().AddElarionConnections().BuildServiceProvider();
        var registry = provider.GetRequiredService<IClientConnectionRegistry>();
        var handler = new ClosureRecordingHandler(throwOnMessage: true, throwOnClosed: true);

        await using var link = InMemoryTcpLink.Start(handler, registry,
            o => o.Framer = new DelimitedTcpFramer(end: (byte)'\n'));
        (await link.Client.ReceiveTextAsync(ct)).Should().Be("challenge");
        await link.Client.SendTextAsync("device:closed-2", ct);
        (await link.Client.ReceiveTextAsync(ct)).Should().Be("welcome");
        await link.ServerConnection.WaitAsync(ct);

        // The codec throws on this message: the connection tears down with that failure as the reason.
        await link.Client.SendTextAsync("boom", ct);
        var (_, reason) = await handler.Protocol!.Closed.Task.WaitAsync(ct);
        reason.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Be("codec parse failure");

        // The recording OnClosedAsync itself threw — unregistration must still complete.
        await link.ServerCompletion.WaitAsync(ct);
        registry.Connections.Should().BeEmpty();
    }

    [Fact]
    public async Task CriticalProtocolInitialization_FailureUnregistersBeforeFramedMessages() {
        var ct = TestContext.Current.CancellationToken;
        var services = new ServiceCollection();
        services.AddElarionConnections();
        await using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IClientConnectionRegistry>();
        var handler = new OpeningFailureTcpHandler();
        await using var link = InMemoryTcpLink.Start(handler, registry, o => o.Framer = new DelimitedTcpFramer((byte)'\n'));

        (await link.Client.ReceiveTextAsync(ct)).Should().Be("challenge");
        await link.Client.SendTextAsync("device:opening", ct);
        (await link.Client.ReceiveTextAsync(ct)).Should().Be("welcome");
        await link.ServerCompletion.WaitAsync(ct);

        handler.Protocol!.Opened.Should().Be(1);
        handler.Protocol.Messages.Should().Be(0);
        registry.Connections.Should().BeEmpty();
    }

    [Fact]
    public async Task Listener_MaxConcurrentConnections_ShedsExcessAndRecoversWhenASlotFrees() {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartListenerHostAsync<ChallengeTcpHandler>(
            ct, configure: o => o.MaxConcurrentConnections = 1);
        using var first = await host.ConnectAsync(ct);
        var firstStream = first.GetStream();
        (await ReadLineAsync(firstStream, ct)).Should().Be("challenge");
        await WriteLineAsync(firstStream, "device:cap-1", ct);
        (await ReadLineAsync(firstStream, ct)).Should().Be("welcome");
        await host.Observer.Connected.Task.WaitAsync(ct);

        // At the cap: the next socket is accepted and immediately closed — EOF (or a reset) before any
        // challenge, and nothing registers.
        using (var shed = await host.ConnectAsync(ct)) {
            string? line = null;
            try {
                line = await ReadLineAsync(shed.GetStream(), ct);
            }
            catch (IOException) {
                // A reset instead of a graceful FIN also proves the shed.
            }

            line.Should().BeNull();
        }

        host.Registry.Connections.Should().ContainSingle();

        // Freeing the slot lets the endpoint serve again (the runner-exit decrement is asynchronous, so
        // poll until the fresh connect is served).
        first.Close();
        await host.Observer.Disconnected.Task.WaitAsync(ct);
        while (true) {
            using var retry = await host.ConnectAsync(ct);
            string? line = null;
            try {
                line = await ReadLineAsync(retry.GetStream(), ct);
            }
            catch (IOException) {
            }

            if (line == "challenge") {
                break;
            }

            await Task.Delay(10, ct);
        }
    }

    [Fact]
    public async Task DynamicEndpoints_SynchronousApplyFailure_LeavesNoZombieEntry() {
        var ct = TestContext.Current.CancellationToken;
        var services = new ServiceCollection();
        services.AddElarionConnections();
        services.AddElarionTcpConnectionEndpoints();
        // ChallengeTcpHandler is deliberately NOT registered: the loop factory throws synchronously.
        await using var provider = services.BuildServiceProvider();
        var endpoints = provider.GetRequiredService<TcpConnectionEndpoints>();

        try {
            var apply = async () => await endpoints.ApplyListenerAsync<ChallengeTcpHandler>("bind-broken", o => {
                o.ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0);
                o.Framer = new DelimitedTcpFramer(end: (byte)'\n');
            }, ct);
            await apply.Should().ThrowAsync<InvalidOperationException>();

            // The failed apply left nothing behind — no entry stuck in Starting.
            endpoints.GetStatus("bind-broken").Should().BeNull();
            endpoints.Statuses.Should().BeEmpty();
            endpoints.EndpointNames.Should().BeEmpty();
        }
        finally {
            await ((IHostedService)endpoints).StopAsync(CancellationToken.None);
        }
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
            o.Framer = new DelimitedTcpFramer(end: (byte)'\n');
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
                (await observer.Connected.Task.WaitAsync(ct)).Connection.PrincipalId.Should().Be("dev-9");

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
            (await observer.Connected.Task.WaitAsync(ct)).Connection.PrincipalId.Should().Be("dev-9");
        }
        finally {
            foreach (var service in hosted) {
                await service.StopAsync(CancellationToken.None);
            }
            await provider.DisposeAsync();
            device.Stop();
        }
    }

    [Fact]
    public async Task DynamicEndpoints_ReconfigureReconnectsUnderNewSettings_AndRemoveTearsDown() {
        var ct = TestContext.Current.CancellationToken;
        using var deviceA = new TcpListener(IPAddress.Loopback, 0);
        using var deviceB = new TcpListener(IPAddress.Loopback, 0);
        deviceA.Start();
        deviceB.Start();

        var observer = new AwaitableConnectionObserver();
        var inbound = Channel.CreateUnbounded<string>();
        var services = new ServiceCollection();
        services.AddElarionConnections();
        services.AddElarionTcpConnectionEndpoints();
        services.AddSingleton(observer);
        services.AddSingleton<IClientConnectionObserver>(sp => sp.GetRequiredService<AwaitableConnectionObserver>());
        services.AddSingleton(new DialerHandler(inbound.Writer));
        await using var provider = services.BuildServiceProvider();
        var endpoints = provider.GetRequiredService<TcpConnectionEndpoints>();

        try {
            // The binding starts as a dialer at device A.
            await endpoints.ApplyDialerAsync<DialerHandler>("bind-1", o => {
                o.Host = "127.0.0.1";
                o.Port = ((IPEndPoint)deviceA.LocalEndpoint).Port;
                o.Framer = new DelimitedTcpFramer(end: (byte)'\n');
                o.ReconnectMinDelay = TimeSpan.FromMilliseconds(50);
            }, ct);
            using (var session = await deviceA.AcceptTcpClientAsync(ct)) {
                var stream = session.GetStream();
                (await ReadLineAsync(stream, ct)).Should().Be("hello");
                await WriteLineAsync(stream, "ticket:dev-A", ct);
                (await observer.Connected.Task.WaitAsync(ct)).Connection.PrincipalId.Should().Be("dev-A");
                observer.Reset();

                // Re-applying the same binding with new settings reconnects it: the device-A link drops
                // while the session is still open, then the dialer establishes at device B.
                await endpoints.ApplyDialerAsync<DialerHandler>("bind-1", o => {
                    o.Host = "127.0.0.1";
                    o.Port = ((IPEndPoint)deviceB.LocalEndpoint).Port;
                    o.Framer = new DelimitedTcpFramer(end: (byte)'\n');
                    o.ReconnectMinDelay = TimeSpan.FromMilliseconds(50);
                }, ct);
                await observer.Disconnected.Task.WaitAsync(ct);
            }

            using var sessionB = await deviceB.AcceptTcpClientAsync(ct);
            var streamB = sessionB.GetStream();
            (await ReadLineAsync(streamB, ct)).Should().Be("hello");
            await WriteLineAsync(streamB, "ticket:dev-B", ct);
            (await observer.Connected.Task.WaitAsync(ct)).Connection.PrincipalId.Should().Be("dev-B");
            endpoints.EndpointNames.Should().BeEquivalentTo(["bind-1"]);

            observer.Reset();
            (await endpoints.RemoveAsync("bind-1", ct)).Should().BeTrue();
            await observer.Disconnected.Task.WaitAsync(ct);
            endpoints.EndpointNames.Should().BeEmpty();
            (await endpoints.RemoveAsync("bind-1", ct)).Should().BeFalse();
        }
        finally {
            await ((IHostedService)endpoints).StopAsync(CancellationToken.None);
            deviceA.Stop();
            deviceB.Stop();
        }
    }

    [Fact]
    public async Task DynamicEndpoints_AdvertiseBindingHealthWithReasons() {
        var ct = TestContext.Current.CancellationToken;
        // Occupy a port so the second binding's listen fails.
        using var occupant = new TcpListener(IPAddress.Loopback, 0);
        occupant.Start();
        var occupiedPort = ((IPEndPoint)occupant.LocalEndpoint).Port;

        var services = new ServiceCollection();
        services.AddElarionConnections();
        services.AddElarionTcpConnectionEndpoints();
        services.AddSingleton<ChallengeTcpHandler>();
        services.AddSingleton(new DialerHandler(Channel.CreateUnbounded<string>().Writer));
        await using var provider = services.BuildServiceProvider();
        var endpoints = provider.GetRequiredService<TcpConnectionEndpoints>();

        var transitions = Channel.CreateUnbounded<TcpEndpointStatus>();
        endpoints.StatusChanged += status => transitions.Writer.TryWrite(status);

        try {
            // A healthy listener advertises Listening.
            await endpoints.ApplyListenerAsync<ChallengeTcpHandler>("bind-ok", o => {
                o.ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0);
                o.Framer = new DelimitedTcpFramer(end: (byte)'\n');
            }, ct);
            var listening = await transitions.Reader.ReadAsync(ct);
            listening.Should().BeEquivalentTo(
                new { Name = "bind-ok", Mode = TcpEndpointMode.Listener, State = TcpEndpointState.Listening });
            endpoints.GetStatus("bind-ok")!.State.Should().Be(TcpEndpointState.Listening);

            // A binding whose port is taken advertises Faulted with the reason — visible, not just logged.
            await endpoints.ApplyListenerAsync<ChallengeTcpHandler>("bind-taken", o => {
                o.ListenEndPoint = new IPEndPoint(IPAddress.Loopback, occupiedPort);
                o.Framer = new DelimitedTcpFramer(end: (byte)'\n');
            }, ct);
            var faulted = await transitions.Reader.ReadAsync(ct);
            faulted.Name.Should().Be("bind-taken");
            faulted.State.Should().Be(TcpEndpointState.Faulted);
            faulted.Error.Should().NotBeNullOrEmpty();
            endpoints.Statuses.Should().HaveCount(2);

            // A dialer whose device is unreachable advertises Dialing with the last attempt's failure.
            occupant.Stop();
            await endpoints.ApplyDialerAsync<DialerHandler>("bind-unreachable", o => {
                o.Host = "127.0.0.1";
                o.Port = occupiedPort;                       // nothing listens here anymore
                o.Framer = new DelimitedTcpFramer(end: (byte)'\n');
                o.ReconnectMinDelay = TimeSpan.FromMilliseconds(20);
            }, ct);
            var dialing = await transitions.Reader.ReadAsync(ct);
            dialing.Name.Should().Be("bind-unreachable");
            dialing.State.Should().Be(TcpEndpointState.Dialing);
            dialing.Error.Should().NotBeNullOrEmpty();
        }
        finally {
            await ((IHostedService)endpoints).StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task DynamicEndpoints_FlipDirectionFromListenerToDialer() {
        var ct = TestContext.Current.CancellationToken;
        using var device = new TcpListener(IPAddress.Loopback, 0);
        device.Start();

        var observer = new AwaitableConnectionObserver();
        var boundEndPoint = new TaskCompletionSource<IPEndPoint>(TaskCreationOptions.RunContinuationsAsynchronously);
        var services = new ServiceCollection();
        services.AddElarionConnections();
        services.AddElarionTcpConnectionEndpoints();
        services.AddSingleton(observer);
        services.AddSingleton<IClientConnectionObserver>(sp => sp.GetRequiredService<AwaitableConnectionObserver>());
        services.AddSingleton<ChallengeTcpHandler>();
        services.AddSingleton(new DialerHandler(Channel.CreateUnbounded<string>().Writer));
        await using var provider = services.BuildServiceProvider();
        var endpoints = provider.GetRequiredService<TcpConnectionEndpoints>();

        try {
            // The binding starts as a server-based endpoint: a device dials in.
            await endpoints.ApplyListenerAsync<ChallengeTcpHandler>("bind-flip", o => {
                o.ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0);
                o.Framer = new DelimitedTcpFramer(end: (byte)'\n');
                o.OnListening = boundEndPoint.SetResult;
            }, ct);
            var listenPort = await boundEndPoint.Task.WaitAsync(ct);
            using (var inDialing = new TcpClient()) {
                await inDialing.ConnectAsync(listenPort, ct);
                var stream = inDialing.GetStream();
                (await ReadLineAsync(stream, ct)).Should().Be("challenge");
                await WriteLineAsync(stream, "device:flip-1", ct);
                (await ReadLineAsync(stream, ct)).Should().Be("welcome");
                await observer.Connected.Task.WaitAsync(ct);
                observer.Reset();

                // Same binding, flipped to client-based: the listener (and its connection) goes down and
                // the endpoint now dials the device instead.
                await endpoints.ApplyDialerAsync<DialerHandler>("bind-flip", o => {
                    o.Host = "127.0.0.1";
                    o.Port = ((IPEndPoint)device.LocalEndpoint).Port;
                    o.Framer = new DelimitedTcpFramer(end: (byte)'\n');
                    o.ReconnectMinDelay = TimeSpan.FromMilliseconds(50);
                }, ct);
                await observer.Disconnected.Task.WaitAsync(ct);
            }

            using var session = await device.AcceptTcpClientAsync(ct);
            var deviceStream = session.GetStream();
            (await ReadLineAsync(deviceStream, ct)).Should().Be("hello");
            await WriteLineAsync(deviceStream, "ticket:flip-1", ct);
            (await observer.Connected.Task.WaitAsync(ct)).Connection.PrincipalId.Should().Be("flip-1");

            // The old listening socket is gone: a fresh connect to it must fail.
            using var probe = new TcpClient();
            var reconnect = async () => await probe.ConnectAsync(listenPort, ct).AsTask().WaitAsync(TimeSpan.FromSeconds(2), ct);
            await reconnect.Should().ThrowAsync<Exception>();
        }
        finally {
            await ((IHostedService)endpoints).StopAsync(CancellationToken.None);
            device.Stop();
        }
    }

    private static Task<TcpTestHost> StartListenerHostAsync(CancellationToken ct, TimeSpan? idleTimeout = null) =>
        StartListenerHostAsync<ChallengeTcpHandler>(ct, idleTimeout);

    private static async Task<TcpTestHost> StartListenerHostAsync<THandler>(
        CancellationToken ct, TimeSpan? idleTimeout = null, Action<ElarionTcpListenerOptions>? configure = null)
        where THandler : TcpConnectionHandler, new() {
        var boundEndPoint = new TaskCompletionSource<IPEndPoint>(TaskCreationOptions.RunContinuationsAsynchronously);
        var services = new ServiceCollection();
        services.AddElarionConnections();
        services.AddSingleton<THandler>();
        services.AddSingleton<AwaitableConnectionObserver>();
        services.AddSingleton<IClientConnectionObserver>(sp => sp.GetRequiredService<AwaitableConnectionObserver>());
        services.AddElarionTcpConnectionListener<THandler>(o => {
            o.ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0);
            o.Framer = new DelimitedTcpFramer(end: (byte)'\n');
            o.IdleTimeout = idleTimeout;
            o.OnListening = boundEndPoint.SetResult;
            configure?.Invoke(o);
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
        // Bytes are bytes on TCP: text protocols decode in the codec — the one-liner the framer no longer does.
        public ValueTask OnBinaryAsync(ReadOnlyMemory<byte> message, CancellationToken ct) =>
            connection.SendTextAsync("echo:" + Encoding.UTF8.GetString(message.Span), ct);

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
                    Framer = new DelimitedTcpFramer(end: (byte)'|'),
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
        public ValueTask OnBinaryAsync(ReadOnlyMemory<byte> message, CancellationToken ct) =>
            inbound.WriteAsync(Encoding.UTF8.GetString(message.Span), ct);
    }

    /// <summary>Records the codec teardown signal; optionally fails on a message (to provoke a codec-fault
    /// close) and from <c>OnClosedAsync</c> itself (to prove teardown is failure-isolated).</summary>
    private sealed class ClosureRecordingHandler(bool throwOnMessage = false, bool throwOnClosed = false)
        : TcpConnectionHandler {
        public ClosureRecordingProtocol? Protocol { get; private set; }

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
            Protocol = new ClosureRecordingProtocol(throwOnMessage, throwOnClosed);
    }

    private sealed class ClosureRecordingProtocol(bool throwOnMessage, bool throwOnClosed)
        : IClientConnectionProtocol {
        public TaskCompletionSource<(ClientConnection Connection, Exception? Reason)> Closed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask OnBinaryAsync(ReadOnlyMemory<byte> message, CancellationToken ct) =>
            throwOnMessage
                ? throw new InvalidOperationException("codec parse failure")
                : ValueTask.CompletedTask;

        public ValueTask OnClosedAsync(ClientConnection connection, Exception? reason, CancellationToken ct) {
            Closed.TrySetResult((connection, reason));
            if (throwOnClosed) {
                throw new InvalidOperationException("teardown failure");
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class OpeningFailureTcpHandler : TcpConnectionHandler {
        public OpeningFailureTcpProtocol? Protocol { get; private set; }

        public override async ValueTask<ClientConnectionTicket?> AuthenticateAsync(TcpHandshakeContext handshake, CancellationToken ct) {
            await handshake.SendTextAsync("challenge", ct);
            var reply = await handshake.ReceiveTextAsync(ct);
            if (reply is null || !reply.StartsWith("device:", StringComparison.Ordinal)) return null;
            await handshake.SendTextAsync("welcome", ct);
            return new ClientConnectionTicket { Principal = new ClaimsPrincipal(new ClaimsIdentity("device")), PrincipalId = reply[7..] };
        }

        public override IClientConnectionProtocol CreateProtocol(TcpClientConnection connection) => Protocol = new OpeningFailureTcpProtocol();
    }

    private sealed class OpeningFailureTcpProtocol : IClientConnectionProtocol {
        public int Opened { get; private set; }
        public int Messages { get; private set; }

        public ValueTask OnOpenedAsync(ClientConnection connection, CancellationToken ct) {
            Opened++;
            throw new InvalidOperationException("required setup failed");
        }

        public ValueTask OnBinaryAsync(ReadOnlyMemory<byte> message, CancellationToken ct) {
            Messages++;
            return ValueTask.CompletedTask;
        }
    }

}
