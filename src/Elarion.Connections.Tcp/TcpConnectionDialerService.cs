using System.Net.Sockets;
using Elarion.Abstractions.Connections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Elarion.Connections.Tcp;

/// <summary>
/// The dial-out endpoint: the gateway initiates the connection to the device and keeps it alive — connect,
/// run the shared lifecycle, and on any end (device closed, network fault, failed connect) redial with
/// jittered exponential backoff, resetting after each successful connection. One service instance per
/// <c>AddElarionTcpConnectionDialer</c> call (one per remote device/channel).
/// </summary>
internal sealed class TcpConnectionDialerService<THandler>(
    ElarionTcpDialerOptions options, IServiceProvider services) : BackgroundService
    where THandler : TcpConnectionHandler {
    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        var registry = services.GetRequiredService<IClientConnectionRegistry>();
        var handler = services.GetRequiredService<THandler>();
        var timeProvider = services.GetService<TimeProvider>() ?? TimeProvider.System;
        var logger = services.GetService<ILoggerFactory>()?.CreateLogger(GetType().Namespace + ".TcpDialer")
            ?? (ILogger)NullLogger.Instance;

        var failedAttempts = 0;
        while (!stoppingToken.IsCancellationRequested) {
            var client = new TcpClient();
            try {
                await client.ConnectAsync(options.Host!, options.Port, stoppingToken);
                failedAttempts = 0;
                // The runner owns and disposes the client, and never throws.
                await TcpConnectionRunner.RunAsync(
                    client, options, handler, registry, timeProvider, logger, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
                client.Dispose();
                break;
            }
            catch (SocketException failure) {
                client.Dispose();
                failedAttempts++;
                logger.LogDebug(failure, "Dialing {Host}:{Port} failed (attempt {Attempt}).",
                    options.Host, options.Port, failedAttempts);
            }

            if (stoppingToken.IsCancellationRequested) {
                break;
            }

            try {
                await Task.Delay(NextDelay(failedAttempts), stoppingToken);
            }
            catch (OperationCanceledException) {
                break;
            }
        }
    }

    private TimeSpan NextDelay(int failedAttempts) {
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
