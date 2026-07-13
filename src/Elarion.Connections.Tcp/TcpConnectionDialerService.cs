using Elarion.Abstractions.Connections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Elarion.Connections.Tcp;

/// <summary>
/// The composition-time dial-out endpoint: one service instance per <c>AddElarionTcpConnectionDialer</c>
/// call (one per remote device/channel). For endpoints managed at runtime, use
/// <see cref="TcpConnectionEndpoints"/> instead — both run the identical loop.
/// </summary>
internal sealed class TcpConnectionDialerService<THandler>(
    ElarionTcpDialerOptions options, IServiceProvider services) : BackgroundService
    where THandler : TcpConnectionHandler {
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        TcpEndpointLoops.RunDialerAsync(
            options,
            services.GetRequiredService<THandler>(),
            services.GetRequiredService<IClientConnectionRegistry>(),
            services.GetService<TimeProvider>() ?? TimeProvider.System,
            services.GetService<ILoggerFactory>()?.CreateLogger(GetType().Namespace + ".TcpDialer")
                ?? (ILogger)NullLogger.Instance,
            stoppingToken);
}
