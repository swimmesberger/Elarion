using System.Collections.ObjectModel;
using System.Security.Claims;
using System.Threading.Channels;
using Elarion.Abstractions.Connections;
using Elarion.Connections;

namespace Elarion.Connections.Simulation;

/// <summary>
/// An in-memory <see cref="IClientConnectionSink"/> double — no socket, no adapter: register it with the
/// real registry to drive twin, observer, presence, and bridge logic deterministically. Outbound
/// <see cref="SendAsync"/> calls land on <see cref="Sent"/> (await the next one); <see cref="InvokeAsync"/>
/// answers through <see cref="InvokeResponder"/>; <see cref="Close"/> makes further sends fault with
/// <see cref="ClientConnectionClosedException"/> — the teardown behavior production code must survive.
/// </summary>
/// <remarks>
/// This works because the kernel contracts never assume a socket (synthetic connections are first-class,
/// ADR-0053) — the same property production proxy taps rely on. Codec-level tests belong on a real
/// adapter instead (loopback TCP via <see cref="TcpSimulatorClient"/>, or Kestrel + <c>ClientWebSocket</c>).
/// </remarks>
public sealed class SimulatedClientConnection : IClientConnectionSink {
    private readonly Channel<SentMessage> _sent = Channel.CreateUnbounded<SentMessage>();
    private int _closed;

    /// <summary>Creates the double; every argument is optional with test-friendly defaults.</summary>
    /// <param name="principalId">The registry index key (device id / user id).</param>
    /// <param name="transport">The transport tag (default <c>"test"</c>).</param>
    /// <param name="connectionId">Explicit id (e.g. to provoke duplicate registration); default a fresh v7.</param>
    /// <param name="principal">The connect-time principal; default an authenticated test identity.</param>
    /// <param name="metadata">Adapter-style metadata.</param>
    public SimulatedClientConnection(
        string? principalId = null,
        string transport = "test",
        string? connectionId = null,
        ClaimsPrincipal? principal = null,
        IReadOnlyDictionary<string, string>? metadata = null) {
        Connection = new ClientConnection {
            ConnectionId = connectionId ?? Guid.CreateVersion7().ToString("N"),
            Transport = transport,
            Principal = principal ?? new ClaimsPrincipal(new ClaimsIdentity(authenticationType: "test")),
            PrincipalId = principalId,
            ConnectedAt = DateTimeOffset.UtcNow,
            Metadata = metadata ?? ReadOnlyDictionary<string, string>.Empty,
        };
    }

    /// <inheritdoc />
    public ClientConnection Connection { get; }

    /// <summary>Everything sent through the neutral sink, in order; completes when <see cref="Close"/>d.</summary>
    public ChannelReader<SentMessage> Sent => _sent.Reader;

    /// <summary>
    /// Answers <see cref="InvokeAsync"/>: receives (name, request), returns the response object (it must be
    /// assignable to the invocation's <c>TResponse</c>). Unset invocations fail loud with
    /// <see cref="NotSupportedException"/>, mirroring the codec seam's defaults.
    /// </summary>
    public Func<string, object, ValueTask<object>>? InvokeResponder { get; set; }

    /// <summary>
    /// The invoke timeout the double applies when a call carries no per-call
    /// <see cref="ClientInvokeOptions.Timeout"/> — the same layering real adapters get from
    /// <c>ElarionConnectionsOptions.DefaultInvokeTimeout</c>, initialized to the same shipped value so a
    /// responder that never answers faults with <see cref="TimeoutException"/> instead of hanging the
    /// test. Set <see langword="null"/> for unbounded.
    /// </summary>
    public TimeSpan? DefaultInvokeTimeout { get; set; } = new ElarionConnectionsOptions().DefaultInvokeTimeout;

    /// <summary>Whether <see cref="Close"/> was called.</summary>
    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    /// <summary>Simulates the connection ending: further sends/invokes fault, <see cref="Sent"/> completes.
    /// Unregistering from the registry (so observers fire) stays the test's explicit step.</summary>
    public void Close() {
        if (Interlocked.Exchange(ref _closed, 1) == 0) {
            _sent.Writer.TryComplete();
        }
    }

    /// <inheritdoc />
    public ValueTask SendAsync<TPayload>(string name, TPayload payload, CancellationToken ct = default)
        where TPayload : class {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(payload);
        ThrowIfClosed();
        _sent.Writer.TryWrite(new SentMessage(name, payload));
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask<TResponse> InvokeAsync<TRequest, TResponse>(
        string name, TRequest request, ClientInvokeOptions? options = null, CancellationToken ct = default)
        where TRequest : class {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfClosed();
        if (InvokeResponder is not { } responder) {
            throw new NotSupportedException(
                "Set SimulatedClientConnection.InvokeResponder to answer InvokeAsync in this test.");
        }

        // The same timeout layering real adapters apply: per-call wins, else the default bounds the wait,
        // else the caller's token is the only bound.
        var timeout = options?.Timeout ?? DefaultInvokeTimeout;
        var reply = responder(name, request).AsTask();
        var response = timeout is { } window
            ? await reply.WaitAsync(window, ct)
            : await reply.WaitAsync(ct);
        return (TResponse)response;
    }

    private void ThrowIfClosed() {
        if (IsClosed) {
            throw new ClientConnectionClosedException(Connection.ConnectionId);
        }
    }

    /// <summary>One captured outbound send.</summary>
    public readonly record struct SentMessage(string Name, object Payload);
}
