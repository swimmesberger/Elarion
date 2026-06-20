using System.Threading.Channels;
using Elarion.Abstractions.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Elarion.Messaging;

/// <summary>
/// Drains flushed integration events and delivers each to its registered consumers on a fresh DI
/// scope, isolated from the originating command.
/// </summary>
/// <remarks>
/// This is the in-memory delivery tier. It is best-effort: events flushed but not yet delivered
/// when the process exits are lost. A consumer failure is logged and isolated so it neither fails
/// the originating command nor blocks other consumers; the in-memory tier does not retry. Use a
/// durable delivery tier for at-least-once guarantees.
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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        if (!_options.Enabled) {
            return;
        }

        try {
            await foreach (var envelope in _channel.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false)) {
                await DispatchAsync(envelope, stoppingToken).ConfigureAwait(false);
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
            await DispatchAsync(envelope, CancellationToken.None).ConfigureAwait(false);
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
