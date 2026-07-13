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

        // Per-connection configuration (the binding-config lookup point): resolved from the upgrade request
        // before the socket is accepted; nulls inherit the endpoint options.
        var overrides = await handler.ConfigureConnectionAsync(context, ct);
        var maxMessageBytes = overrides?.MaxMessageBytes ?? options.MaxMessageBytes;
        var idleTimeout = overrides?.IdleTimeout ?? options.IdleTimeout;
        var transport = overrides?.Transport ?? "websocket";
        var receiveBufferBytes = overrides?.ReceiveBufferBytes ?? options.ReceiveBufferBytes;

        using var socket = await context.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext {
            KeepAliveInterval = overrides?.KeepAliveInterval ?? options.KeepAliveInterval,
        });
        var reader = new WebSocketMessageReader(socket, maxMessageBytes, receiveBufferBytes);

        ClientConnectionTicket? ticket;
        try {
            // The handshake is deadline-bounded: an accepted client that never authenticates must not hold
            // a slot forever.
            using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            handshakeCts.CancelAfter(overrides?.HandshakeTimeout ?? options.HandshakeTimeout);
            ticket = await handler.AuthenticateAsync(
                new WebSocketHandshakeContext(context, socket, reader), handshakeCts.Token);
        }
        catch (OperationCanceledException) {
            // Request abort or handshake deadline — either way nothing was registered. Cancelling a
            // pending WebSocket receive aborts the socket, so this close is best-effort: the peer may
            // observe a reset instead of a close frame; the slot is freed regardless.
            await CloseSafelyAsync(socket, WebSocketCloseStatus.PolicyViolation, "handshake timeout", CancellationToken.None);
            return Results.Empty;
        }
        catch (WebSocketMessageTooLargeException) {
            await CloseSafelyAsync(socket, WebSocketCloseStatus.MessageTooBig, "message too large", ct);
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
            Transport = transport,
            Principal = ticket.Principal,
            PrincipalId = ticket.PrincipalId,
            Metadata = ticket.Metadata,
            ConnectedAt = (services.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow(),
        };
        var connection = new WebSocketClientConnection(identity, socket);
        connection.AttachProtocol(handler.CreateProtocol(connection));

        Exception? closeReason = null;
        try {
            // Registration lives inside this try: RegisterAsync mutates the index before dispatching
            // observers, so an abort mid-registration must still reach the unregister in finally.
            await registry.RegisterAsync(connection, ct);
            await ReceiveLoopAsync(connection, reader, idleTimeout, ct);
            await CloseSafelyAsync(socket, WebSocketCloseStatus.NormalClosure, "closing", ct);
        }
        catch (WebSocketMessageTooLargeException failure) {
            closeReason = failure;
            await CloseSafelyAsync(socket, WebSocketCloseStatus.MessageTooBig, "message too large", ct);
        }
        catch (OperationCanceledException failure) when (ct.IsCancellationRequested) {
            // Host shutdown or request abort — the normal end of a long-lived socket.
            closeReason = failure;
        }
        catch (WebSocketException failure) {
            // Abrupt client death — also a normal end.
            closeReason = failure;
        }
        catch (Exception failure) {
            closeReason = failure;
            logger.LogWarning(failure,
                "Connection {ConnectionId} codec failed; closing the connection.", identity.ConnectionId);
            await CloseSafelyAsync(socket, WebSocketCloseStatus.InternalServerError, "protocol failure", ct);
        }
        finally {
            // The codec learns the connection ended before observers do — its pending invokes and
            // conversation waiters must fault, not hang. Then unregister must run even when the request
            // token is already cancelled — observers tear down subscriptions and presence.
            await NotifyClosedSafelyAsync(connection, closeReason, logger);
            await registry.UnregisterAsync(identity.ConnectionId, CancellationToken.None);
        }

        return Results.Empty;
    }

    // Failure-isolated: a throwing OnClosedAsync must never break teardown/unregistration.
    private static async ValueTask NotifyClosedSafelyAsync(
        WebSocketClientConnection connection, Exception? reason, ILogger logger) {
        try {
            await connection.Protocol.OnClosedAsync(connection.Connection, reason, CancellationToken.None);
        }
        catch (Exception failure) {
            logger.LogWarning(failure, "Connection {ConnectionId} codec OnClosedAsync failed.",
                connection.Connection.ConnectionId);
        }
    }

    private static async Task ReceiveLoopAsync(
        WebSocketClientConnection connection, WebSocketMessageReader reader, TimeSpan? idleTimeout,
        CancellationToken ct) {
        while (true) {
            var message = idleTimeout is { } window
                ? await ReadWithIdleAsync(connection, reader, window, ct)
                : await reader.ReadAsync(ct);
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

    // The idle race only engages when the read actually goes async — synchronous completions pay for no
    // timer or Task materialization. The pending read is never abandoned (a second concurrent receive on
    // one socket is invalid); idle ticks fire per elapsed window until data arrives.
    private static async ValueTask<WebSocketInboundMessage?> ReadWithIdleAsync(
        WebSocketClientConnection connection, WebSocketMessageReader reader, TimeSpan window,
        CancellationToken ct) {
        var read = reader.ReadAsync(ct);
        if (read.IsCompletedSuccessfully) {
            return read.Result;
        }

        var pending = read.AsTask();
        // If OnIdleAsync throws (documented dead-link detection) the connection tears down while this read
        // is still in flight on the doomed socket — observe its eventual fault so it never surfaces as an
        // unobserved task exception.
        _ = pending.ContinueWith(
            static faulted => _ = faulted.Exception,
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        while (true) {
            using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var winner = await Task.WhenAny(pending, Task.Delay(window, delayCts.Token));
            if (winner == pending) {
                delayCts.Cancel();
                return await pending;
            }

            // A cancelled delay can win the race during shutdown — that is teardown, not an idle window.
            ct.ThrowIfCancellationRequested();
            await connection.Protocol.OnIdleAsync(ct);
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
