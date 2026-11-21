using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

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
    /// <para>
    /// The options go through the options pattern: <paramref name="configure"/> is registered as a
    /// <c>Configure</c> action, so <c>IOptions&lt;ResumableBlobUploadOptions&gt;</c>, a later
    /// <c>services.Configure&lt;ResumableBlobUploadOptions&gt;(…)</c> and the endpoints all see the same
    /// configured value.
    /// </para>
    /// <para>
    /// The endpoints additionally require an <see cref="IBlobStore"/> and an <c>ICurrentUser</c> from
    /// the host. Replace the in-memory staging default with a durable <see cref="IStagedUploadStore"/>
    /// (for example PostgreSQL staging via <c>AddElarionPostgreSqlStagedUploads</c>, or Azure append-blob
    /// staging via <c>AddElarionAzureStagedUploads</c>) for resumability across restarts and instances.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddElarionResumableBlobUploads(
        this IServiceCollection services,
        Action<ResumableBlobUploadOptions>? configure = null) {
        ArgumentNullException.ThrowIfNull(services);

        var options = services.AddOptions<ResumableBlobUploadOptions>();
        if (configure is not null) options.Configure(configure);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(static provider =>
            provider.GetRequiredService<IOptions<ResumableBlobUploadOptions>>().Value);
        services.AddElarionStagedUploads();

        return services;
    }
}
