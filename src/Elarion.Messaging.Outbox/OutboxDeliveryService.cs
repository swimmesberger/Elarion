using System.Diagnostics;
using Elarion.Abstractions.Coordination;
using Elarion.Abstractions.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Elarion.Messaging.Outbox;

/// <summary>
/// Polls the outbox, claims eligible per-consumer deliveries, dispatches them on isolated scopes, and
/// finalizes each — the durable, after-commit delivery tier.
/// </summary>
/// <remarks>
/// <para>
/// Delivery is at-least-once: a message redelivers if the worker crashes after dispatch but before finalizing, or if
/// a consumer throws (only that delivery retries until <see cref="OutboxOptions.MaxDeliveryAttempts"/>). Consumers
/// must still be idempotent across crash windows. A claim lease lets a crashed worker's deliveries be reclaimed,
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

        // Retention purging is a maintenance sweep, not per-poll work: without a cadence guard every idle poll
        // (default 1 s) would issue the purge DELETE on every node. Track the last purge and re-run only once
        // per PurgeInterval; a failed purge waits for the next interval rather than hammering every tick.
        var lastPurge = timeProvider.GetUtcNow();

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

            var now = timeProvider.GetUtcNow();
            if (now - lastPurge >= options.PurgeInterval)
            {
                lastPurge = now;
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
        var roleLeases = pollScope.ServiceProvider.GetService<IRoleLeaseRegistry>()?.Leases
            .ToDictionary(static lease => lease.Role, StringComparer.Ordinal);
        var heldRoles = roleLeases?.Values
            .Where(static lease => lease.IsHeld)
            .Select(static lease => lease.Role)
            .ToArray() ?? [];

        var lockId = Guid.CreateVersion7();
        var leaseUntil = timeProvider.GetUtcNow() + options.LeaseDuration;
        var claimed = await store.ClaimPendingAsync(
            lockId,
            leaseUntil,
            options.BatchSize,
            heldRoles,
            ct).ConfigureAwait(false);

        foreach (var delivery in claimed)
        {
            ct.ThrowIfCancellationRequested();
            if (delivery.TargetRole is { } targetRole
                && (roleLeases is null
                    || !roleLeases.TryGetValue(targetRole, out var targetLease)
                    || !targetLease.IsHeld)) {
                if (!await store.ReleaseClaimAsync(delivery.Id, lockId, ct).ConfigureAwait(false)) {
                    LogLeaseLost(delivery);
                }
                else {
                    logger.LogInformation(
                        "Released outbox delivery {DeliveryId} for role '{Role}' because this process no longer holds the role.",
                        delivery.Id,
                        targetRole);
                }

                continue;
            }

            await DeliverAsync(store, lockId, delivery, ct).ConfigureAwait(false);
        }

        return claimed.Count;
    }

    private async Task DeliverAsync(IOutboxStore store, Guid lockId, OutboxDelivery delivery, CancellationToken ct)
    {
        var message = delivery.Message;
        // Parent the consume span on the traceparent persisted at publish time, so delivery stays in the
        // publishing operation's trace even on another worker instance or after a restart.
        ActivityContext.TryParse(message.TraceParent, null, isRemote: true, out var traceParent);
        using var activity = EventTelemetry.Source.HasListeners()
            ? EventTelemetry.Source.StartActivity($"consume {message.EventType}", ActivityKind.Internal, traceParent)
            : null;
        if (activity is not null)
        {
            activity.SetTag("messaging.event.type", message.EventType);
            activity.SetTag("messaging.event.plane", "integration");
            activity.SetTag("messaging.correlation_id", message.CorrelationId);
            activity.SetTag("messaging.outbox.consumer_id", delivery.ConsumerId);
            activity.SetTag("messaging.outbox.target_role", delivery.TargetRole);
            activity.SetTag("messaging.outbox.attempt", delivery.Attempts + 1);
        }

        var startTimestamp = Stopwatch.GetTimestamp();
        try
        {
            OutboxDispatchOutcome outcome;
            await using (var consumerScope = scopeFactory.CreateAsyncScope())
            {
                outcome = await dispatcher.DispatchAsync(consumerScope.ServiceProvider, delivery, ct).ConfigureAwait(false);
            }

            if (outcome is OutboxDispatchOutcome.Unresolvable)
            {
                // The event type resolves to no consumer or the payload is null — a retry can never succeed, so park
                // the message for inspection instead of redelivering it every poll (the dispatcher already logged why).
                var parked = await store.MarkPermanentlyFailedAsync(
                    delivery.Id,
                    lockId,
                    "Event type unresolvable or payload null; parked for inspection.",
                    ct).ConfigureAwait(false);
                if (!parked)
                {
                    LogLeaseLost(delivery);
                }

                EventTelemetry.RecordDelivery(
                    message.EventType, "unresolvable", Stopwatch.GetElapsedTime(startTimestamp));
                return;
            }

            if (!await store.MarkProcessedAsync(delivery.Id, lockId, timeProvider.GetUtcNow(), ct).ConfigureAwait(false))
            {
                LogLeaseLost(delivery);
            }

            EventTelemetry.RecordDelivery(
                message.EventType, "delivered", Stopwatch.GetElapsedTime(startTimestamp));
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
                message.EventType, "failed", Stopwatch.GetElapsedTime(startTimestamp));
            logger.LogError(
                ex,
                "Delivery {DeliveryId} of outbox message {MessageId} to {ConsumerId} ({EventType}, correlation {CorrelationId}) failed on attempt {Attempt}.",
                delivery.Id,
                message.Id,
                delivery.ConsumerId,
                message.EventType,
                message.CorrelationId,
                delivery.Attempts + 1);
            var retryVisibleAfter = timeProvider.GetUtcNow() + ComputeBackoff(delivery.Attempts + 1);
            if (!await store.MarkFailedAsync(delivery.Id, lockId, Describe(ex), retryVisibleAfter, ct).ConfigureAwait(false))
            {
                LogLeaseLost(delivery);
            }
        }
    }

    private void LogLeaseLost(OutboxDelivery delivery) =>
        // Zero rows updated means our lease expired and another worker legitimately reclaimed the message while we
        // were dispatching. Finalizing anyway would wipe the new owner's active lease and cause overlapping
        // redelivery, so we skip and let the current owner finalize it.
        logger.LogWarning(
            "Outbox delivery {DeliveryId} for message {MessageId} ({EventType}) lease was lost before finalizing; another worker reclaimed it. Skipping finalize.",
            delivery.Id,
            delivery.MessageId,
            delivery.Message.EventType);

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
