using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Elarion.Blobs.AspNetCore;

/// <summary>
/// Registers the services backing the direct blob-upload endpoints.
/// </summary>
public static class BlobUploadServiceCollectionExtensions {
    /// <summary>
    /// Registers <see cref="BlobUploadEndpointOptions"/> for the endpoints mapped by
    /// <c>MapElarionBlobUploads</c>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional configuration of <see cref="BlobUploadEndpointOptions"/>.</param>
    /// <returns>The same service collection for chaining.</returns>
    /// <remarks>
    /// <para>
    /// The options go through the options pattern: <paramref name="configure"/> is registered as a
    /// <c>Configure</c> action, so <c>IOptions&lt;BlobUploadEndpointOptions&gt;</c>, a later
    /// <c>services.Configure&lt;BlobUploadEndpointOptions&gt;(…)</c> and the endpoints all see the same
    /// configured value (the endpoints resolve <see cref="BlobUploadEndpointOptions"/> itself, a singleton
    /// taken from <c>IOptions</c>). Calling this method again adds another configure action.
    /// </para>
    /// <para>
    /// The endpoints additionally require an <see cref="IBlobStore"/> (for example
    /// <c>AddElarionPostgreSqlBlobLifecycle</c>) and an <c>ICurrentUser</c> (for example
    /// <c>AddElarionCurrentUser</c> or the optional Identity integration) to be registered by the host.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddElarionBlobUploads(
        this IServiceCollection services,
        Action<BlobUploadEndpointOptions>? configure = null) {
        ArgumentNullException.ThrowIfNull(services);

        var options = services.AddOptions<BlobUploadEndpointOptions>();
        if (configure is not null) options.Configure(configure);

        services.TryAddSingleton(TimeProvider.System);
        // One value for both views: an app reading IOptions<BlobUploadEndpointOptions> (for the route prefix, say)
        // sees exactly what the endpoints use.
        services.TryAddSingleton(static provider =>
            provider.GetRequiredService<IOptions<BlobUploadEndpointOptions>>().Value);

        return services;
    }
}
