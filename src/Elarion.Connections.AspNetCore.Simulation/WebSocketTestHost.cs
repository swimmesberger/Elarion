using System.Net.WebSockets;
using System.Text;
using Elarion.Abstractions.Connections;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Elarion.Connections.AspNetCore.Simulation;

/// <summary>Owns an ephemeral Kestrel host and real <see cref="ClientWebSocket"/> connections for adapter
/// integration tests. Use it for the complete HTTP upgrade, handshake, framing and close path; use
/// <c>SimulatedClientConnection</c> or <c>InMemoryTcpLink</c> for cheaper transport-neutral/TCP tests.</summary>
public sealed class WebSocketTestHost : IAsyncDisposable {
    private readonly WebApplication _application;

    private WebSocketTestHost(WebApplication application, string httpBase, string route) {
        _application = application;
        HttpBase = httpBase;
        Route = route;
    }

    /// <summary>The ephemeral HTTP base address.</summary>
    public string HttpBase { get; }

    /// <summary>The WebSocket route mapped by this host.</summary>
    public string Route { get; }

    /// <summary>The host service provider, including the real connection registry.</summary>
    public IServiceProvider Services => _application.Services;

    /// <summary>The real registry used by the mapped adapter.</summary>
    public IClientConnectionRegistry Registry => Services.GetRequiredService<IClientConnectionRegistry>();

    /// <summary>Starts a host with one real Elarion WebSocket endpoint.</summary>
    public static async Task<WebSocketTestHost> StartAsync<THandler>(
        string route,
        CancellationToken cancellationToken = default,
        Action<IServiceCollection>? configureServices = null,
        Action<ElarionConnectionSocketOptions>? configureEndpoint = null)
        where THandler : WebSocketConnectionHandler {
        ArgumentException.ThrowIfNullOrWhiteSpace(route);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddElarionConnections();
        builder.Services.AddSingleton<THandler>();
        configureServices?.Invoke(builder.Services);

        var app = builder.Build();
        app.UseWebSockets();
        app.MapElarionConnectionSocket<THandler>(route, configureEndpoint);
        await app.StartAsync(cancellationToken).ConfigureAwait(false);
        var httpBase = app.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.First();
        return new WebSocketTestHost(app, httpBase, route);
    }

    /// <summary>Connects a real client, optionally with a query string beginning with <c>?</c>.</summary>
    public async Task<ClientWebSocket> ConnectAsync(CancellationToken cancellationToken = default, string query = "") {
        var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(HttpBase.Replace("http://", "ws://", StringComparison.Ordinal) + Route + query), cancellationToken)
            .ConfigureAwait(false);
        return socket;
    }

    /// <summary>Sends one complete UTF-8 text frame.</summary>
    public static Task SendTextAsync(ClientWebSocket socket, string message, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(socket);
        return socket.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, true, cancellationToken);
    }

    /// <summary>Receives one complete UTF-8 text frame, or <see langword="null"/> on a close frame.</summary>
    public static async Task<string?> ReceiveTextAsync(ClientWebSocket socket, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(socket);
        var buffer = new byte[8192];
        using var assembled = new MemoryStream();
        while (true) {
            var result = await socket.ReceiveAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) {
                return null;
            }

            assembled.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) {
                return Encoding.UTF8.GetString(assembled.ToArray());
            }
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _application.DisposeAsync();
}
