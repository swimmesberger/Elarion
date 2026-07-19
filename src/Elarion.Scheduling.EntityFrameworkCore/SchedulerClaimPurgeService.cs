using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Elarion.Scheduling.EntityFrameworkCore;

/// <summary>
/// Deletes scheduler claim rows older than <see cref="SchedulerClaimsOptions.ClaimRetention"/> in bounded
/// batches, so the claims table stays an indexed probe regardless of how many occurrences have fired.
/// Lease-free: the set-based delete is idempotent, so concurrent purges on several nodes are harmless.
/// </summary>
internal sealed class SchedulerClaimPurgeService<TDbContext>(
    IServiceScopeFactory scopeFactory,
    SchedulerClaimsOptions options,
    TimeProvider timeProvider,
    ILogger<SchedulerClaimPurgeService<TDbContext>> logger) : BackgroundService
    where TDbContext : DbContext {
    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        using var timer = new PeriodicTimer(options.PurgeInterval, timeProvider);

        while (!stoppingToken.IsCancellationRequested) {
            try {
                await PurgeAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
                return;
            }
            catch (Exception exception) {
                logger.LogError(exception, "Scheduler claim retention purge failed.");
            }

            try {
                if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false)) return;
            }
            catch (OperationCanceledException) {
                return;
            }
        }
    }

    private async Task PurgeAsync(CancellationToken cancellationToken) {
        var cutoff = timeProvider.GetUtcNow() - options.ClaimRetention;

        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TDbContext>();

        var deleted = await dbContext.Set<SchedulerClaimEntity>()
            .Where(claim => claim.OccurrenceUtc < cutoff)
            .OrderBy(claim => claim.OccurrenceUtc)
            .Take(options.PurgeBatchSize)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

        if (deleted > 0) logger.LogInformation("Purged {Count} scheduler claim row(s) past retention.", deleted);
    }
}
