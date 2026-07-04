using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Elarion.Blobs;

/// <summary>
/// Periodically reclaims expired, never-committed pending blobs — the time-to-live garbage collector
/// that stands in for an "abandoned upload" signal (a browser close leaves no backend signal, so expiry
/// is the signal). Provider-neutral: it sweeps whatever <see cref="IBlobLifecycle"/> is registered.
/// </summary>
/// <remarks>
/// <para>
/// Modeled on the outbox delivery worker: a <see cref="PeriodicTimer"/> drives sweeps, each on its own
/// DI scope, and a full batch keeps draining without waiting. Unlike the outbox it needs no lease:
/// <see cref="IBlobLifecycle.DeleteExpiredPendingAsync"/> re-checks the pending state, so it is
/// idempotent and self-coordinating — running multiple instances, or racing a concurrent commit, only
/// ever deletes still-pending, still-expired blobs.
/// </para>
/// </remarks>
public sealed class BlobGarbageCollector(
    IServiceScopeFactory scopeFactory,
    BlobGcOptions options,
    TimeProvider timeProvider,
    ILogger<BlobGarbageCollector> logger) : BackgroundService {
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        using var timer = new PeriodicTimer(options.PollingInterval, timeProvider);

        while (!stoppingToken.IsCancellationRequested) {
            int deleted;
            try {
                deleted = await SweepAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
                break;
            }
            catch (Exception ex) {
                // A failed sweep (for example a transient database error) must not stop the worker; the
                // next tick retries.
                logger.LogError(ex, "Blob garbage-collection sweep failed.");
                deleted = 0;
            }

            // A full batch likely means more is expiring, so drain without waiting; otherwise idle.
            if (deleted >= options.BatchSize) {
                continue;
            }

            if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false)) {
                break;
            }
        }
    }

    private async Task<int> SweepAsync(CancellationToken cancellationToken) {
        await using var scope = scopeFactory.CreateAsyncScope();
        var lifecycle = scope.ServiceProvider.GetRequiredService<IBlobLifecycle>();
        var olderThan = timeProvider.GetUtcNow() - options.SafetyMargin;
        var deleted = await lifecycle
            .DeleteExpiredPendingAsync(olderThan, options.BatchSize, cancellationToken)
            .ConfigureAwait(false);
        if (deleted > 0) {
            logger.LogInformation("Blob garbage collection deleted {Count} expired pending blob(s).", deleted);
        }

        return deleted;
    }
}
