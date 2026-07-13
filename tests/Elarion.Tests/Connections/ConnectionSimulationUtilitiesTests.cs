using System.Net;
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
/// Covers the shipped simulation utilities themselves: the synthetic sink's capture/invoke/close semantics
/// (including registry integration — the no-socket-assumption property), and the framed
/// <see cref="TcpSimulatorClient"/> driving a real listener end to end.
/// </summary>
public sealed class ConnectionSimulationUtilitiesTests {
    [Fact]
    public async Task SimulatedClientConnection_CapturesSendsAnswersInvokes_AndFaultsAfterClose() {
        var ct = TestContext.Current.CancellationToken;
        var connection = new SimulatedClientConnection(principalId: "dev-1") {
            InvokeResponder = (name, request) => ValueTask.FromResult<object>($"{name}:{request}:ack"),
        };

        await connection.SendAsync("state.changed", new { Value = 1 }, ct);
        (await connection.Sent.ReadAsync(ct)).Name.Should().Be("state.changed");

        (await connection.InvokeAsync<string, string>("start", "now", ct: ct)).Should().Be("start:now:ack");

        connection.Close();
        var send = async () => await connection.SendAsync("late", new object(), ct);
        await send.Should().ThrowAsync<ClientConnectionClosedException>();
    }

    [Fact]
    public async Task SimulatedClientConnection_RegistersWithTheRealRegistry() {
        var ct = TestContext.Current.CancellationToken;
        await using var provider = new ServiceCollection().AddElarionConnections().BuildServiceProvider();
        var registry = provider.GetRequiredService<IClientConnectionRegistry>();
        var connection = new SimulatedClientConnection(principalId: "dev-2");

        await registry.RegisterAsync(connection, ct);

        registry.GetForPrincipal("dev-2").Should().ContainSingle().Which.Should().BeSameAs(connection);
        await registry.UnregisterAsync(connection.Connection.ConnectionId, ct);
        registry.Connections.Should().BeEmpty();
    }

    [Fact]
    public async Task TcpSimulatorClient_CompletesAFramedHandshakeAndEcho() {
        var ct = TestContext.Current.CancellationToken;
        var framer = new DelimitedTcpFramer(end: (byte)'\n');
        var bound = new TaskCompletionSource<IPEndPoint>(TaskCreationOptions.RunContinuationsAsynchronously);
        var services = new ServiceCollection();
        services.AddElarionConnections();
        services.AddSingleton<EchoHandler>();
        services.AddElarionTcpConnectionListener<EchoHandler>(o => {
            o.ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0);
            o.Framer = framer;
            o.OnListening = bound.SetResult;
        });
        await using var provider = services.BuildServiceProvider();
        var hosted = provider.GetServices<IHostedService>().ToArray();
        foreach (var service in hosted) {
            await service.StartAsync(ct);
        }

        try {
            await using var client = await TcpSimulatorClient.ConnectAsync(await bound.Task.WaitAsync(ct), framer, ct);
            (await client.ReceiveTextAsync(ct)).Should().Be("challenge");
            await client.SendTextAsync("device:sim-1", ct);
            (await client.ReceiveTextAsync(ct)).Should().Be("welcome");

            await client.SendTextAsync("ping", ct);
            (await client.ReceiveTextAsync(ct)).Should().Be("echo:ping");
        }
        finally {
            foreach (var service in hosted) {
                await service.StopAsync(CancellationToken.None);
            }
        }
    }

    [Fact]
    public async Task InMemoryTcpLink_RunsTheFullLifecycleWithoutSockets() {
        var ct = TestContext.Current.CancellationToken;
        await using var provider = new ServiceCollection().AddElarionConnections().BuildServiceProvider();
        var registry = provider.GetRequiredService<IClientConnectionRegistry>();

        await using (var link = InMemoryTcpLink.Start(new EchoHandler(), registry,
                         o => o.Framer = new DelimitedTcpFramer(end: (byte)'\n'))) {
            (await link.Client.ReceiveTextAsync(ct)).Should().Be("challenge");
            await link.Client.SendTextAsync("device:mem-1", ct);
            (await link.Client.ReceiveTextAsync(ct)).Should().Be("welcome");

            var connected = await link.ServerConnection.WaitAsync(ct);
            connected.Connection.Transport.Should().Be("in-memory");
            registry.GetForPrincipal("mem-1").Should().ContainSingle();

            await link.Client.SendTextAsync("ping", ct);
            (await link.Client.ReceiveTextAsync(ct)).Should().Be("echo:ping");
        }

        // Disposing the link closes the client end; the server observed EOF, unregistered, and completed.
        registry.Connections.Should().BeEmpty();
    }

    [Fact]
    public async Task InMemoryDuplexStream_EmptyWriteIsNotEndOfStream() {
        var ct = TestContext.Current.CancellationToken;
        var (left, right) = InMemoryDuplexStream.CreatePair();
        await using var _ = left;
        await using var __ = right;

        await left.WriteAsync(ReadOnlyMemory<byte>.Empty, ct);
        await left.WriteAsync(new byte[] { 42 }, ct);

        var buffer = new byte[8];
        (await right.ReadAsync(buffer, ct)).Should().Be(1);
        buffer[0].Should().Be(42);
    }

    private sealed class EchoHandler : TcpConnectionHandler {
        public override async ValueTask<ClientConnectionTicket?> AuthenticateAsync(
            TcpHandshakeContext handshake, CancellationToken ct) {
            await handshake.SendTextAsync("challenge", ct);
            var reply = await handshake.ReceiveTextAsync(ct);
            if (reply is null || !reply.StartsWith("device:", StringComparison.Ordinal)) {
                return null;
            }

            await handshake.SendTextAsync("welcome", ct);
            return new ClientConnectionTicket {
                Principal = new System.Security.Claims.ClaimsPrincipal(
                    new System.Security.Claims.ClaimsIdentity(authenticationType: "device")),
                PrincipalId = reply["device:".Length..],
            };
        }

        public override IClientConnectionProtocol CreateProtocol(TcpClientConnection connection) =>
            new EchoProtocol(connection);
    }

    private sealed class EchoProtocol(TcpClientConnection connection) : IClientConnectionProtocol {
        public ValueTask OnBinaryAsync(ReadOnlyMemory<byte> message, CancellationToken ct) =>
            connection.SendTextAsync("echo:" + System.Text.Encoding.UTF8.GetString(message.Span), ct);
    }
}
