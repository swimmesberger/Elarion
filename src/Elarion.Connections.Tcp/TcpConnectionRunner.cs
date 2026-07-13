using System.Net.Sockets;
using System.Text;
using Elarion.Abstractions.Connections;
using Microsoft.Extensions.Logging;

namespace Elarion.Connections.Tcp;

/// <summary>
/// Drives one accepted or dialed socket through the shared lifecycle — handshake (timeout-bounded, reject
/// closes with nothing registered) → register → receive loop (framer-fed codec dispatch, idle hook) →
/// unregister — identically for the listener and the dialer. Never throws: every exit path is classified
/// and the socket is disposed.
/// </summary>
internal static class TcpConnectionRunner {
    public static async Task RunAsync(
        TcpClient client,
        ElarionTcpConnectionOptions options,
        TcpConnectionHandler handler,
        IClientConnectionRegistry registry,
        TimeProvider timeProvider,
        ILogger logger,
        CancellationToken ct) {
        using var owned = client;
        ClientConnection? identity = null;
        try {
            var stream = client.GetStream();
            var framer = options.Framer!;
            var reader = new TcpMessageReader(stream, framer, options.MaxMessageBytes);

            ClientConnectionTicket? ticket;
            using (var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(ct)) {
                handshakeCts.CancelAfter(options.HandshakeTimeout);
                var handshake = new TcpHandshakeContext(
                    stream, framer, reader, client.Client.RemoteEndPoint, client.Client.LocalEndPoint);
                ticket = await handler.AuthenticateAsync(handshake, handshakeCts.Token);
            }

            if (ticket is null) {
                return;
            }

            identity = new ClientConnection {
                ConnectionId = Guid.CreateVersion7().ToString("N"),
                Transport = options.Transport,
                Principal = ticket.Principal,
                PrincipalId = ticket.PrincipalId,
                Metadata = ticket.Metadata,
                ConnectedAt = timeProvider.GetUtcNow(),
            };
            var connection = new TcpClientConnection(identity, client, stream, framer);
            connection.AttachProtocol(handler.CreateProtocol(connection));

            await registry.RegisterAsync(connection, ct);
            try {
                await ReceiveLoopAsync(connection, reader, options.IdleTimeout, ct);
            }
            finally {
                // Unregister must run even when the token is already cancelled — observers tear down
                // subscriptions and presence.
                await registry.UnregisterAsync(identity.ConnectionId, CancellationToken.None);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            // Host shutdown — the normal end of a long-lived link.
        }
        catch (TcpMessageTooLargeException) {
            logger.LogWarning("Connection {ConnectionId} exceeded the message size cap; closing.",
                identity?.ConnectionId ?? "(handshake)");
        }
        catch (IOException) {
            // Abrupt peer death — also a normal end.
        }
        catch (SocketException) {
        }
        catch (Exception failure) {
            logger.LogWarning(failure, "Connection {ConnectionId} codec failed; closing the connection.",
                identity?.ConnectionId ?? "(handshake)");
        }
    }

    private static async Task ReceiveLoopAsync(
        TcpClientConnection connection, TcpMessageReader reader, TimeSpan? idleTimeout, CancellationToken ct) {
        // The pending read is threaded across idle ticks — an idle callback must not abandon it, mirroring
        // the WebSocket adapter.
        Task<TcpFramedMessage?>? pendingRead = null;
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

            if (message.Value.Kind == TcpMessageKind.Text) {
                await connection.Protocol.OnTextAsync(Encoding.UTF8.GetString(message.Value.Payload.Span), ct);
            }
            else {
                await connection.Protocol.OnBinaryAsync(message.Value.Payload, ct);
            }
        }
    }
}
