using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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
    /// The endpoints additionally require an <see cref="IBlobStore"/> (for example
    /// <c>AddPostgreSqlBlobLifecycle</c>) and an <c>ICurrentUser</c> (for example
    /// <c>AddElarionCurrentUser</c> or the optional Identity integration) to be registered by the host.
    /// </remarks>
    public static IServiceCollection AddElarionBlobUploads(
        this IServiceCollection services,
        Action<BlobUploadEndpointOptions>? configure = null) {
        ArgumentNullException.ThrowIfNull(services);

        var options = new BlobUploadEndpointOptions();
        configure?.Invoke(options);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(options);

        return services;
    }
}
