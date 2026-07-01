using Elarion.Blobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Elarion.Blobs.PostgreSql;

/// <summary>
/// Periodically reclaims expired, never-committed pending blobs — the time-to-live garbage collector
/// that stands in for an "abandoned upload" signal (a browser close leaves no backend signal, so expiry
/// is the signal).
/// </summary>
/// <remarks>
/// <para>
/// Modeled on the outbox delivery worker: a <see cref="PeriodicTimer"/> drives sweeps, each on its own
/// DI scope, and a full batch keeps draining without waiting. Unlike the outbox it needs no lease: the
/// delete re-checks <c>State == Pending</c> over the partial index, so it is idempotent and
/// self-coordinating — running multiple instances, or racing a concurrent commit, only ever deletes
/// still-pending, still-expired rows.
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
        return await lifecycle
            .DeleteExpiredPendingAsync(olderThan, options.BatchSize, cancellationToken)
            .ConfigureAwait(false);
    }
}
