using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Elarion.Auditing.EntityFrameworkCore;

/// <summary>
/// Periodically deletes audit records older than the configured retention window. Registered only when the host
/// opts into retention (<see cref="AuditRetentionOptions.RetainFor"/>); the delete is a single indexed
/// <c>ExecuteDelete</c> over <c>occurred_at_utc</c>, idempotent and safe to run on every instance concurrently
/// (the redundancy trade-off documented for the idempotency purge, ADR-0021, applies unchanged).
/// </summary>
public sealed class AuditRetentionService<TDbContext>(
    IServiceScopeFactory scopeFactory,
    AuditRetentionOptions options,
    TimeProvider timeProvider,
    ILogger<AuditRetentionService<TDbContext>> logger) : BackgroundService
    where TDbContext : DbContext {
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        if (options.RetainFor is not { } retainFor) return;

        using var timer = new PeriodicTimer(options.PollingInterval, timeProvider);

        while (!stoppingToken.IsCancellationRequested) {
            try {
                await PurgeAsync(retainFor, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
                break;
            }
            catch (Exception ex) {
                // A failed cycle (e.g. a transient database error) must not stop the worker; the next tick retries.
                logger.LogError(ex, "Audit-log retention purge failed.");
            }

            if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false)) break;
        }
    }

    private async Task PurgeAsync(TimeSpan retainFor, CancellationToken ct) {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TDbContext>();
        var cutoff = timeProvider.GetUtcNow() - retainFor;
        var purged = await dbContext.Set<AuditLogEntry>()
            .Where(entry => entry.OccurredAtUtc < cutoff)
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);
        if (purged > 0) logger.LogInformation("Audit-log retention purge deleted {Count} expired record(s).", purged);
    }
}
