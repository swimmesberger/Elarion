using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Elarion.Connections.Tcp;

/// <summary>
/// Registers TCP connection endpoints as hosted services. Each call adds one endpoint (deliberately not
/// idempotent — an app may listen on several ports or dial several devices, each with its own handler,
/// framer, and options). Requires <c>AddElarionConnections()</c> and the concrete handler in DI.
/// </summary>
public static class TcpConnectionServiceCollectionExtensions {
    /// <summary>Adds a listening endpoint (devices dial in).</summary>
    /// <typeparam name="THandler">The app's connection handler, resolved from DI.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Must set <see cref="ElarionTcpListenerOptions.ListenEndPoint"/> and
    /// <see cref="ElarionTcpConnectionOptions.Framer"/>.</param>
    public static IServiceCollection AddElarionTcpConnectionListener<THandler>(
        this IServiceCollection services, Action<ElarionTcpListenerOptions> configure)
        where THandler : TcpConnectionHandler {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        var options = new ElarionTcpListenerOptions();
        configure(options);
        if (options.ListenEndPoint is null) {
            throw new ArgumentException("ListenEndPoint is required.", nameof(configure));
        }

        ValidateShared(options);
        services.AddSingleton<IHostedService>(sp => new TcpConnectionListenerService<THandler>(options, sp));
        return services;
    }

    /// <summary>Adds a dial-out endpoint (the gateway initiates and maintains the connection).</summary>
    /// <typeparam name="THandler">The app's connection handler, resolved from DI.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Must set <see cref="ElarionTcpDialerOptions.Host"/>,
    /// <see cref="ElarionTcpDialerOptions.Port"/>, and <see cref="ElarionTcpConnectionOptions.Framer"/>.</param>
    public static IServiceCollection AddElarionTcpConnectionDialer<THandler>(
        this IServiceCollection services, Action<ElarionTcpDialerOptions> configure)
        where THandler : TcpConnectionHandler {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        var options = new ElarionTcpDialerOptions();
        configure(options);
        if (string.IsNullOrEmpty(options.Host)) {
            throw new ArgumentException("Host is required.", nameof(configure));
        }

        if (options.Port is <= 0 or > 65535) {
            throw new ArgumentException("Port must be a valid TCP port.", nameof(configure));
        }

        ValidateShared(options);
        services.AddSingleton<IHostedService>(sp => new TcpConnectionDialerService<THandler>(options, sp));
        return services;
    }

    private static void ValidateShared(ElarionTcpConnectionOptions options) {
        if (options.Framer is null) {
            throw new ArgumentException(
                "Framer is required — TCP has no message boundaries; pick LengthPrefixedTcpFramer, DelimitedTextTcpFramer, or a custom framer.",
                nameof(options));
        }

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaxMessageBytes, 0);
    }
}
