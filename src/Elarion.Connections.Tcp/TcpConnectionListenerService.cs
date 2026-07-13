using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Elarion.Abstractions.Connections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Elarion.Connections.Tcp;

/// <summary>
/// The listening endpoint: accepts sockets and drives each through the shared runner concurrently. One
/// service instance per <c>AddElarionTcpConnectionListener</c> call, so an app can listen on several ports
/// (e.g. one per device channel type) with distinct handlers/framers.
/// </summary>
internal sealed class TcpConnectionListenerService<THandler>(
    ElarionTcpListenerOptions options, IServiceProvider services) : BackgroundService
    where THandler : TcpConnectionHandler {
    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        var registry = services.GetRequiredService<IClientConnectionRegistry>();
        var handler = services.GetRequiredService<THandler>();
        var timeProvider = services.GetService<TimeProvider>() ?? TimeProvider.System;
        var logger = services.GetService<ILoggerFactory>()?.CreateLogger(GetType().Namespace + ".TcpListener")
            ?? (ILogger)NullLogger.Instance;

        var listener = new TcpListener(options.ListenEndPoint!);
        listener.Start();
        options.OnListening?.Invoke((IPEndPoint)listener.LocalEndpoint);

        var running = new ConcurrentDictionary<Task, byte>();
        try {
            while (!stoppingToken.IsCancellationRequested) {
                TcpClient client;
                try {
                    client = await listener.AcceptTcpClientAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
                    break;
                }

                // The runner never throws; tracking exists only so shutdown can await open connections.
                var run = TcpConnectionRunner.RunAsync(
                    client, options, handler, registry, timeProvider, logger, stoppingToken);
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
                // Connections that ignore cancellation get abandoned at shutdown rather than blocking it.
            }
        }
    }
}
