using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Authentication;
using Elarion.Abstractions.Connections;
using Elarion.Connections.Tcp.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Elarion.Connections.Tcp;

/// <summary>
/// Drives one accepted or dialed socket through the shared lifecycle — optional TLS upgrade (before framing
/// or application authentication) → framed handshake (timeout-bounded, reject closes with nothing
/// registered) → register → receive loop (framer-fed codec dispatch, idle hook) → unregister — identically
/// for the listener and the dialer. Never throws: every exit path is classified and the socket is disposed.
/// A per-connection <see cref="TcpConnectionLifetime"/> owns the close transition, so competing initiators
/// (peer EOF, explicit close, endpoint shutdown, failures) settle on one first reason, one codec close, one
/// unregistration, one disposal, and one terminal completion.
/// </summary>
internal static class TcpConnectionRunner {
    private static readonly ConditionalWeakTable<SslServerAuthenticationOptions, object>
        UsedServerAuthenticationOptions = new();

    private static readonly ConditionalWeakTable<SslClientAuthenticationOptions, object>
        UsedClientAuthenticationOptions = new();

    public static async Task RunAsync(
        TcpClient client,
        ElarionTcpConnectionOptions options,
        TcpConnectionHandler handler,
        IClientConnectionRegistry registry,
        TimeSpan? defaultInvokeTimeout,
        TimeProvider timeProvider,
        ILogger logger,
        CancellationToken ct,
        TcpLiveConnectionSet? live = null) {
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
            options, handler, registry, defaultInvokeTimeout, timeProvider, logger, ct, live);
    }

    /// <summary>
    /// The transport-agnostic core: any duplex <see cref="Stream"/> works — a socket's network stream, or
    /// an in-memory pair (the socket-free simulation path). <paramref name="transport"/> is disposed on
    /// every exit; <paramref name="applyNoDelay"/> is <see langword="null"/> when the transport has no
    /// socket-level knobs. <paramref name="live"/>, when provided, tracks this connection's lifetime so an
    /// endpoint can request graceful close and force-abort stragglers during shutdown.
    /// </summary>
    public static async Task RunAsync(
        Stream stream,
        TcpConnectionPeer peer,
        IDisposable transport,
        Action<bool>? applyNoDelay,
        ElarionTcpConnectionOptions options,
        TcpConnectionHandler handler,
        IClientConnectionRegistry registry,
        TimeSpan? defaultInvokeTimeout,
        TimeProvider timeProvider,
        ILogger logger,
        CancellationToken ct,
        TcpLiveConnectionSet? live = null) {
        using var lifetime = new TcpConnectionLifetime(transport, ct);
        live?.Add(lifetime);
        ClientConnection? identity = null;
        SslStream? tlsStream = null;
        TcpOutboundWriter? writer = null;
        Exception? registrationRejection = null;
        var transportTag = options.Transport;
        try {
            // The per-connection session (the binding-config lookup point): created before any byte is
            // exchanged so the session's framer governs the handshake too; a null session is an early
            // reject — the socket closes and nothing was ever registered.
            var session = await handler.CreateSessionAsync(peer, lifetime.ReceiveToken);
            if (session is null) return;

            var overrides = session.Settings;
            var framer = overrides?.Framer ?? options.Framer!;
            var maxMessageBytes = overrides?.MaxInboundFrameBytes ?? options.MaxInboundFrameBytes;
            var maxOutboundMessageBytes = overrides?.MaxOutboundFrameBytes ?? options.MaxOutboundFrameBytes;
            var maxPendingSends = overrides?.MaxPendingSends ?? options.MaxPendingSends;
            var idleTimeout = overrides?.IdleTimeout ?? options.IdleTimeout;
            var handshakeTimeout = overrides?.HandshakeTimeout ?? options.HandshakeTimeout;
            transportTag = overrides?.Transport ?? options.Transport;
            var readBufferBytes = overrides?.InitialReadBufferBytes ?? options.InitialReadBufferBytes;
            var sendBufferBytes = overrides?.InitialSendBufferBytes ?? options.InitialSendBufferBytes;
            var tls = overrides?.Tls ?? options.Tls;
            ValidateResolvedSettings(
                framer, maxMessageBytes, maxOutboundMessageBytes, readBufferBytes, sendBufferBytes, maxPendingSends);
            applyNoDelay?.Invoke(overrides?.NoDelay ?? options.NoDelay);
            stream = await UpgradeToTlsAsync(stream, peer, tls, options, timeProvider, lifetime.ReceiveToken);
            tlsStream = stream as SslStream;
            var reader = new TcpMessageReader(stream, framer, maxMessageBytes, readBufferBytes);

            ClientConnectionTicket? ticket;
            using (var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ReceiveToken)) {
                handshakeCts.CancelAfter(handshakeTimeout);
                var handshake = new TcpHandshakeContext(
                    stream, framer, reader, peer.RemoteEndPoint, peer.LocalEndPoint);
                try {
                    ticket = await session.AuthenticateAsync(handshake, handshakeCts.Token);
                }
                catch (OperationCanceledException) when (!lifetime.ReceiveToken.IsCancellationRequested) {
                    // The handshake deadline, not host shutdown — a slow/silent peer is rejected quietly,
                    // not logged as a codec failure.
                    logger.LogDebug("Connection from {RemoteEndPoint} exceeded the handshake deadline.",
                        peer.RemoteEndPoint);
                    TcpConnectionTelemetry.RecordFailure(transportTag, "handshake");
                    return;
                }
            }

            if (ticket is null) return;

            identity = new ClientConnection {
                ConnectionId = Guid.CreateVersion7().ToString("N"),
                Transport = transportTag,
                Principal = ticket.Principal,
                PrincipalId = ticket.PrincipalId,
                Metadata = ticket.Metadata,
                ConnectedAt = timeProvider.GetUtcNow()
            };
            writer = new TcpOutboundWriter(
                stream, framer, sendBufferBytes, maxOutboundMessageBytes, maxPendingSends,
                identity.ConnectionId, transportTag, lifetime);
            lifetime.AttachWriter(writer);
            var connection = new TcpClientConnection(identity, writer, lifetime, defaultInvokeTimeout);
            connection.AttachProtocol(session.CreateProtocol(connection));

            var ownsRegistration = false;
            try {
                try {
                    await registry.RegisterAsync(connection, ct);
                    ownsRegistration = true;
                }
                catch (Exception failure) {
                    // Observer cancellation can throw after the registry mutation. A duplicate-id failure,
                    // however, belongs to another sink and must never unregister that live connection.
                    ownsRegistration = registry.TryGet(identity.ConnectionId, out var registered)
                                       && ReferenceEquals(registered, connection);
                    if (!ownsRegistration && failure is not OperationCanceledException) {
                        // A rejected registration is an adapter/ticket bug (e.g. an authenticated ticket
                        // without a PrincipalId, or a duplicate connection id) — the peer did nothing
                        // wrong, and burying it as a generic codec warning cost real debugging time.
                        // Fail loud: error-level log plus its own bounded telemetry stage.
                        registrationRejection = failure;
                        logger.LogError(failure,
                            "Connection {ConnectionId} was rejected by the registry; the connection is closed. This is an adapter/ticket bug, not a peer failure.",
                            identity.ConnectionId);
                        TcpConnectionTelemetry.RecordFailure(transportTag, "registration");
                    }

                    throw;
                }

                // Required codec setup is visible to observers but runs before any framed message. A
                // failure is handled by this connection's normal OnClosed/unregister teardown.
                await connection.Protocol.OnOpenedAsync(connection.Connection, lifetime.ReceiveToken);
                await ReceiveLoopAsync(connection, reader, idleTimeout, transportTag, lifetime.ReceiveToken);
                lifetime.TryBeginClose(null);
            }
            catch (OperationCanceledException) {
                // Host shutdown, endpoint stop, or an explicit close request — all clean ends; a close
                // that carries a reason recorded it first, so this no-op keeps the first reason.
                lifetime.TryBeginClose(null);
                throw;
            }
            catch (Exception failure) {
                lifetime.TryBeginClose(failure);
                throw;
            }
            finally {
                if (ownsRegistration) {
                    // Ordered teardown: outbound work settles (drain on a clean close, fault on failure or
                    // abort — bounded by the shutdown grace period either way), then the codec learns the
                    // connection ended before observers do — its pending invokes and conversation waiters
                    // must fault, not hang. Then unregister must run even when the token is already
                    // cancelled — observers tear down subscriptions and presence.
                    await SettleOutboundAsync(writer, lifetime, options.ShutdownGracePeriod, timeProvider);
                    await NotifyClosedSafelyAsync(connection, lifetime.CloseReason, logger);
                    await registry.UnregisterAsync(identity.ConnectionId, CancellationToken.None);
                    TcpConnectionTelemetry.RecordClosed(transportTag, lifetime.WasForced);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested || lifetime.CloseRequested) {
            // Host shutdown or a lifetime-initiated close — the normal end of a long-lived link.
        }
        catch (AuthenticationException) {
            logger.LogDebug("TCP TLS establishment failed; the connection was rejected before framing.");
            TcpConnectionTelemetry.RecordFailure(transportTag, "tls");
        }
        catch (OperationCanceledException) when (identity is null) {
            logger.LogDebug("TCP TLS establishment or framed authentication timed out; the connection was rejected.");
            TcpConnectionTelemetry.RecordFailure(transportTag, "handshake");
        }
        catch (TcpMessageTooLargeException) {
            logger.LogWarning("Connection {ConnectionId} exceeded the message size cap; closing.",
                identity?.ConnectionId ?? "(handshake)");
            TcpConnectionTelemetry.RecordFailure(transportTag, "oversize");
        }
        catch (TcpMessageFramingException failure) {
            logger.LogWarning(failure, "Connection {ConnectionId} returned an invalid TCP framing result; closing.",
                identity?.ConnectionId ?? "(handshake)");
            TcpConnectionTelemetry.RecordFailure(transportTag, "framing");
        }
        catch (ObjectDisposedException) when (lifetime.CloseRequested) {
            // A forced abort disposed the raw transport under a blocked read — that is the abort working,
            // not a codec failure.
        }
        catch (IOException) {
            // Abrupt peer death — also a normal end.
            TcpConnectionTelemetry.RecordFailure(transportTag, "io");
        }
        catch (SocketException) {
            TcpConnectionTelemetry.RecordFailure(transportTag, "io");
        }
        catch (Exception failure) when (ReferenceEquals(failure, registrationRejection)) {
            // Already reported at error level with the "registration" stage at the rejection site.
        }
        catch (Exception failure) {
            logger.LogWarning(failure, "Connection {ConnectionId} codec failed; closing the connection.",
                identity?.ConnectionId ?? "(handshake)");
            TcpConnectionTelemetry.RecordFailure(transportTag, "codec");
        }
        finally {
            if (tlsStream is not null) await tlsStream.DisposeAsync();

            lifetime.DisposeTransport();
            writer?.Dispose();
            live?.Remove(lifetime);
            lifetime.MarkClosed();
        }
    }

    // Settles every admitted outbound send exactly once: drain when the close is clean and not forced,
    // fault otherwise. The grace period bounds a drain against a peer that stopped reading — after it, the
    // raw transport is aborted so the in-flight write faults deterministically.
    private static async ValueTask SettleOutboundAsync(
        TcpOutboundWriter writer, TcpConnectionLifetime lifetime, TimeSpan gracePeriod, TimeProvider timeProvider) {
        var reason = lifetime.CloseReason;
        if (reason is null && !lifetime.WasForced)
            writer.BeginGracefulClose();
        else
            writer.Abort(reason);

        try {
            await writer.DrainCompletion.WaitAsync(gracePeriod, timeProvider);
        }
        catch (TimeoutException) {
            lifetime.Abort(reason);
            await writer.DrainCompletion;
        }
    }

    private static async ValueTask<Stream> UpgradeToTlsAsync(
        Stream stream,
        TcpConnectionPeer peer,
        TcpTlsOptions? tls,
        ElarionTcpConnectionOptions endpoint,
        TimeProvider timeProvider,
        CancellationToken ct) {
        if (tls is null) return stream;

        ValidateTlsDirection(tls, endpoint);
        if (tls.HandshakeTimeout <= TimeSpan.Zero && tls.HandshakeTimeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentException(
                "TLS HandshakeTimeout must be positive or Timeout.InfiniteTimeSpan.", nameof(tls));

        using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        handshakeCts.CancelAfter(tls.HandshakeTimeout);

        var started = timeProvider.GetTimestamp();
        var sslStream = new SslStream(stream, true);
        try {
            switch (tls) {
                case TcpServerTlsOptions serverTls:
                    var serverOptions = await serverTls.CreateAuthenticationOptionsAsync(peer, handshakeCts.Token);
                    ArgumentNullException.ThrowIfNull(serverOptions);
                    RegisterFreshAuthenticationOptions(UsedServerAuthenticationOptions, serverOptions);
                    await sslStream.AuthenticateAsServerAsync(serverOptions, handshakeCts.Token);
                    break;

                case TcpClientTlsOptions clientTls:
                    var clientOptions = await clientTls.CreateAuthenticationOptionsAsync(peer, handshakeCts.Token);
                    ArgumentNullException.ThrowIfNull(clientOptions);
                    RegisterFreshAuthenticationOptions(UsedClientAuthenticationOptions, clientOptions);
                    await sslStream.AuthenticateAsClientAsync(clientOptions, handshakeCts.Token);
                    break;

                default:
                    throw new ArgumentException("The resolved TLS policy is not supported.", nameof(tls));
            }

            TcpConnectionTelemetry.RecordTlsHandshake("ok", timeProvider.GetElapsedTime(started));
            return sslStream;
        }
        catch (Exception failure) {
            var outcome = failure switch {
                OperationCanceledException when ct.IsCancellationRequested => "cancelled",
                OperationCanceledException => "timeout",
                _ => "failed"
            };
            TcpConnectionTelemetry.RecordTlsHandshake(outcome, timeProvider.GetElapsedTime(started));
            await sslStream.DisposeAsync();
            throw;
        }
    }

    private static void RegisterFreshAuthenticationOptions<TOptions>(
        ConditionalWeakTable<TOptions, object> usedOptions,
        TOptions authenticationOptions)
        where TOptions : class {
        if (!usedOptions.TryAdd(authenticationOptions, new object()))
            throw new InvalidOperationException(
                "TLS authentication option factories must return a fresh BCL options instance for each connection.");
    }

    private static void ValidateTlsDirection(TcpTlsOptions tls, ElarionTcpConnectionOptions endpoint) {
        if (endpoint is ElarionTcpListenerOptions && tls is not TcpServerTlsOptions)
            throw new ArgumentException("Listener connections require TcpServerTlsOptions.", nameof(tls));

        if (endpoint is ElarionTcpDialerOptions && tls is not TcpClientTlsOptions)
            throw new ArgumentException("Dialer connections require TcpClientTlsOptions.", nameof(tls));

        if (endpoint is not ElarionTcpListenerOptions and not ElarionTcpDialerOptions)
            throw new ArgumentException(
                "TLS requires a listener or dialer endpoint so the adapter can select server or client mode.",
                nameof(endpoint));
    }

    private static void ValidateResolvedSettings(
        TcpMessageFramer framer, int maxMessageBytes, int maxOutboundMessageBytes,
        int readBufferBytes, int sendBufferBytes, int maxPendingSends) {
        ArgumentNullException.ThrowIfNull(framer);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxMessageBytes, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxOutboundMessageBytes, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(readBufferBytes, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sendBufferBytes, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxPendingSends, 0);
        if (readBufferBytes > maxMessageBytes)
            throw new ArgumentOutOfRangeException(nameof(readBufferBytes),
                "InitialReadBufferBytes cannot exceed MaxInboundFrameBytes.");

        if (sendBufferBytes > maxOutboundMessageBytes)
            throw new ArgumentOutOfRangeException(nameof(sendBufferBytes),
                "InitialSendBufferBytes cannot exceed MaxOutboundFrameBytes.");
    }

    // Failure-isolated: a throwing OnClosedAsync must never break teardown/unregistration.
    private static async ValueTask NotifyClosedSafelyAsync(
        TcpClientConnection connection, Exception? reason, ILogger logger) {
        try {
            await connection.Protocol.OnClosedAsync(connection.Connection, reason, CancellationToken.None);
        }
        catch (Exception failure) {
            logger.LogWarning(failure, "Connection {ConnectionId} codec OnClosedAsync failed.",
                connection.Connection.ConnectionId);
        }
    }

    private static async Task ReceiveLoopAsync(
        TcpClientConnection connection, TcpMessageReader reader, TimeSpan? idleTimeout, string transportTag,
        CancellationToken ct) {
        while (true) {
            var message = idleTimeout is { } window
                ? await ReadWithIdleAsync(connection, reader, window, transportTag, ct)
                : await reader.ReadAsync(ct);
            if (message is null) return;

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
        TcpClientConnection connection, TcpMessageReader reader, TimeSpan window, string transportTag,
        CancellationToken ct) {
        var read = reader.ReadAsync(ct);
        if (read.IsCompletedSuccessfully) return read.Result;

        var pending = read.AsTask();
        // If OnIdleAsync throws (documented dead-link detection) the connection tears down while this read
        // is still in flight on the doomed stream — observe its eventual fault so it never surfaces as an
        // unobserved task exception.
        _ = pending.ContinueWith(
            static faulted => _ = faulted.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
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
            TcpConnectionTelemetry.RecordIdle(transportTag);
            await connection.Protocol.OnIdleAsync(ct);
        }
    }
}
