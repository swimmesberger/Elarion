using Elarion.Abstractions.Connections;
using Elarion.Connections.Tcp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Elarion.Connections.Simulation;

/// <summary>
/// Runs the full TCP connection lifecycle — per-connection settings, handshake, framing, registry
/// lifecycle, codec dispatch, idle hook — over an in-memory duplex pair instead of a socket. The default
/// for simulators and unit tests: no ports, no firewall prompts, no OS resources, fully deterministic.
/// Real sockets stay for integration tests of the TCP adapter itself.
/// </summary>
/// <example>
/// <code>
/// await using var link = InMemoryTcpLink.Start(handler, registry, o => o.Framer = framer);
/// var reply = await link.Client.ReceiveTextAsync(ct);      // the handler's challenge
/// </code>
/// </example>
public static class InMemoryTcpLink {
    /// <summary>Starts one in-memory connection against <paramref name="handler"/>.</summary>
    /// <param name="handler">The connection handler under test/simulation (authenticator + codec).</param>
    /// <param name="registry">The registry the connection registers with (resolve from DI so observers,
    /// twins, and the client-events bridge participate exactly as in production).</param>
    /// <param name="configure">Endpoint options; must set <c>Framer</c>. <c>Transport</c> defaults to
    /// <c>"in-memory"</c>.</param>
    /// <param name="timeProvider">Optional clock.</param>
    /// <param name="logger">Optional logger for codec failures.</param>
    public static InMemoryTcpLinkSession Start(
        TcpConnectionHandler handler,
        IClientConnectionRegistry registry,
        Action<ElarionTcpConnectionOptions> configure,
        TimeProvider? timeProvider = null,
        ILogger? logger = null) {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(configure);
        var options = new ElarionTcpConnectionOptions { Transport = "in-memory" };
        configure(options);
        if (options.Framer is null) {
            throw new ArgumentException("Framer is required.", nameof(configure));
        }

        var (serverEnd, clientEnd) = InMemoryDuplexStream.CreatePair();
        var shutdown = new CancellationTokenSource();
        var observing = new ObservingRegistry(registry);
        var serverRun = TcpConnectionRunner.RunAsync(
            serverEnd, new TcpConnectionPeer(null, null), serverEnd, applyNoDelay: null,
            options, handler, observing, timeProvider ?? TimeProvider.System,
            logger ?? NullLogger.Instance, shutdown.Token);
        // A rejected handshake never registers: the run completing first faults the ServerConnection await
        // instead of hanging it. The run itself never throws.
        _ = serverRun.ContinueWith(
            _ => observing.Registered.TrySetException(new InvalidOperationException(
                "The connection ended without registering — the handshake was rejected or failed.")),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        var client = TcpSimulatorClient.FromStream(clientEnd, options.Framer);
        return new InMemoryTcpLinkSession(client, serverRun, observing.Registered.Task, shutdown);
    }

    /// <summary>Forwards to the real registry while completing the session's deterministic
    /// registered-signal — link consumers must never have to poll or race the handshake.</summary>
    private sealed class ObservingRegistry(IClientConnectionRegistry inner) : IClientConnectionRegistry {
        public TaskCompletionSource<IClientConnectionSink> Registered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask RegisterAsync(IClientConnectionSink connection, CancellationToken ct = default) {
            await inner.RegisterAsync(connection, ct);
            Registered.TrySetResult(connection);
        }

        public ValueTask UnregisterAsync(string connectionId, CancellationToken ct = default) =>
            inner.UnregisterAsync(connectionId, ct);

        public bool TryGet(string connectionId, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IClientConnectionSink? connection) =>
            inner.TryGet(connectionId, out connection);

        public IReadOnlyList<IClientConnectionSink> GetForPrincipal(string principalId) =>
            inner.GetForPrincipal(principalId);

        public IReadOnlyCollection<IClientConnectionSink> Connections => inner.Connections;
    }
}

/// <summary>One live in-memory link: the simulator-side client plus the server side's lifecycle task.
/// Disposing closes the client end — the server observes end-of-stream, unregisters, and completes,
/// exactly like a real disconnect.</summary>
public sealed class InMemoryTcpLinkSession : IAsyncDisposable {
    private readonly CancellationTokenSource _shutdown;

    internal InMemoryTcpLinkSession(
        TcpSimulatorClient client, Task serverCompletion, Task<IClientConnectionSink> serverConnection,
        CancellationTokenSource shutdown) {
        Client = client;
        ServerCompletion = serverCompletion;
        ServerConnection = serverConnection;
        _shutdown = shutdown;
    }

    /// <summary>The simulator/peer side of the link.</summary>
    public TcpSimulatorClient Client { get; }

    /// <summary>
    /// Completes with the server-side sink once the handshake succeeded and the connection is registered —
    /// the deterministic "connected" signal (asserting registry state right after the handshake's last
    /// frame would race registration). Faults when the handshake was rejected.
    /// </summary>
    public Task<IClientConnectionSink> ServerConnection { get; }

    /// <summary>Completes when the server side has fully torn down (unregistered); await it after closing
    /// the client for deterministic teardown assertions.</summary>
    public Task ServerCompletion { get; }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() {
        await Client.DisposeAsync();
        try {
            await ServerCompletion.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        }
        catch (TimeoutException) {
            await _shutdown.CancelAsync();
        }

        _shutdown.Dispose();
    }
}
