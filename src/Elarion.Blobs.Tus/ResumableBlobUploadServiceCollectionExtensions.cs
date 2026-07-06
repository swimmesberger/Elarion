using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Elarion.Blobs.Tus;

/// <summary>
/// Registers the services backing the tus resumable-upload endpoints.
/// </summary>
public static class ResumableBlobUploadServiceCollectionExtensions {
    /// <summary>
    /// Registers <see cref="ResumableBlobUploadOptions"/> and the staged-upload seam (with its in-memory default) for
    /// the endpoints mapped by <c>MapElarionResumableBlobUploads</c>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional configuration of <see cref="ResumableBlobUploadOptions"/>.</param>
    /// <returns>The same service collection for chaining.</returns>
    /// <remarks>
    /// The endpoints additionally require an <see cref="IBlobStore"/> and an <c>ICurrentUser</c> from
    /// the host. Replace the in-memory staging default with a durable <see cref="IStagedUploadStore"/>
    /// (for example PostgreSQL staging via <c>AddElarionPostgreSqlStagedUploads</c>, or Azure append-blob
    /// staging via <c>AddElarionAzureStagedUploads</c>) for resumability across restarts and instances.
    /// </remarks>
    public static IServiceCollection AddElarionResumableBlobUploads(
        this IServiceCollection services,
        Action<ResumableBlobUploadOptions>? configure = null) {
        ArgumentNullException.ThrowIfNull(services);

        var options = new ResumableBlobUploadOptions();
        configure?.Invoke(options);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(options);
        services.AddElarionStagedUploads();

        return services;
    }
}
