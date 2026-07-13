using System.Net.Sockets;
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
            // Per-connection configuration (the binding-config lookup point): resolved before any byte is
            // exchanged so the chosen framer governs the handshake too; nulls inherit the endpoint options.
            var peer = new TcpConnectionPeer(client.Client.RemoteEndPoint, client.Client.LocalEndPoint);
            var overrides = await handler.ConfigureConnectionAsync(peer, ct);
            var framer = overrides?.Framer ?? options.Framer!;
            var maxMessageBytes = overrides?.MaxMessageBytes ?? options.MaxMessageBytes;
            var idleTimeout = overrides?.IdleTimeout ?? options.IdleTimeout;
            var handshakeTimeout = overrides?.HandshakeTimeout ?? options.HandshakeTimeout;
            var transport = overrides?.Transport ?? options.Transport;
            client.Client.NoDelay = overrides?.NoDelay ?? options.NoDelay;
            var readBufferBytes = overrides?.InitialReadBufferBytes ?? options.InitialReadBufferBytes;
            var sendBufferBytes = overrides?.InitialSendBufferBytes ?? options.InitialSendBufferBytes;
            var reader = new TcpMessageReader(stream, framer, maxMessageBytes, readBufferBytes);

            ClientConnectionTicket? ticket;
            using (var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(ct)) {
                handshakeCts.CancelAfter(handshakeTimeout);
                var handshake = new TcpHandshakeContext(
                    stream, framer, reader, peer.RemoteEndPoint, peer.LocalEndPoint);
                ticket = await handler.AuthenticateAsync(handshake, handshakeCts.Token);
            }

            if (ticket is null) {
                return;
            }

            identity = new ClientConnection {
                ConnectionId = Guid.CreateVersion7().ToString("N"),
                Transport = transport,
                Principal = ticket.Principal,
                PrincipalId = ticket.PrincipalId,
                Metadata = ticket.Metadata,
                ConnectedAt = timeProvider.GetUtcNow(),
            };
            var connection = new TcpClientConnection(identity, client, stream, framer, sendBufferBytes);
            connection.AttachProtocol(handler.CreateProtocol(connection));

            await registry.RegisterAsync(connection, ct);
            try {
                await ReceiveLoopAsync(connection, reader, idleTimeout, ct);
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
        while (true) {
            var message = idleTimeout is { } window
                ? await ReadWithIdleAsync(connection, reader, window, ct)
                : await reader.ReadAsync(ct);
            if (message is null) {
                return;
            }

            // Bytes are bytes on TCP: every message is a raw slice on the binary leg; a text protocol's
            // codec decodes it — the string is paid for only where it is wanted.
            await connection.Protocol.OnBinaryAsync(message.Value, ct);
        }
    }

    // The idle race only engages when the read actually goes async: buffered messages complete
    // synchronously and pay for no timer, no Task materialization, no linked token source — the hot path
    // under load is identical with and without an idle window. The pending read is never abandoned (a
    // second concurrent read would be invalid); idle ticks fire per elapsed window until data arrives.
    private static async ValueTask<ReadOnlyMemory<byte>?> ReadWithIdleAsync(
        TcpClientConnection connection, TcpMessageReader reader, TimeSpan window, CancellationToken ct) {
        var read = reader.ReadAsync(ct);
        if (read.IsCompletedSuccessfully) {
            return read.Result;
        }

        var pending = read.AsTask();
        while (true) {
            using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var winner = await Task.WhenAny(pending, Task.Delay(window, delayCts.Token));
            if (winner == pending) {
                delayCts.Cancel();
                return await pending;
            }

            await connection.Protocol.OnIdleAsync(ct);
        }
    }
}
