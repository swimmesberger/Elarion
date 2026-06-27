using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace Elarion.Settings.Configuration;

/// <summary>
/// Keeps a <see cref="SettingsConfigurationProvider"/> in sync with the global settings. Because
/// configuration is built before the DI container, the provider starts empty; this hosted service performs
/// the initial load once the container exists, then reloads whenever the <see cref="ISettingsChangeSource"/>
/// signals a global change. It is store-agnostic — it reads through <see cref="ISettingsStore"/> on a fresh
/// scope, so it works with the in-process or EF Core backend alike.
/// </summary>
public sealed class SettingsConfigurationRefresher(
    SettingsConfigurationProvider provider,
    IServiceScopeFactory scopeFactory,
    ISettingsChangeSource changeSource,
    ILogger<SettingsConfigurationRefresher> logger) : BackgroundService {
    // Capacity 1 with DropWrite coalesces a burst of changes into a single pending reload.
    private readonly Channel<byte> _signals = Channel.CreateBounded<byte>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    private IDisposable? _subscription;

    /// <summary>Reloads the global settings into the provider. Exposed so a host can force a refresh.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken) {
        await using var scope = scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<ISettingsStore>();
        var entries = await store.GetAllAsync(SettingsScope.Global, cancellationToken).ConfigureAwait(false);
        provider.Apply(entries);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        // Subscribe before the first load so a change during startup is not missed; the callback only signals
        // the loop (it runs on the writer's thread, so it must not block).
        _subscription = ChangeToken.OnChange(
            () => changeSource.Watch(SettingsScope.Global),
            () => _signals.Writer.TryWrite(0));

        await RefreshSafelyAsync(stoppingToken).ConfigureAwait(false);

        try {
            await foreach (var _ in _signals.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false)) {
                await RefreshSafelyAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
            // Expected on shutdown.
        }
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken) {
        _subscription?.Dispose();
        _signals.Writer.TryComplete();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RefreshSafelyAsync(CancellationToken cancellationToken) {
        try {
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            throw;
        }
        catch (Exception ex) {
            // A transient store failure must not crash the host; the next change (or restart) retries.
            logger.LogError(ex, "Failed to refresh settings-backed configuration.");
        }
    }
}
