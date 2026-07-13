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
        Stream stream;
        TcpConnectionPeer peer;
        try {
            stream = client.GetStream();
            peer = new TcpConnectionPeer(client.Client.RemoteEndPoint, client.Client.LocalEndPoint);
        }
        catch (Exception) {
            // The socket died between accept/connect and here — nothing was registered.
            client.Dispose();
            return;
        }

        await RunAsync(
            stream, peer, client, noDelay => client.Client.NoDelay = noDelay,
            options, handler, registry, timeProvider, logger, ct);
    }

    /// <summary>
    /// The transport-agnostic core: any duplex <see cref="Stream"/> works — a socket's network stream, or
    /// an in-memory pair (the socket-free simulation path). <paramref name="transport"/> is disposed on
    /// every exit; <paramref name="applyNoDelay"/> is <see langword="null"/> when the transport has no
    /// socket-level knobs.
    /// </summary>
    public static async Task RunAsync(
        Stream stream,
        TcpConnectionPeer peer,
        IDisposable transport,
        Action<bool>? applyNoDelay,
        ElarionTcpConnectionOptions options,
        TcpConnectionHandler handler,
        IClientConnectionRegistry registry,
        TimeProvider timeProvider,
        ILogger logger,
        CancellationToken ct) {
        using var owned = transport;
        ClientConnection? identity = null;
        try {
            // Per-connection configuration (the binding-config lookup point): resolved before any byte is
            // exchanged so the chosen framer governs the handshake too; nulls inherit the endpoint options.
            var overrides = await handler.ConfigureConnectionAsync(peer, ct);
            var framer = overrides?.Framer ?? options.Framer!;
            var maxMessageBytes = overrides?.MaxMessageBytes ?? options.MaxMessageBytes;
            var idleTimeout = overrides?.IdleTimeout ?? options.IdleTimeout;
            var handshakeTimeout = overrides?.HandshakeTimeout ?? options.HandshakeTimeout;
            var transportTag = overrides?.Transport ?? options.Transport;
            applyNoDelay?.Invoke(overrides?.NoDelay ?? options.NoDelay);
            var readBufferBytes = overrides?.InitialReadBufferBytes ?? options.InitialReadBufferBytes;
            var sendBufferBytes = overrides?.InitialSendBufferBytes ?? options.InitialSendBufferBytes;
            var reader = new TcpMessageReader(stream, framer, maxMessageBytes, readBufferBytes);

            ClientConnectionTicket? ticket;
            using (var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(ct)) {
                handshakeCts.CancelAfter(handshakeTimeout);
                var handshake = new TcpHandshakeContext(
                    stream, framer, reader, peer.RemoteEndPoint, peer.LocalEndPoint);
                try {
                    ticket = await handler.AuthenticateAsync(handshake, handshakeCts.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
                    // The handshake deadline, not host shutdown — a slow/silent peer is rejected quietly,
                    // not logged as a codec failure.
                    logger.LogDebug("Connection from {RemoteEndPoint} exceeded the handshake deadline.",
                        peer.RemoteEndPoint);
                    return;
                }
            }

            if (ticket is null) {
                return;
            }

            identity = new ClientConnection {
                ConnectionId = Guid.CreateVersion7().ToString("N"),
                Transport = transportTag,
                Principal = ticket.Principal,
                PrincipalId = ticket.PrincipalId,
                Metadata = ticket.Metadata,
                ConnectedAt = timeProvider.GetUtcNow(),
            };
            var connection = new TcpClientConnection(identity, stream, framer, sendBufferBytes, closeTransport: transport.Dispose);
            connection.AttachProtocol(handler.CreateProtocol(connection));

            try {
                // Registration lives inside this try: RegisterAsync mutates the index before dispatching
                // observers, so an abort mid-registration must still reach the unregister in finally.
                await registry.RegisterAsync(connection, ct);
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
        // If OnIdleAsync throws (documented dead-link detection) the connection tears down while this read
        // is still in flight on the doomed stream — observe its eventual fault so it never surfaces as an
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
}
