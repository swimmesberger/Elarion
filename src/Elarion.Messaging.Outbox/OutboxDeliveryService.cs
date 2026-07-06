using System.Diagnostics;
using Elarion.Abstractions.Messaging;
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

        var lockId = Guid.CreateVersion7();
        var leaseUntil = timeProvider.GetUtcNow() + options.LeaseDuration;
        var claimed = await store.ClaimPendingAsync(lockId, leaseUntil, options.BatchSize, ct).ConfigureAwait(false);

        foreach (var message in claimed)
        {
            ct.ThrowIfCancellationRequested();
            await DeliverAsync(store, lockId, message, ct).ConfigureAwait(false);
        }

        return claimed.Count;
    }

    private async Task DeliverAsync(IOutboxStore store, Guid lockId, OutboxMessage message, CancellationToken ct)
    {
        // Parent the consume span on the traceparent persisted at publish time, so delivery stays in the
        // publishing operation's trace even on another worker instance or after a restart.
        ActivityContext.TryParse(message.TraceParent, null, isRemote: true, out var traceParent);
        using var activity = EventTelemetry.Source.StartActivity(
            $"consume {message.EventType}", ActivityKind.Internal, traceParent);
        if (activity is not null)
        {
            activity.SetTag("messaging.event.type", message.EventType);
            activity.SetTag("messaging.event.plane", "integration");
            activity.SetTag("messaging.correlation_id", message.CorrelationId);
            activity.SetTag("messaging.outbox.attempt", message.Attempts + 1);
        }

        var startTimestamp = Stopwatch.GetTimestamp();
        try
        {
            OutboxDispatchOutcome outcome;
            await using (var consumerScope = scopeFactory.CreateAsyncScope())
            {
                outcome = await dispatcher.DispatchAsync(consumerScope.ServiceProvider, message, ct).ConfigureAwait(false);
            }

            if (outcome is OutboxDispatchOutcome.Unresolvable)
            {
                // The event type resolves to no consumer or the payload is null — a retry can never succeed, so park
                // the message for inspection instead of redelivering it every poll (the dispatcher already logged why).
                var parked = await store.MarkPermanentlyFailedAsync(
                    message.Id,
                    lockId,
                    "Event type unresolvable or payload null; parked for inspection.",
                    ct).ConfigureAwait(false);
                if (!parked)
                {
                    LogLeaseLost(message);
                }

                EventTelemetry.RecordDelivery(
                    message.EventType, "unresolvable", Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds);
                return;
            }

            if (!await store.MarkProcessedAsync(message.Id, lockId, timeProvider.GetUtcNow(), ct).ConfigureAwait(false))
            {
                LogLeaseLost(message);
            }

            EventTelemetry.RecordDelivery(
                message.EventType, "delivered", Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown mid-delivery: leave the lease to expire so another worker reclaims the message.
            throw;
        }
        catch (Exception ex)
        {
            activity?.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection {
                { "exception.type", ex.GetType().FullName },
                { "exception.message", ex.Message },
            }));
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            EventTelemetry.RecordDelivery(
                message.EventType, "failed", Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds);
            logger.LogError(
                ex,
                "Delivery of outbox message {MessageId} ({EventType}, correlation {CorrelationId}) failed on attempt {Attempt}.",
                message.Id,
                message.EventType,
                message.CorrelationId,
                message.Attempts + 1);
            var retryVisibleAfter = timeProvider.GetUtcNow() + ComputeBackoff(message.Attempts + 1);
            if (!await store.MarkFailedAsync(message.Id, lockId, Describe(ex), retryVisibleAfter, ct).ConfigureAwait(false))
            {
                LogLeaseLost(message);
            }
        }
    }

    private void LogLeaseLost(OutboxMessage message) =>
        // Zero rows updated means our lease expired and another worker legitimately reclaimed the message while we
        // were dispatching. Finalizing anyway would wipe the new owner's active lease and cause overlapping
        // redelivery, so we skip and let the current owner finalize it.
        logger.LogWarning(
            "Outbox message {MessageId} ({EventType}) lease was lost before finalizing; another worker reclaimed it. Skipping finalize.",
            message.Id,
            message.EventType);

    /// <summary>Exponential backoff for the next attempt: <c>BaseRetryDelay × 2^(attempt-1)</c>, capped at <see cref="OutboxOptions.MaxRetryDelay"/>.</summary>
    private TimeSpan ComputeBackoff(int attempt)
    {
        if (options.BaseRetryDelay <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        // Shift on ticks with a guarded exponent so a large attempt count can never overflow into a negative delay.
        var exponent = Math.Min(attempt - 1, 30);
        var scaled = options.BaseRetryDelay.Ticks * (1L << exponent);
        var capTicks = options.MaxRetryDelay.Ticks;
        if (scaled <= 0 || scaled > capTicks)
        {
            return options.MaxRetryDelay;
        }

        return TimeSpan.FromTicks(scaled);
    }

    private async Task PurgeAsync(CancellationToken ct)
    {
        if (options.RetentionPeriod is not { } retention)
        {
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
        var purged = await store.PurgeProcessedAsync(timeProvider.GetUtcNow() - retention, ct).ConfigureAwait(false);
        if (purged > 0)
        {
            logger.LogInformation("Outbox retention purge deleted {Count} processed message(s).", purged);
        }
    }

    private static string Describe(Exception ex)
    {
        var text = $"{ex.GetType().FullName}: {ex.Message}";
        return text.Length <= 2048 ? text : text[..2048];
    }
}
