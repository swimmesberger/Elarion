using Elarion.Abstractions.Connections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Elarion.Connections.Tcp;

/// <summary>
/// The composition-time listening endpoint: one service instance per
/// <c>AddElarionTcpConnectionListener</c> call, so an app can listen on several ports (e.g. one per device
/// channel type) with distinct handlers/framers. For endpoints managed at runtime (bind/unbind/reconfigure
/// from configuration), use <see cref="TcpConnectionEndpoints"/> instead — both run the identical loop.
/// </summary>
internal sealed class TcpConnectionListenerService<THandler>(
    ElarionTcpListenerOptions options, IServiceProvider services) : BackgroundService
    where THandler : TcpConnectionHandler {
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        TcpEndpointLoops.RunListenerAsync(
            options,
            services.GetRequiredService<THandler>(),
            services.GetRequiredService<IClientConnectionRegistry>(),
            services.GetService<TimeProvider>() ?? TimeProvider.System,
            services.GetService<ILoggerFactory>()?.CreateLogger(GetType().Namespace + ".TcpListener")
                ?? (ILogger)NullLogger.Instance,
            stoppingToken);
}
