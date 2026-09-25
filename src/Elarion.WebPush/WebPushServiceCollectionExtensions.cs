using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Elarion.WebPush;

/// <summary>Registers Web Push delivery (ADR-0076).</summary>
public static class WebPushServiceCollectionExtensions {
    /// <summary>
    /// Registers <see cref="IWebPushSender"/>, <see cref="WebPushSubscriptionService"/>,
    /// <see cref="IVapidKeyProvider"/>, and the named <see cref="WebPushOptions.HttpClientName"/> client.
    /// Stores default to <see cref="InMemoryPushSubscriptionStore"/>/<see cref="InMemoryVapidKeyStore"/>
    /// (only where none is registered) — use <c>AddElarionWebPushEntityFrameworkCore&lt;TDbContext&gt;()</c>
    /// for durable ones. Safe to call repeatedly; later <paramref name="configure"/> delegates compose onto
    /// the same options instance.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Configures the options; <see cref="WebPushOptions.Subject"/> is required.</param>
    /// <example>
    /// <code>
    /// builder.Services.AddElarionWebPush(options => {
    ///     options.Subject = "mailto:ops@example.com";
    ///     options.PublicKey = builder.Configuration["WebPush:PublicKey"];   // optional pinned keys
    ///     options.PrivateKey = builder.Configuration["WebPush:PrivateKey"];
    /// });
    /// </code>
    /// </example>
    public static IServiceCollection AddElarionWebPush(
        this IServiceCollection services,
        Action<WebPushOptions>? configure = null) {
        ArgumentNullException.ThrowIfNull(services);

        // Compose repeat calls onto the one instance this method registered (the device-identity pattern):
        // keyed descriptors are skipped, and a host-registered non-instance descriptor fails loud.
        var descriptor = services.LastOrDefault(candidate =>
            candidate.ServiceType == typeof(WebPushOptions) && !candidate.IsKeyedService);
        if (descriptor is not null && descriptor.ImplementationInstance is not WebPushOptions)
            throw new InvalidOperationException(
                "WebPushOptions is already registered with a factory or implementation type. "
                + "Configure Web Push via AddElarionWebPush(options => …) instead of registering the options yourself.");

        var existing = descriptor?.ImplementationInstance as WebPushOptions;
        var options = existing ?? new WebPushOptions();
        configure?.Invoke(options);
        options.Validate();
        if (existing is not null) return services;

        services.AddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IPushSubscriptionStore, InMemoryPushSubscriptionStore>();
        services.TryAddSingleton<IVapidKeyStore, InMemoryVapidKeyStore>();
        services.TryAddSingleton<IVapidKeyProvider, VapidKeyProvider>();
        services.TryAddSingleton<VapidTokenFactory>();
        services.TryAddSingleton<WebPushClient>();
        // Scoped: both read ICurrentUser, which is per request.
        services.TryAddScoped<IWebPushSender, WebPushSender>();
        services.TryAddScoped<WebPushSubscriptionService>();

        services.AddHttpClient(WebPushOptions.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(30))
            // Endpoints are allow-listed by host; following a redirect would leave that list.
            .ConfigurePrimaryHttpMessageHandler(static () => new SocketsHttpHandler {
                AllowAutoRedirect = false,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                AutomaticDecompression = DecompressionMethods.None
            });
        return services;
    }
}
