using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Elarion.Migrations;

/// <summary>
/// Runs <see cref="IMigrationRunner.MigrateAsync"/> during host startup, before the host reports ready.
/// A migration failure fails startup — serving traffic against a half-migrated schema is worse than not
/// starting, and the runner's explicit failure states name the recovery.
/// </summary>
internal sealed class MigrationHostedService(
    IMigrationRunner runner,
    ILogger<MigrationHostedService> logger) : IHostedService {
    public async Task StartAsync(CancellationToken cancellationToken) {
        logger.LogInformation("Applying pending database migrations…");
        await runner.MigrateAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) {
        return Task.CompletedTask;
    }
}
