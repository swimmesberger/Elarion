using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Elarion.Coordination.PostgreSql;

/// <summary>
/// Renews (or keeps attempting to acquire) one role lease on
/// <see cref="RoleLeaseOptions.RenewInterval"/>. A failed renewal round is logged and retried on the
/// next tick — <see cref="PostgreSqlRoleLease{TDbContext}.IsHeld"/> decays on its own through the
/// safety margin, so a database outage degrades to "nobody holds the role" (fail-closed), never to
/// two holders. On shutdown the lease is released so failover is immediate.
/// </summary>
internal sealed class RoleLeaseHeartbeatService<TDbContext>(
    PostgreSqlRoleLease<TDbContext> lease,
    RoleLeaseOptions options,
    TimeProvider timeProvider,
    ILogger<RoleLeaseHeartbeatService<TDbContext>> logger) : BackgroundService
    where TDbContext : DbContext {
    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        using var timer = new PeriodicTimer(options.RenewInterval, timeProvider);
        while (!stoppingToken.IsCancellationRequested) {
            try {
                await lease.TryAcquireOrRenewAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
                break;
            }
            catch (Exception ex) {
                logger.LogError(
                    ex, "Role lease '{Role}' renewal failed; retrying on the next tick.", options.RoleName);
            }

            if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false)) {
                break;
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken) {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        try {
            await lease.ReleaseAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) {
            // Best effort: an unreleased lease just means failover waits for expiry.
            logger.LogWarning(ex, "Releasing the role lease '{Role}' on shutdown failed.", options.RoleName);
        }
    }
}
