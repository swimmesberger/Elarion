using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Elarion.Blobs.Tus.PostgreSql;

/// <summary>
/// Periodically reclaims expired, never-completed tus upload sessions — the analog of S3's
/// "abort incomplete multipart upload" lifecycle rule for the resumable transport.
/// </summary>
/// <remarks>
/// Mirrors the blob garbage collector: a <see cref="PeriodicTimer"/> drives sweeps on their own DI scope,
/// a full batch keeps draining, and the delete rides the partial index over incomplete sessions, so it is
/// idempotent and needs no lease.
/// </remarks>
public sealed class TusUploadGarbageCollector(
    IServiceScopeFactory scopeFactory,
    TusGcOptions options,
    TimeProvider timeProvider,
    ILogger<TusUploadGarbageCollector> logger) : BackgroundService {
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
                logger.LogError(ex, "tus session garbage-collection sweep failed.");
                deleted = 0;
            }

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
        var store = scope.ServiceProvider.GetRequiredService<ITusUploadStore>();
        var olderThan = timeProvider.GetUtcNow() - options.SafetyMargin;
        return await store
            .DeleteExpiredAsync(olderThan, options.BatchSize, cancellationToken)
            .ConfigureAwait(false);
    }
}
