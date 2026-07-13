using Elarion.Abstractions.ClientEvents;
using Elarion.Abstractions.Connections;
using Elarion.ClientEvents;
using Microsoft.Extensions.Logging;

namespace Elarion.Connections;

/// <summary>
/// One live client-event subscription over a connection: owns the registry handle and the pump that moves
/// matched envelopes into the adapter's <see cref="ClientEventDelivery"/>. Delivery starts with the
/// <c>elarion.connected</c> greeting — the same re-query contract an SSE stream opens with — so a client
/// converges regardless of which transport carried its subscription. Dispose to unsubscribe; the bridge
/// also disposes it automatically when its connection unregisters.
/// </summary>
public sealed class ClientConnectionEventSubscription : IDisposable {
    private readonly ClientEventSubscriptionHandle _handle;
    private readonly ClientEventDelivery _deliver;
    private readonly Action<ClientConnectionEventSubscription> _onFinished;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private Task _pump = Task.CompletedTask;
    private int _disposed;

    internal ClientConnectionEventSubscription(
        ClientEventSubscriptionHandle handle,
        ClientEventDelivery deliver,
        Action<ClientConnectionEventSubscription> onFinished,
        ILogger logger) {
        _handle = handle;
        _deliver = deliver;
        _onFinished = onFinished;
        _logger = logger;
    }

    internal void Start() {
        if (Volatile.Read(ref _disposed) == 0) {
            _pump = PumpAsync();
        }
    }

    /// <summary>Completes when the pump has ended (subscription disposed, connection closed, or delivery
    /// failed). Exposed for deterministic teardown in adapters and tests.</summary>
    public Task Completion => _pump;

    private async Task PumpAsync() {
        var ct = _cts.Token;
        try {
            // The greeting: every subscribe converges the client via re-query, exactly like an SSE open.
            await _deliver(ConnectedEnvelope(), ct);
            while (await _handle.Events.WaitToReadAsync(ct)) {
                while (_handle.Events.TryRead(out var envelope)) {
                    await _deliver(envelope, ct);
                }
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) {
            // Disposed (explicit unsubscribe or connection teardown) — the normal end.
        }
        catch (ClientConnectionClosedException) {
            // The connection died under the pump — the other normal end.
        }
        catch (Exception failure) {
            _logger.LogWarning(failure, "Client-event delivery to a connection failed; the subscription ends.");
        }
        finally {
            Dispose();
        }
    }

    /// <inheritdoc />
    public void Dispose() {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) {
            return;
        }

        _cts.Cancel();
        _handle.Dispose();
        _onFinished(this);
    }

    private static ClientEventEnvelope ConnectedEnvelope() => new() {
        Id = Guid.CreateVersion7(),
        Topic = ClientEventControlEvents.Connected,
        Scope = ClientEventScope.Global,
        Payload = string.Empty,
    };
}
