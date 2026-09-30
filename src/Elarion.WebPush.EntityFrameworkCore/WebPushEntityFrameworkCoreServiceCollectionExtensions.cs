using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Elarion.WebPush.EntityFrameworkCore;

/// <summary>Registers the EF-backed Web Push stores (ADR-0076).</summary>
public static class WebPushEntityFrameworkCoreServiceCollectionExtensions {
    /// <summary>
    /// Registers Web Push (<c>AddElarionWebPush</c>) with durable <see cref="IPushSubscriptionStore"/> and
    /// <see cref="IVapidKeyStore"/> over <typeparamref name="TDbContext"/>, replacing the in-memory defaults.
    /// The context must map the Web Push tables — annotate it with <c>[GenerateElarionWebPush]</c> or call
    /// <c>modelBuilder.UseElarionWebPush()</c>.
    /// </summary>
    /// <remarks>
    /// The subscription store is scoped and works on the caller's <typeparamref name="TDbContext"/>, so it joins
    /// an active unit of work. The VAPID key pair is resolved once when the host starts, outside any caller's
    /// transaction (it has to be stored for good before it is used); a start-up failure — the tables not
    /// created yet — is logged and the pair is resolved on first use instead.
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Configures the options; <see cref="WebPushOptions.Subject"/> is required.</param>
    public static IServiceCollection AddElarionWebPushEntityFrameworkCore<TDbContext>(
        this IServiceCollection services,
        Action<WebPushOptions>? configure = null)
        where TDbContext : DbContext {
        ArgumentNullException.ThrowIfNull(services);
        services.AddElarionWebPush(configure);
        services.RemoveAll<IPushSubscriptionStore>();
        services.AddScoped<IPushSubscriptionStore, EfCorePushSubscriptionStore<TDbContext>>();
        services.RemoveAll<IVapidKeyStore>();
        services.AddSingleton<IVapidKeyStore, EfCoreVapidKeyStore<TDbContext>>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, VapidKeyWarmUp>());
        return services;
    }
}

/// <summary>
/// Resolves the VAPID key pair at host start, so its first-use generation and insert never run inside a
/// request's unit of work — where, on SQLite, the insert on the key store's own connection would wait for the
/// request's write lock.
/// </summary>
internal sealed class VapidKeyWarmUp(IVapidKeyProvider keys, ILogger<VapidKeyWarmUp> logger) : IHostedService {
    public async Task StartAsync(CancellationToken cancellationToken) {
        try {
            await keys.GetAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested) {
            // Not fatal: the provider retries on first use. Typically the schema is created after the host starts.
            logger.LogWarning(ex, "Could not resolve the Web Push VAPID key pair at start; it is resolved on first use.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) {
        return Task.CompletedTask;
    }
}
