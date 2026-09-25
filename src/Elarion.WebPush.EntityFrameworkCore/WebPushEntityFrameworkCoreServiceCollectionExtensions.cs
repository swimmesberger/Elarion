using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Elarion.WebPush.EntityFrameworkCore;

/// <summary>Registers the EF-backed Web Push stores (ADR-0076).</summary>
public static class WebPushEntityFrameworkCoreServiceCollectionExtensions {
    /// <summary>
    /// Registers Web Push (<c>AddElarionWebPush</c>) with durable <see cref="IPushSubscriptionStore"/> and
    /// <see cref="IVapidKeyStore"/> over <typeparamref name="TDbContext"/>, replacing the in-memory defaults.
    /// The context must map the Web Push tables — annotate it with <c>[GenerateElarionWebPush]</c> or call
    /// <c>modelBuilder.UseElarionWebPush()</c>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Configures the options; <see cref="WebPushOptions.Subject"/> is required.</param>
    public static IServiceCollection AddElarionWebPushEntityFrameworkCore<TDbContext>(
        this IServiceCollection services,
        Action<WebPushOptions>? configure = null)
        where TDbContext : DbContext {
        ArgumentNullException.ThrowIfNull(services);
        services.AddElarionWebPush(configure);
        services.RemoveAll<IPushSubscriptionStore>();
        services.AddSingleton<IPushSubscriptionStore, EfCorePushSubscriptionStore<TDbContext>>();
        services.RemoveAll<IVapidKeyStore>();
        services.AddSingleton<IVapidKeyStore, EfCoreVapidKeyStore<TDbContext>>();
        return services;
    }
}
