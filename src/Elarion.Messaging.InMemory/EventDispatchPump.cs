using System.Threading.Channels;
using Elarion.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Elarion.Messaging.InMemory;

/// <summary>
/// Drains flushed integration events and delivers each to its registered consumers on a fresh DI
/// scope, isolated from the originating command.
/// </summary>
/// <remarks>
/// This is the in-memory delivery tier. It is best-effort: events flushed but not yet delivered
/// when the process exits are lost. A consumer failure is logged and isolated so it neither fails
/// the originating command nor blocks other consumers; the in-memory tier does not retry. Use the
/// EF Core transactional outbox for at-least-once guarantees.
/// </remarks>
internal sealed class EventDispatchPump : BackgroundService {
    private readonly EventSubscriptionRegistry _registry;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly EventBusOptions _options;
    private readonly ILogger<EventDispatchPump> _logger;
    private readonly Channel<EventEnvelope> _channel;

    public EventDispatchPump(
        EventSubscriptionRegistry registry,
        IServiceScopeFactory scopeFactory,
        EventBusOptions options,
        ILogger<EventDispatchPump> logger) {
        _registry = registry;
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
        _channel = Channel.CreateBounded<EventEnvelope>(
            new BoundedChannelOptions(Math.Max(1, options.DeliveryChannelCapacity)) {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });
    }

    public ValueTask EnqueueAsync(EventEnvelope envelope, CancellationToken ct) {
        if (!_options.Enabled) {
            return ValueTask.CompletedTask;
        }

        return _channel.Writer.WriteAsync(envelope, ct);
    }

    /// <summary>
    /// Enqueues an envelope from a <b>synchronous</b> commit/save interceptor path, where the channel is bounded
    /// with <see cref="BoundedChannelFullMode.Wait"/> and an <c>await</c> is not available.
    /// </summary>
    /// <remarks>
    /// Blocking a synchronous <c>SaveChanges</c>/commit thread on <c>WriteAsync(...).GetResult()</c> when the
    /// channel is full stalls the caller indefinitely with no cancellation. The deliberate policy here is
    /// <b>try-then-drop-with-Error-log</b>: attempt a non-blocking <see cref="ChannelWriter{T}.TryWrite"/> and, if
    /// the channel is momentarily full, drop the event and log an Error naming it rather than deadlocking the
    /// commit thread. The in-memory tier is explicitly best-effort (undelivered-on-crash by design), so a rare
    /// drop under sustained back-pressure is preferable to blocking; hosts needing at-least-once use the outbox.
    /// The async interceptor path (<see cref="EnqueueAsync"/>) still applies back-pressure via <c>WriteAsync</c>.
    /// </remarks>
    public void EnqueueSynchronously(EventEnvelope envelope) {
        if (!_options.Enabled) {
            return;
        }

        if (_channel.Writer.TryWrite(envelope)) {
            return;
        }

        _logger.LogError(
            "Dropped integration event '{Event}' from a synchronous commit: the delivery channel was full "
            + "(capacity {Capacity}). Increase EventBus:DeliveryChannelCapacity, publish from an async "
            + "SaveChangesAsync/commit path, or use the transactional outbox for durable delivery.",
            envelope.EventType,
            _options.DeliveryChannelCapacity);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        if (!_options.Enabled) {
            return;
        }

        try {
            await foreach (var envelope in _channel.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false)) {
                await ProcessAsync(envelope, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
            // Expected on shutdown; drain whatever was already flushed below.
        }

        await DrainAsync().ConfigureAwait(false);
    }

    private async ValueTask DrainAsync() {
        // Deliberate CancellationToken.None: a graceful shutdown drains events already flushed
        // after a successful commit. The in-memory tier makes no durability promise on crash.
        while (_channel.Reader.TryRead(out var envelope)) {
            await ProcessAsync(envelope, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Processes one flushed envelope, guarding the <b>whole</b> operation — scope creation and registry lookup
    /// included — so a failure there (a broken scope factory, a registry that throws) logs and the loop continues,
    /// rather than escaping <c>ExecuteAsync</c> and stopping delivery for the process lifetime. Only a genuine stop
    /// (an <see cref="OperationCanceledException"/> tied to the stopping token) is rethrown to exit the loop.
    /// </summary>
    private async ValueTask ProcessAsync(EventEnvelope envelope, CancellationToken ct) {
        try {
            await DispatchAsync(envelope, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        }
        catch (Exception ex) {
            _logger.LogError(
                ex,
                "Failed to dispatch integration event '{Event}' to its consumers; skipping it and continuing.",
                envelope.EventType);
        }
    }

    private async ValueTask DispatchAsync(EventEnvelope envelope, CancellationToken ct) {
        var subscribers = _registry.GetIntegrationSubscribers(envelope.EventType);
        if (subscribers.Length == 0) {
            return;
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        foreach (var descriptor in subscribers) {
            try {
                await descriptor.InvokeAsync!(scope.ServiceProvider, envelope.Event, envelope.Context, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                throw;
            }
            catch (Exception ex) {
                _logger.LogError(
                    ex,
                    "Integration event consumer '{Consumer}' failed for event '{Event}'.",
                    descriptor.ServiceType,
                    envelope.EventType);
            }
        }
    }
}
