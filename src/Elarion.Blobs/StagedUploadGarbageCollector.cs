using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Elarion.Blobs;

/// <summary>
/// Periodically reclaims expired staged-upload sessions — the analog of S3's "abort incomplete
/// multipart upload" lifecycle rule for resumable upload transports. Provider-neutral: it sweeps
/// whatever <see cref="IStagedUploadStore"/> is registered.
/// </summary>
/// <remarks>
/// Mirrors the blob garbage collector: a <see cref="PeriodicTimer"/> drives sweeps on their own DI
/// scope, a full batch keeps draining, and the delete is idempotent, so multiple instances coexist.
/// </remarks>
public sealed class StagedUploadGarbageCollector(
    IServiceScopeFactory scopeFactory,
    StagedUploadGcOptions options,
    TimeProvider timeProvider,
    ILogger<StagedUploadGarbageCollector> logger) : BackgroundService {
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
                logger.LogError(ex, "Staged-upload garbage-collection sweep failed.");
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
        var store = scope.ServiceProvider.GetRequiredService<IStagedUploadStore>();
        var olderThan = timeProvider.GetUtcNow() - options.SafetyMargin;
        var deleted = await store
            .DeleteExpiredAsync(olderThan, options.BatchSize, cancellationToken)
            .ConfigureAwait(false);
        if (deleted > 0) {
            logger.LogInformation("Staged-upload garbage collection deleted {Count} expired session(s).", deleted);
        }

        return deleted;
    }
}
