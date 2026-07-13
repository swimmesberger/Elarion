using System.Net;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using AwesomeAssertions;
using Elarion.Abstractions.Connections;
using Elarion.Connections;
using Elarion.Connections.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Elarion.Tests.Connections;

/// <summary>
/// End-to-end tests for the WebSocket connection endpoint over a real Kestrel host: the in-socket
/// challenge/response handshake (reject closes with <c>PolicyViolation</c>, nothing registered), registry
/// lifecycle around the socket's life, codec round-trips both ways (device-style echo in, raw server push
/// out), and the oversized-message close.
/// </summary>
public sealed class ConnectionSocketEndpointTests {
    [Fact]
    public async Task NonWebSocketRequest_Returns400() {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(ct);
        using var client = new HttpClient();

        var response = await client.GetAsync(host.HttpBase + "/ws", ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task RejectedHandshake_ClosesWithPolicyViolation_AndRegistersNothing() {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(ct);
        using var socket = await host.ConnectAsync(ct);

        (await ReceiveTextAsync(socket, ct)).Should().Be("challenge");
        await SendTextAsync(socket, "intruder", ct);

        (await ReceiveTextAsync(socket, ct)).Should().BeNull();
        socket.CloseStatus.Should().Be(WebSocketCloseStatus.PolicyViolation);
        host.Registry.Connections.Should().BeEmpty();
    }

    [Fact]
    public async Task AuthenticatedConnection_RegistersEchoesAndUnregistersOnClose() {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(ct);
        using var socket = await host.ConnectAsync(ct);

        await CompleteHandshakeAsync(socket, "dev-1", ct);
        var connected = await host.Observer.Connected.Task.WaitAsync(ct);
        connected.PrincipalId.Should().Be("dev-1");
        connected.Transport.Should().Be("websocket");
        connected.Metadata.Should().ContainKey("channel").WhoseValue.Should().Be("main");
        host.Registry.GetForPrincipal("dev-1").Should().ContainSingle();

        await SendTextAsync(socket, "ping", ct);
        (await ReceiveTextAsync(socket, ct)).Should().Be("echo:ping");

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", ct);
        var disconnected = await host.Observer.Disconnected.Task.WaitAsync(ct);
        disconnected.ConnectionId.Should().Be(connected.ConnectionId);
        host.Registry.Connections.Should().BeEmpty();
    }

    [Fact]
    public async Task ServerSide_CanPushRawFrames_AndDefaultInvokeFailsLoud() {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(ct);
        using var socket = await host.ConnectAsync(ct);
        await CompleteHandshakeAsync(socket, "dev-2", ct);
        await host.Observer.Connected.Task.WaitAsync(ct);

        var sink = host.Registry.GetForPrincipal("dev-2").Single();
        await ((WebSocketClientConnection)sink).SendTextAsync("server-says-hi", ct);
        (await ReceiveTextAsync(socket, ct)).Should().Be("server-says-hi");

        // The echo codec implements no request/reply — the neutral invoke leg fails loud, never silently.
        var invoke = async () => await sink.InvokeAsync<string, string>("anything", "payload", ct: ct);
        await invoke.Should().ThrowAsync<NotSupportedException>();
    }

    [Fact]
    public async Task OversizedMessage_ClosesWithMessageTooBig() {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(ct, o => o.MaxMessageBytes = 64);
        using var socket = await host.ConnectAsync(ct);
        await CompleteHandshakeAsync(socket, "dev-3", ct);
        await host.Observer.Connected.Task.WaitAsync(ct);

        await SendTextAsync(socket, new string('x', 1024), ct);

        (await ReceiveTextAsync(socket, ct)).Should().BeNull();
        socket.CloseStatus.Should().Be(WebSocketCloseStatus.MessageTooBig);
        await host.Observer.Disconnected.Task.WaitAsync(ct);
        host.Registry.Connections.Should().BeEmpty();
    }

    private static async Task CompleteHandshakeAsync(ClientWebSocket socket, string deviceId, CancellationToken ct) {
        (await ReceiveTextAsync(socket, ct)).Should().Be("challenge");
        await SendTextAsync(socket, "device:" + deviceId, ct);
        (await ReceiveTextAsync(socket, ct)).Should().Be("welcome");
    }

    private static async Task SendTextAsync(ClientWebSocket socket, string message, CancellationToken ct) =>
        await socket.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, endOfMessage: true, ct);

    private static async Task<string?> ReceiveTextAsync(ClientWebSocket socket, CancellationToken ct) {
        var buffer = new byte[8 * 1024];
        using var assembled = new MemoryStream();
        while (true) {
            var result = await socket.ReceiveAsync(buffer.AsMemory(), ct);
            if (result.MessageType == WebSocketMessageType.Close) {
                return null;
            }

            assembled.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) {
                return Encoding.UTF8.GetString(assembled.ToArray());
            }
        }
    }

    private static async Task<SocketTestHost> StartAsync(
        CancellationToken ct, Action<ElarionConnectionSocketOptions>? configure = null) {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddElarionConnections();
        builder.Services.AddSingleton<ChallengeHandler>();
        builder.Services.AddSingleton<AwaitableObserver>();
        builder.Services.AddSingleton<IClientConnectionObserver>(sp => sp.GetRequiredService<AwaitableObserver>());

        var app = builder.Build();
        app.UseWebSockets();
        app.MapElarionConnectionSocket<ChallengeHandler>("/ws", configure);
        await app.StartAsync(ct);

        var httpBase = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new SocketTestHost(app, httpBase);
    }

    private sealed class SocketTestHost(WebApplication app, string httpBase) : IAsyncDisposable {
        public string HttpBase { get; } = httpBase;

        public IClientConnectionRegistry Registry => app.Services.GetRequiredService<IClientConnectionRegistry>();

        public AwaitableObserver Observer => app.Services.GetRequiredService<AwaitableObserver>();

        public async Task<ClientWebSocket> ConnectAsync(CancellationToken ct) {
            var socket = new ClientWebSocket();
            await socket.ConnectAsync(new Uri(HttpBase.Replace("http://", "ws://") + "/ws"), ct);
            return socket;
        }

        public async ValueTask DisposeAsync() => await app.DisposeAsync();
    }

    /// <summary>The device-gateway shape: an in-socket challenge/response handshake and an echo codec.</summary>
    private sealed class ChallengeHandler : WebSocketConnectionHandler {
        public override async ValueTask<ClientConnectionTicket?> AuthenticateAsync(
            WebSocketHandshakeContext handshake, CancellationToken ct) {
            await handshake.SendTextAsync("challenge", ct);
            var reply = await handshake.ReceiveTextAsync(ct);
            if (reply is null || !reply.StartsWith("device:", StringComparison.Ordinal)) {
                return null;
            }

            await handshake.SendTextAsync("welcome", ct);
            return new ClientConnectionTicket {
                Principal = new ClaimsPrincipal(new ClaimsIdentity(authenticationType: "device")),
                PrincipalId = reply["device:".Length..],
                Metadata = new Dictionary<string, string> { ["channel"] = "main" },
            };
        }

        public override IClientConnectionProtocol CreateProtocol(WebSocketClientConnection connection) =>
            new EchoProtocol(connection);
    }

    private sealed class EchoProtocol(WebSocketClientConnection connection) : IClientConnectionProtocol {
        public ValueTask OnTextAsync(string message, CancellationToken ct) =>
            connection.SendTextAsync("echo:" + message, ct);
    }

    private sealed class AwaitableObserver : IClientConnectionObserver {
        public TaskCompletionSource<ClientConnection> Connected { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<ClientConnection> Disconnected { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask OnConnectedAsync(IClientConnectionSink connection, CancellationToken ct = default) {
            Connected.TrySetResult(connection.Connection);
            return ValueTask.CompletedTask;
        }

        public ValueTask OnDisconnectedAsync(ClientConnection connection, CancellationToken ct = default) {
            Disconnected.TrySetResult(connection);
            return ValueTask.CompletedTask;
        }
    }
}
