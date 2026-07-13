using System.Net;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using AwesomeAssertions;
using Elarion.Abstractions.Connections;
using Elarion.Connections;
using Elarion.Connections.AspNetCore;
using Elarion.Connections.Simulation;
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
        var connected = (await host.Observer.Connected.Task.WaitAsync(ct)).Connection;
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

    [Fact]
    public async Task SilentHandshake_IsClosedAtTheDeadline_AndRegistersNothing() {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(ct, o => o.HandshakeTimeout = TimeSpan.FromMilliseconds(100));
        using var socket = await host.ConnectAsync(ct);

        (await ReceiveTextAsync(socket, ct)).Should().Be("challenge");
        // Send nothing: an accepted client that never authenticates must not hold a slot forever. The
        // deadline cancellation aborts the pending receive, so the client observes either a close frame
        // or an abrupt reset — the slot is gone either way.
        try {
            (await ReceiveTextAsync(socket, ct)).Should().BeNull();
        }
        catch (WebSocketException) {
        }

        host.Registry.Connections.Should().BeEmpty();
    }

    [Fact]
    public async Task IdleHook_SendsProtocolKeepaliveOnSilentSocket() {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(ct, o => o.IdleTimeout = TimeSpan.FromMilliseconds(50));
        using var socket = await host.ConnectAsync(ct);
        await CompleteHandshakeAsync(socket, "dev-idle", ct);
        await host.Observer.Connected.Task.WaitAsync(ct);

        // Send nothing: the idle window elapses and the codec's OnIdleAsync pushes its keepalive.
        (await ReceiveTextAsync(socket, ct)).Should().Be("idle-ping");
    }

    [Fact]
    public async Task Codec_OnClosedAsync_RunsWithNullReasonOnCleanSocketClose() {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync<ClosureRecordingHandler>(ct);
        using var socket = await host.ConnectAsync(ct);
        await CompleteHandshakeAsync(socket, "dev-closed", ct);
        await host.Observer.Connected.Task.WaitAsync(ct);

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", ct);
        await host.Observer.Disconnected.Task.WaitAsync(ct);

        var handler = host.Services.GetRequiredService<ClosureRecordingHandler>();
        var (connection, reason) = await handler.Protocol!.Closed.Task.WaitAsync(ct);
        connection.PrincipalId.Should().Be("dev-closed");
        reason.Should().BeNull();
        host.Registry.Connections.Should().BeEmpty();
    }

    [Fact]
    public async Task PerConnectionSettings_ServeDifferentTiersOnOneRoute() {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync<TieredHandler>(ct);

        // Default tier: endpoint options apply, per-connection transport tag from the query.
        using (var standard = await host.ConnectAsync(ct, "?tier=standard")) {
            await CompleteHandshakeAsync(standard, "ws-std", ct);
            (await host.Observer.Connected.Task.WaitAsync(ct)).Connection.Transport.Should().Be("websocket-standard");
            await SendTextAsync(standard, new string('x', 256), ct);
            (await ReceiveTextAsync(standard, ct)).Should().StartWith("echo:");
            await standard.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", ct);
            await host.Observer.Disconnected.Task.WaitAsync(ct);
            host.Observer.Reset();
        }

        // Constrained tier, same route: a per-connection size cap makes the same message oversized.
        using var constrained = await host.ConnectAsync(ct, "?tier=constrained");
        await CompleteHandshakeAsync(constrained, "ws-tiny", ct);
        (await host.Observer.Connected.Task.WaitAsync(ct)).Connection.Transport.Should().Be("websocket-constrained");

        await SendTextAsync(constrained, new string('x', 256), ct);
        (await ReceiveTextAsync(constrained, ct)).Should().BeNull();
        constrained.CloseStatus.Should().Be(WebSocketCloseStatus.MessageTooBig);
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

    private static Task<SocketTestHost> StartAsync(
        CancellationToken ct, Action<ElarionConnectionSocketOptions>? configure = null) =>
        StartAsync<ChallengeHandler>(ct, configure);

    private static async Task<SocketTestHost> StartAsync<THandler>(
        CancellationToken ct, Action<ElarionConnectionSocketOptions>? configure = null)
        where THandler : WebSocketConnectionHandler, new() {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddElarionConnections();
        builder.Services.AddSingleton<THandler>();
        builder.Services.AddSingleton<AwaitableConnectionObserver>();
        builder.Services.AddSingleton<IClientConnectionObserver>(sp => sp.GetRequiredService<AwaitableConnectionObserver>());

        var app = builder.Build();
        app.UseWebSockets();
        app.MapElarionConnectionSocket<THandler>("/ws", configure);
        await app.StartAsync(ct);

        var httpBase = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new SocketTestHost(app, httpBase);
    }

    private sealed class SocketTestHost(WebApplication app, string httpBase) : IAsyncDisposable {
        public string HttpBase { get; } = httpBase;

        public IServiceProvider Services => app.Services;

        public IClientConnectionRegistry Registry => app.Services.GetRequiredService<IClientConnectionRegistry>();

        public AwaitableConnectionObserver Observer => app.Services.GetRequiredService<AwaitableConnectionObserver>();

        public async Task<ClientWebSocket> ConnectAsync(CancellationToken ct, string query = "") {
            var socket = new ClientWebSocket();
            await socket.ConnectAsync(new Uri(HttpBase.Replace("http://", "ws://") + "/ws" + query), ct);
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

        public ValueTask OnIdleAsync(CancellationToken ct) =>
            connection.SendTextAsync("idle-ping", ct);
    }

    /// <summary>Records the codec teardown signal — the connection-ended notification a codec mounts its
    /// pending-invoke <c>FailAll</c> on.</summary>
    private sealed class ClosureRecordingHandler : WebSocketConnectionHandler {
        public ClosureRecordingProtocol? Protocol { get; private set; }

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
            };
        }

        public override IClientConnectionProtocol CreateProtocol(WebSocketClientConnection connection) =>
            Protocol = new ClosureRecordingProtocol();
    }

    private sealed class ClosureRecordingProtocol : IClientConnectionProtocol {
        public TaskCompletionSource<(ClientConnection Connection, Exception? Reason)> Closed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask OnClosedAsync(ClientConnection connection, Exception? reason, CancellationToken ct) {
            Closed.TrySetResult((connection, reason));
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>One route, two tiers: per-connection settings picked from the upgrade request's query —
    /// the constrained tier gets a tiny size cap, and both get tier-tagged transports.</summary>
    private sealed class TieredHandler : WebSocketConnectionHandler {
        public override ValueTask<WebSocketConnectionSettings?> ConfigureConnectionAsync(
            Microsoft.AspNetCore.Http.HttpContext context, CancellationToken ct) {
            var tier = context.Request.Query["tier"].ToString();
            return ValueTask.FromResult<WebSocketConnectionSettings?>(new WebSocketConnectionSettings {
                Transport = "websocket-" + tier,
                MaxMessageBytes = tier == "constrained" ? 64 : null,
            });
        }

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
            };
        }

        public override IClientConnectionProtocol CreateProtocol(WebSocketClientConnection connection) =>
            new EchoProtocol(connection);
    }

}
