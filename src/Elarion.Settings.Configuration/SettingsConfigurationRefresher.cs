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
/// <remarks>
/// The initial load runs in <see cref="StartAsync"/>, so it completes <b>before</b> subsequently-registered
/// hosted services (the scheduler, background workers) start — settings-backed <c>${...}</c> configuration
/// placeholders resolve to their stored values rather than to stale defaults. Register this refresher
/// <i>before</i> any hosted service that reads settings-backed configuration at start (this is what
/// <c>AddElarionSettingsConfiguration</c> does). The initial load is bounded by
/// <see cref="InitialLoadTimeout"/> and honors the start cancellation token so a slow or unavailable store
/// cannot hang host startup indefinitely — on timeout or failure the provider stays empty and the
/// change-token-driven loop retries on the next signal.
/// </remarks>
public sealed class SettingsConfigurationRefresher(
    SettingsConfigurationProvider provider,
    IServiceScopeFactory scopeFactory,
    ISettingsChangeSource changeSource,
    ILogger<SettingsConfigurationRefresher> logger) : BackgroundService {
    /// <summary>The bound on the blocking initial load in <see cref="StartAsync"/>, so a slow store cannot hang startup.</summary>
    public static readonly TimeSpan InitialLoadTimeout = TimeSpan.FromSeconds(30);

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
    public override async Task StartAsync(CancellationToken cancellationToken) {
        // Subscribe before the first load so a change during startup is not missed; the callback only signals
        // the loop (it runs on the writer's thread, so it must not block).
        _subscription = ChangeToken.OnChange(
            () => changeSource.Watch(SettingsScope.Global),
            () => _signals.Writer.TryWrite(0));

        // Load synchronously (bounded) so settings-backed ${...} placeholders are populated before later hosted
        // services — notably the scheduler — start and read them.
        using var timeoutCts = new CancellationTokenSource(InitialLoadTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        try {
            await RefreshAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            throw;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested) {
            logger.LogWarning(
                "Initial settings-backed configuration load timed out after {Timeout}; starting with the empty " +
                "provider — values populate once the store responds and a change is observed.",
                InitialLoadTimeout);
        }
        catch (Exception ex) {
            logger.LogError(ex, "Failed to perform the initial settings-backed configuration load; starting empty.");
        }

        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
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
