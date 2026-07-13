using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Elarion.Abstractions.Connections;
using Microsoft.Extensions.Logging;

namespace Elarion.Connections.Tcp;

/// <summary>
/// The endpoint loop bodies, shared by the composition-time hosted services and the runtime
/// <see cref="TcpConnectionEndpoints"/> manager so both paths behave identically. Both loops run until
/// cancelled and never throw.
/// </summary>
internal static class TcpEndpointLoops {
    /// <summary>Accepts sockets on the listening endpoint and drives each through the shared runner.</summary>
    public static async Task RunListenerAsync(
        ElarionTcpListenerOptions options,
        TcpConnectionHandler handler,
        IClientConnectionRegistry registry,
        TimeProvider timeProvider,
        ILogger logger,
        CancellationToken ct,
        Action<TcpEndpointState, string?>? reportState = null) {
        TcpListener listener;
        try {
            listener = new TcpListener(options.ListenEndPoint!);
            listener.Start();
        }
        catch (SocketException failure) {
            logger.LogWarning(failure, "Listening on {EndPoint} failed; the endpoint is not serving.",
                options.ListenEndPoint);
            reportState?.Invoke(TcpEndpointState.Faulted, failure.Message);
            return;
        }

        reportState?.Invoke(TcpEndpointState.Listening, null);
        options.OnListening?.Invoke((IPEndPoint)listener.LocalEndpoint);

        var running = new ConcurrentDictionary<Task, byte>();
        try {
            while (!ct.IsCancellationRequested) {
                TcpClient client;
                try {
                    client = await listener.AcceptTcpClientAsync(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                    break;
                }
                catch (SocketException failure) {
                    // The listener socket died under us; stop serving this endpoint.
                    reportState?.Invoke(TcpEndpointState.Faulted, failure.Message);
                    break;
                }

                // The runner never throws; tracking exists only so teardown can await open connections.
                var run = TcpConnectionRunner.RunAsync(
                    client, options, handler, registry, timeProvider, logger, ct);
                running[run] = 0;
                _ = run.ContinueWith(
                    finished => running.TryRemove(finished, out _),
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        finally {
            listener.Stop();
            try {
                await Task.WhenAll(running.Keys).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
            }
            catch (TimeoutException) {
                // Connections that ignore cancellation get abandoned at teardown rather than blocking it.
            }
        }
    }

    /// <summary>Dials the remote endpoint and keeps the link alive with jittered exponential backoff.</summary>
    public static async Task RunDialerAsync(
        ElarionTcpDialerOptions options,
        TcpConnectionHandler handler,
        IClientConnectionRegistry registry,
        TimeProvider timeProvider,
        ILogger logger,
        CancellationToken ct,
        Action<TcpEndpointState, string?>? reportState = null) {
        var failedAttempts = 0;
        while (!ct.IsCancellationRequested) {
            var client = new TcpClient();
            try {
                await client.ConnectAsync(options.Host!, options.Port, ct);
                reportState?.Invoke(TcpEndpointState.Connected, null);
                var sessionStart = timeProvider.GetTimestamp();
                // The runner owns and disposes the client, and never throws.
                await TcpConnectionRunner.RunAsync(
                    client, options, handler, registry, timeProvider, logger, ct);
                // A session that ended almost immediately (rejected handshake, instant server close) is a
                // failure for backoff purposes — otherwise a misconfigured credential hammers the device
                // at the minimum delay forever. A real session resets the backoff.
                failedAttempts = timeProvider.GetElapsedTime(sessionStart) < options.ReconnectMinDelay
                    ? failedAttempts + 1
                    : 0;
                reportState?.Invoke(TcpEndpointState.Dialing, null);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                client.Dispose();
                break;
            }
            catch (SocketException failure) {
                client.Dispose();
                failedAttempts++;
                logger.LogDebug(failure, "Dialing {Host}:{Port} failed (attempt {Attempt}).",
                    options.Host, options.Port, failedAttempts);
                reportState?.Invoke(TcpEndpointState.Dialing, failure.Message);
            }

            if (ct.IsCancellationRequested) {
                break;
            }

            try {
                await Task.Delay(NextDelay(options, failedAttempts), timeProvider, ct);
            }
            catch (OperationCanceledException) {
                break;
            }
        }
    }

    private static TimeSpan NextDelay(ElarionTcpDialerOptions options, int failedAttempts) {
        // Exponential from the min delay, capped, with ±20 % jitter so a fleet of dialers doesn't
        // thundering-herd a recovering device. A session that ended cleanly retries at the min delay.
        var exponent = Math.Clamp(failedAttempts - 1, 0, 16);
        var baseDelay = options.ReconnectMinDelay * Math.Pow(2, exponent);
        if (baseDelay > options.ReconnectMaxDelay) {
            baseDelay = options.ReconnectMaxDelay;
        }

        var jitter = 0.8 + (Random.Shared.NextDouble() * 0.4);
        return baseDelay * jitter;
    }
}
