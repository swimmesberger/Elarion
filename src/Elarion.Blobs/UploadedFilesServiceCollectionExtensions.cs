using Elarion.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Elarion.Blobs;

/// <summary>
/// Registers the <see cref="IUploadedFiles"/> reader that hands staged uploads to handlers taking an
/// <see cref="UploadedFile"/>.
/// </summary>
public static class UploadedFilesServiceCollectionExtensions {
    /// <summary>
    /// Registers <see cref="IUploadedFiles"/> and <see cref="IDeclaredUploadConstraints"/> (one scoped instance, via
    /// <c>TryAdd</c>) and its <see cref="UploadedFilesOptions"/>.
    /// Idempotent: an explicit <paramref name="configure"/> replaces previously registered options, while <c>null</c>
    /// keeps them.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional configuration of <see cref="UploadedFilesOptions"/>.</param>
    /// <returns>The same service collection for chaining.</returns>
    /// <remarks>
    /// The reader needs an <see cref="IBlobStore"/> and an <c>ICurrentUser</c> from the host, the same two services the
    /// upload endpoints need, and <see cref="IBlobLifecycle"/> for <see cref="IUploadedFiles.CommitAsync"/> (for example
    /// <c>AddElarionPostgreSqlBlobLifecycle</c>). It reads what either upload endpoint stored, so it is registered once
    /// for both.
    /// </remarks>
    /// <example>
    /// <code>
    /// services.AddElarionPostgreSqlBlobLifecycle&lt;AppDbContext&gt;();
    /// services.AddElarionBlobUploads(o =&gt; o.Ttl = TimeSpan.FromHours(2)); // a wizard keeps the file for 2 hours
    /// services.AddElarionUploadedFiles();
    /// </code>
    /// </example>
    public static IServiceCollection AddElarionUploadedFiles(
        this IServiceCollection services,
        Action<UploadedFilesOptions>? configure = null) {
        ArgumentNullException.ThrowIfNull(services);

        if (configure is not null) {
            var options = new UploadedFilesOptions();
            configure(options);
            services.RemoveAll<UploadedFilesOptions>();
            services.AddSingleton(options);
        }
        else {
            services.TryAddSingleton(new UploadedFilesOptions());
        }

        // Scoped like the current user and the blob store it reads through. One instance per scope serves both
        // contracts, so the limits the pipeline declares ([UploadLimits]) are the ones the handler's reads check.
        services.TryAddScoped<UploadedFiles>();
        services.TryAddScoped<IUploadedFiles>(static sp => sp.GetRequiredService<UploadedFiles>());
        services.TryAddScoped<IDeclaredUploadConstraints>(static sp => sp.GetRequiredService<UploadedFiles>());

        return services;
    }
}
