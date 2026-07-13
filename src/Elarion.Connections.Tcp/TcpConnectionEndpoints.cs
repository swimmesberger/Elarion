using Elarion.Abstractions.Connections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Elarion.Connections.Tcp;

/// <summary>
/// Runtime-managed TCP endpoints — the binding-configuration shape: an app that stores its device bindings
/// as data (which port to listen on, which device to dial, which framing, which direction) applies them
/// here at startup and re-applies on every configuration change. <c>Apply…</c> is an upsert: applying a
/// name that is already running tears the old endpoint down first (its connections unregister — observers
/// and twins see the disconnect) and starts fresh with the new settings, so <b>changing a binding means a
/// reconnect under the new settings</b> — including flipping the direction (a name previously applied as a
/// dialer can be re-applied as a listener, and vice versa).
/// </summary>
/// <remarks>
/// <para>
/// Every endpoint <b>advertises its state</b>: <see cref="Statuses"/>/<see cref="GetStatus"/> answer "which
/// bindings are serving, which failed to bind, and why" (a listener whose port could not be bound is
/// <see cref="TcpEndpointState.Faulted"/> with the reason; a dialer between attempts is
/// <see cref="TcpEndpointState.Dialing"/> carrying the last failure). <see cref="StatusChanged"/> fires on
/// every transition — project it onto a client event to surface binding health live in an admin UI.
/// </para>
/// <para>
/// Registered by <c>AddElarionTcpConnectionEndpoints()</c> (also as a hosted service so host shutdown tears
/// every dynamic endpoint down). Endpoints run the identical loops as the composition-time
/// <c>AddElarionTcpConnectionListener</c>/<c>Dialer</c> registrations — use those when the bindings are
/// static, this manager when they are data.
/// </para>
/// </remarks>
public sealed class TcpConnectionEndpoints(IServiceProvider services) : IHostedService {
    private readonly Dictionary<string, Endpoint> _endpoints = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _mutation = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();

    /// <summary>Fires on every endpoint state transition (from the endpoint's own loop — treat it like an
    /// event handler: fast, non-throwing). The subscription point for advertising binding health.</summary>
    public event Action<TcpEndpointStatus>? StatusChanged;

    /// <summary>The names of the currently applied endpoints (snapshot).</summary>
    public IReadOnlyCollection<string> EndpointNames {
        get {
            lock (_endpoints) {
                return [.. _endpoints.Keys];
            }
        }
    }

    /// <summary>The advertised status of every applied endpoint (snapshot).</summary>
    public IReadOnlyCollection<TcpEndpointStatus> Statuses {
        get {
            lock (_endpoints) {
                return [.. _endpoints.Values.Select(static e => e.Status)];
            }
        }
    }

    /// <summary>The advertised status of the endpoint under <paramref name="name"/>, or
    /// <see langword="null"/> when no such endpoint is applied.</summary>
    public TcpEndpointStatus? GetStatus(string name) {
        ArgumentException.ThrowIfNullOrEmpty(name);
        lock (_endpoints) {
            return _endpoints.TryGetValue(name, out var endpoint) ? endpoint.Status : null;
        }
    }

    /// <summary>Applies (starts or reconfigures) a listening endpoint under <paramref name="name"/>.</summary>
    /// <typeparam name="THandler">The handler resolved from DI for this endpoint's connections.</typeparam>
    /// <param name="name">The app's binding key (e.g. <c>"device-7:management"</c>).</param>
    /// <param name="configure">Must set <c>ListenEndPoint</c> and <c>Framer</c>.</param>
    /// <param name="ct">Cancels waiting for a previous endpoint's teardown.</param>
    public ValueTask ApplyListenerAsync<THandler>(
        string name, Action<ElarionTcpListenerOptions> configure, CancellationToken ct = default)
        where THandler : TcpConnectionHandler {
        ArgumentNullException.ThrowIfNull(configure);
        var options = new ElarionTcpListenerOptions();
        configure(options);
        if (options.ListenEndPoint is null) {
            throw new ArgumentException("ListenEndPoint is required.", nameof(configure));
        }

        TcpConnectionServiceCollectionExtensions.ValidateShared(options);
        return ApplyAsync(name, TcpEndpointMode.Listener, (report, token) => TcpEndpointLoops.RunListenerAsync(
            options, services.GetRequiredService<THandler>(), Registry, Time, Logger("TcpListener"), token, report), ct);
    }

    /// <summary>Applies (starts or reconfigures) a dial-out endpoint under <paramref name="name"/>.</summary>
    /// <typeparam name="THandler">The handler resolved from DI for this endpoint's connections.</typeparam>
    /// <param name="name">The app's binding key.</param>
    /// <param name="configure">Must set <c>Host</c>, <c>Port</c>, and <c>Framer</c>.</param>
    /// <param name="ct">Cancels waiting for a previous endpoint's teardown.</param>
    public ValueTask ApplyDialerAsync<THandler>(
        string name, Action<ElarionTcpDialerOptions> configure, CancellationToken ct = default)
        where THandler : TcpConnectionHandler {
        ArgumentNullException.ThrowIfNull(configure);
        var options = new ElarionTcpDialerOptions();
        configure(options);
        if (string.IsNullOrEmpty(options.Host)) {
            throw new ArgumentException("Host is required.", nameof(configure));
        }

        if (options.Port is <= 0 or > 65535) {
            throw new ArgumentException("Port must be a valid TCP port.", nameof(configure));
        }

        TcpConnectionServiceCollectionExtensions.ValidateShared(options);
        return ApplyAsync(name, TcpEndpointMode.Dialer, (report, token) => TcpEndpointLoops.RunDialerAsync(
            options, services.GetRequiredService<THandler>(), Registry, Time, Logger("TcpDialer"), token, report), ct);
    }

    /// <summary>
    /// Stops and removes the endpoint under <paramref name="name"/> (its connections unregister);
    /// <see langword="false"/> when no such endpoint is applied.
    /// </summary>
    public async ValueTask<bool> RemoveAsync(string name, CancellationToken ct = default) {
        ArgumentException.ThrowIfNullOrEmpty(name);
        await _mutation.WaitAsync(ct);
        try {
            Endpoint? existing;
            lock (_endpoints) {
                if (!_endpoints.Remove(name, out existing)) {
                    return false;
                }
            }

            await StopEndpointAsync(existing!);
            return true;
        }
        finally {
            _mutation.Release();
        }
    }

    private async ValueTask ApplyAsync(
        string name, TcpEndpointMode mode,
        Func<Action<TcpEndpointState, string?>, CancellationToken, Task> loop, CancellationToken ct) {
        ArgumentException.ThrowIfNullOrEmpty(name);
        await _mutation.WaitAsync(ct);
        try {
            Endpoint? previous;
            lock (_endpoints) {
                _endpoints.Remove(name, out previous);
            }

            if (previous is not null) {
                // The reconnect semantic: the old endpoint (and its connections) is fully down before the
                // new settings take effect, so e.g. a re-listen on the same port never races the old socket.
                await StopEndpointAsync(previous);
            }

            ObjectDisposedException.ThrowIf(_shutdown.IsCancellationRequested, this);
            var cts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            var endpoint = new Endpoint(cts) {
                Status = new TcpEndpointStatus {
                    Name = name,
                    Mode = mode,
                    State = TcpEndpointState.Starting,
                    ChangedAt = Time.GetUtcNow(),
                },
            };
            // Visible in the dictionary before the loop runs: the loop reports its first state
            // synchronously (a listener binds immediately), and a StatusChanged subscriber calling
            // GetStatus(name) must find the endpoint.
            lock (_endpoints) {
                _endpoints[name] = endpoint;
            }

            try {
                endpoint.Run = loop((state, error) => Advertise(endpoint, state, error), cts.Token);
            }
            catch {
                // A synchronous factory throw (e.g. resolving an unregistered handler type) must not leave
                // a zombie entry stuck in Starting with a leaked CTS — the apply fails, nothing is applied.
                lock (_endpoints) {
                    _endpoints.Remove(name);
                }

                cts.Dispose();
                throw;
            }
        }
        finally {
            _mutation.Release();
        }
    }

    private void Advertise(Endpoint endpoint, TcpEndpointState state, string? error) {
        var status = endpoint.Status with { State = state, Error = error, ChangedAt = Time.GetUtcNow() };
        endpoint.Status = status;
        try {
            StatusChanged?.Invoke(status);
        }
        catch (Exception failure) {
            // A faulty subscriber must never take an endpoint loop down with it.
            Logger("Endpoints").LogWarning(failure, "A StatusChanged subscriber threw for endpoint {Name}.",
                status.Name);
        }
    }

    private static async Task StopEndpointAsync(Endpoint endpoint) {
        await endpoint.Cts.CancelAsync();
        // Loops never throw; the wait only bounds a teardown that ignores cancellation.
        try {
            await endpoint.Run.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        }
        catch (TimeoutException) {
        }

        endpoint.Cts.Dispose();
    }

    Task IHostedService.StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    async Task IHostedService.StopAsync(CancellationToken cancellationToken) {
        // Under the mutation gate: an Apply in flight either completes before the sweep (and its endpoint
        // is swept) or observes the shutdown and throws — no zombie entries after stop.
        await _mutation.WaitAsync(cancellationToken);
        Task[] runs;
        try {
            await _shutdown.CancelAsync();
            lock (_endpoints) {
                runs = [.. _endpoints.Values.Select(static e => e.Run)];
                _endpoints.Clear();
            }
        }
        finally {
            _mutation.Release();
        }

        try {
            await Task.WhenAll(runs).WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        }
        catch (TimeoutException) {
        }
    }

    private IClientConnectionRegistry Registry => services.GetRequiredService<IClientConnectionRegistry>();

    private TimeProvider Time => services.GetService<TimeProvider>() ?? TimeProvider.System;

    private ILogger Logger(string kind) =>
        services.GetService<ILoggerFactory>()?.CreateLogger(GetType().Namespace + "." + kind)
            ?? (ILogger)NullLogger.Instance;

    private sealed class Endpoint(CancellationTokenSource cts) {
        public CancellationTokenSource Cts { get; } = cts;

        public Task Run { get; set; } = Task.CompletedTask;

        // Written by the endpoint's own loop, read by any thread — a whole-record swap keeps it coherent.
        public volatile TcpEndpointStatus Status = null!;
    }
}
