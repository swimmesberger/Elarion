using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Elarion.Messaging.Outbox;

/// <summary>
/// Polls the outbox, claims pending messages, dispatches them to their integration consumers on isolated scopes, and
/// finalizes each — the durable, after-commit delivery tier.
/// </summary>
/// <remarks>
/// <para>
/// Delivery is at-least-once: a message redelivers if the worker crashes after dispatch but before finalizing, or if
/// any consumer throws (the whole message is retried until <see cref="OutboxOptions.MaxDeliveryAttempts"/>). Consumers
/// must therefore be idempotent. A claim lease lets a crashed worker's messages be reclaimed once the lease expires,
/// and the conditional claim makes running multiple instances safe.
/// </para>
/// </remarks>
public sealed class OutboxDeliveryService(
    IServiceScopeFactory scopeFactory,
    OutboxEventDispatcher dispatcher,
    OutboxOptions options,
    TimeProvider timeProvider,
    ILogger<OutboxDeliveryService> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.PollingInterval, timeProvider);

        while (!stoppingToken.IsCancellationRequested)
        {
            int delivered;
            try
            {
                delivered = await ProcessBatchAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A failed cycle (e.g. a transient database error) must not stop the worker; the next tick retries.
                logger.LogError(ex, "Outbox delivery cycle failed.");
                delivered = 0;
            }

            // A full batch likely means more work is waiting, so drain without waiting; otherwise idle until the next tick.
            if (delivered >= options.BatchSize)
            {
                continue;
            }

            try
            {
                await PurgeAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Outbox retention purge failed.");
            }

            if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                break;
            }
        }
    }

    private async Task<int> ProcessBatchAsync(CancellationToken ct)
    {
        await using var pollScope = scopeFactory.CreateAsyncScope();
        var store = pollScope.ServiceProvider.GetRequiredService<IOutboxStore>();

        var lockId = Guid.NewGuid();
        var leaseUntil = timeProvider.GetUtcNow() + options.LeaseDuration;
        var claimed = await store.ClaimPendingAsync(lockId, leaseUntil, options.BatchSize, ct).ConfigureAwait(false);

        foreach (var message in claimed)
        {
            ct.ThrowIfCancellationRequested();
            await DeliverAsync(store, message, ct).ConfigureAwait(false);
        }

        return claimed.Count;
    }

    private async Task DeliverAsync(IOutboxStore store, OutboxMessage message, CancellationToken ct)
    {
        try
        {
            await using var consumerScope = scopeFactory.CreateAsyncScope();
            await dispatcher.DispatchAsync(consumerScope.ServiceProvider, message, ct).ConfigureAwait(false);
            await store.MarkProcessedAsync(message.Id, timeProvider.GetUtcNow(), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown mid-delivery: leave the lease to expire so another worker reclaims the message.
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Delivery of outbox message {MessageId} ({EventType}) failed on attempt {Attempt}.",
                message.Id,
                message.EventType,
                message.Attempts + 1);
            await store.MarkFailedAsync(message.Id, Describe(ex), ct).ConfigureAwait(false);
        }
    }

    private async Task PurgeAsync(CancellationToken ct)
    {
        if (options.RetentionPeriod is not { } retention)
        {
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
        await store.PurgeProcessedAsync(timeProvider.GetUtcNow() - retention, ct).ConfigureAwait(false);
    }

    private static string Describe(Exception ex)
    {
        var text = $"{ex.GetType().FullName}: {ex.Message}";
        return text.Length <= 2048 ? text : text[..2048];
    }
}
