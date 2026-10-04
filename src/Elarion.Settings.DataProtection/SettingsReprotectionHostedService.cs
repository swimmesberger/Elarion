using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Elarion.Settings.DataProtection;

/// <summary>
/// Runs <see cref="ISettingReprotector"/> over the global scope once when the host starts, so legacy plaintext
/// secrets and payloads under a rotated key converge without operator action. Per-user secrets are not
/// enumerable without an owner; re-protect them with <see cref="ISettingReprotector"/> per scope.
/// </summary>
public sealed class SettingsReprotectionHostedService(
    IServiceScopeFactory scopeFactory,
    SettingsDataProtectionOptions options,
    ILogger<SettingsReprotectionHostedService> logger) : IHostedService {
    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken) {
        using var timeout = new CancellationTokenSource(options.ReprotectTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try {
            await using var scope = scopeFactory.CreateAsyncScope();
            var reprotector = scope.ServiceProvider.GetRequiredService<ISettingReprotector>();
            var report = await reprotector.ReprotectAsync(SettingsScope.Global, cancellationToken: linked.Token)
                .ConfigureAwait(false);

            if (report.Reprotected > 0 || report.Failed > 0)
                logger.LogInformation(
                    "Secret settings re-protected: {Reprotected} rewritten, {Skipped} skipped, {Failed} unreadable of {Scanned}.",
                    report.Reprotected, report.Skipped, report.Failed, report.Scanned);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            throw;
        }
        catch (Exception ex) {
            // Re-protection is housekeeping: a store that is not ready must not stop the host. The next start retries.
            logger.LogError(ex, "Startup re-protection of secret settings failed.");
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) {
        return Task.CompletedTask;
    }
}
