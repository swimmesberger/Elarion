using Elarion.Abstractions.Idempotency;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Elarion.Idempotency.EntityFrameworkCore;

/// <summary>
/// Periodically deletes expired (past-retention) completed idempotency records. Records are self-expiring — each
/// carries its handler's retention window — so the purge is a simple indexed delete of everything past due.
/// Mirrors the outbox delivery/retention worker.
/// </summary>
public sealed class IdempotencyKeyPurgeService(
    IServiceScopeFactory scopeFactory,
    IdempotencyPurgeOptions options,
    TimeProvider timeProvider,
    ILogger<IdempotencyKeyPurgeService> logger) : BackgroundService {
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        using var timer = new PeriodicTimer(options.PollingInterval, timeProvider);

        while (!stoppingToken.IsCancellationRequested) {
            try {
                await PurgeAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
                break;
            }
            catch (Exception ex) {
                // A failed cycle (e.g. a transient database error) must not stop the worker; the next tick retries.
                logger.LogError(ex, "Idempotency-key retention purge failed.");
            }

            if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false)) {
                break;
            }
        }
    }

    private async Task PurgeAsync(CancellationToken ct) {
        await using var scope = scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IIdempotencyStore>();
        await store.PurgeCompletedAsync(timeProvider.GetUtcNow(), ct).ConfigureAwait(false);
    }
}
