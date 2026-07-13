using System.Diagnostics.CodeAnalysis;
using System.Net.WebSockets;
using System.Text;
using Elarion.Abstractions.Connections;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Elarion.Connections.AspNetCore;

/// <summary>
/// Maps a WebSocket connection endpoint: accept → the handler's handshake (reject closes with
/// <c>PolicyViolation</c>, nothing registered) → mint the <see cref="ClientConnection"/> → register with the
/// kernel registry (observers fire — the client-events bridge and any presence projection hook in here) →
/// one receive loop dispatching complete messages to the connection's codec → unregister on any exit. The
/// app supplies exactly two things via its <see cref="WebSocketConnectionHandler"/>: the authenticator and
/// the codec.
/// </summary>
/// <remarks>
/// Requires <c>app.UseWebSockets()</c> and <c>AddElarionConnections()</c>; register the concrete
/// <typeparamref name="THandler"/> in DI. A non-upgrade request gets a bodyless 400. Per-endpoint auth
/// conventions (e.g. <c>RequireAuthorization()</c> for cookie-carried browser links) are the host's to
/// apply on the returned builder — device handshakes typically stay anonymous at the HTTP level and
/// authenticate in-socket.
/// </remarks>
public static class ConnectionSocketEndpointRouteBuilderExtensions {
    /// <summary>Maps the endpoint at <paramref name="pattern"/> using <typeparamref name="THandler"/>.</summary>
    /// <typeparam name="THandler">The app's connection handler, resolved from the request's services.</typeparam>
    /// <param name="endpoints">The route builder.</param>
    /// <param name="pattern">The route pattern (e.g. <c>"/gateway/ws"</c>).</param>
    /// <param name="configure">Optional per-endpoint tuning.</param>
    public static IEndpointConventionBuilder MapElarionConnectionSocket<THandler>(
        this IEndpointRouteBuilder endpoints,
        [StringSyntax("Route")] string pattern,
        Action<ElarionConnectionSocketOptions>? configure = null)
        where THandler : WebSocketConnectionHandler {
        ArgumentNullException.ThrowIfNull(endpoints);
        var options = new ElarionConnectionSocketOptions();
        configure?.Invoke(options);
        return endpoints.MapGet(pattern,
            (HttpContext context, CancellationToken ct) => HandleAsync<THandler>(context, options, ct));
    }

    private static async Task<IResult> HandleAsync<THandler>(
        HttpContext context, ElarionConnectionSocketOptions options, CancellationToken ct)
        where THandler : WebSocketConnectionHandler {
        if (!context.WebSockets.IsWebSocketRequest) {
            return Results.BadRequest();
        }

        var services = context.RequestServices;
        var registry = services.GetRequiredService<IClientConnectionRegistry>();
        var handler = services.GetRequiredService<THandler>();
        var logger = services.GetService<ILoggerFactory>()?.CreateLogger(typeof(ConnectionSocketEndpointRouteBuilderExtensions).Namespace + ".ConnectionSocket")
            ?? NullLogger.Instance;

        using var socket = await context.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext {
            KeepAliveInterval = options.KeepAliveInterval,
        });
        var reader = new WebSocketMessageReader(socket, options.MaxMessageBytes);

        ClientConnectionTicket? ticket;
        try {
            ticket = await handler.AuthenticateAsync(new WebSocketHandshakeContext(context, socket, reader), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            return Results.Empty;
        }
        catch (WebSocketException) {
            // The client vanished mid-handshake — nothing was registered, nothing to clean up.
            return Results.Empty;
        }

        if (ticket is null) {
            await CloseSafelyAsync(socket, WebSocketCloseStatus.PolicyViolation, "unauthorized", ct);
            return Results.Empty;
        }

        var identity = new ClientConnection {
            ConnectionId = Guid.CreateVersion7().ToString("N"),
            Transport = "websocket",
            Principal = ticket.Principal,
            PrincipalId = ticket.PrincipalId,
            Metadata = ticket.Metadata,
            ConnectedAt = (services.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow(),
        };
        var connection = new WebSocketClientConnection(identity, socket);
        connection.AttachProtocol(handler.CreateProtocol(connection));

        await registry.RegisterAsync(connection, ct);
        try {
            await ReceiveLoopAsync(connection, reader, options.IdleTimeout, ct);
            await CloseSafelyAsync(socket, WebSocketCloseStatus.NormalClosure, "closing", ct);
        }
        catch (WebSocketMessageTooLargeException) {
            await CloseSafelyAsync(socket, WebSocketCloseStatus.MessageTooBig, "message too large", ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            // Host shutdown or request abort — the normal end of a long-lived socket.
        }
        catch (WebSocketException) {
            // Abrupt client death — also a normal end.
        }
        catch (Exception failure) {
            logger.LogWarning(failure,
                "Connection {ConnectionId} codec failed; closing the connection.", identity.ConnectionId);
            await CloseSafelyAsync(socket, WebSocketCloseStatus.InternalServerError, "protocol failure", ct);
        }
        finally {
            // Unregister must run even when the request token is already cancelled — observers tear down
            // subscriptions and presence.
            await registry.UnregisterAsync(identity.ConnectionId, CancellationToken.None);
        }

        return Results.Empty;
    }

    private static async Task ReceiveLoopAsync(
        WebSocketClientConnection connection, WebSocketMessageReader reader, TimeSpan? idleTimeout,
        CancellationToken ct) {
        // The pending read is threaded across idle ticks — an idle callback must not abandon it (a second
        // concurrent receive on one socket is invalid), mirroring the SSE keep-alive pattern.
        Task<WebSocketInboundMessage?>? pendingRead = null;
        while (true) {
            pendingRead ??= reader.ReadAsync(ct).AsTask();
            if (idleTimeout is { } window) {
                using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var winner = await Task.WhenAny(pendingRead, Task.Delay(window, delayCts.Token));
                if (winner != pendingRead) {
                    await connection.Protocol.OnIdleAsync(ct);
                    continue;
                }
                delayCts.Cancel();
            }

            var message = await pendingRead;
            pendingRead = null;
            if (message is null) {
                return;
            }

            if (message.Value.Type == WebSocketMessageType.Text) {
                await connection.Protocol.OnTextAsync(Encoding.UTF8.GetString(message.Value.Payload), ct);
            }
            else {
                await connection.Protocol.OnBinaryAsync(message.Value.Payload, ct);
            }
        }
    }

    private static async Task CloseSafelyAsync(
        WebSocket socket, WebSocketCloseStatus status, string description, CancellationToken ct) {
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived)) {
            return;
        }

        try {
            await socket.CloseOutputAsync(status, description, ct);
        }
        catch (Exception) {
            // Closing a dying socket is best-effort; the server-side teardown happens regardless.
        }
    }
}
