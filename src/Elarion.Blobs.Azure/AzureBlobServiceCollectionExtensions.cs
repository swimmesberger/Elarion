using Azure.Storage.Blobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Elarion.Blobs.Azure;

/// <summary>
/// Registers the Azure Blob Storage implementations of the blob seams.
/// </summary>
/// <remarks>
/// All overloads resolving a <see cref="BlobServiceClient"/> from DI expect the host to have registered
/// one (for example via the Aspire/Azure client integrations); the <c>connectionString</c> overloads
/// register a shared singleton via <c>TryAdd</c>, so a host-registered client wins.
/// </remarks>
public static class AzureBlobServiceCollectionExtensions {
    /// <summary>
    /// Registers <see cref="IBlobStore"/> using <see cref="AzureBlobStore"/>. Requires a
    /// <see cref="BlobServiceClient"/> in the container; use the <c>connectionString</c> overload to
    /// register one in the same call.
    /// </summary>
    /// <param name="services">The service collection to add blob storage to.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddElarionAzureBlobStore(this IServiceCollection services) {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<AzureBlobStore>();
        services.TryAddSingleton<IBlobStore>(provider => provider.GetRequiredService<AzureBlobStore>());
        return services;
    }

    /// <summary>
    /// Registers <see cref="IBlobStore"/> using <see cref="AzureBlobStore"/>, plus a shared
    /// <see cref="BlobServiceClient"/> for <paramref name="connectionString"/> (via <c>TryAdd</c>, so a
    /// host-registered client wins).
    /// </summary>
    /// <param name="services">The service collection to add blob storage to.</param>
    /// <param name="connectionString">The Azure Storage connection string.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddElarionAzureBlobStore(
        this IServiceCollection services,
        string connectionString) {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.TryAddSingleton(_ => new BlobServiceClient(connectionString));
        return services.AddElarionAzureBlobStore();
    }

    /// <summary>
    /// Registers the blob lifecycle (<see cref="IBlobLifecycle"/>) plus the background garbage collector
    /// that reclaims expired pending blobs, on top of
    /// <see cref="AddElarionAzureBlobStore(IServiceCollection)"/>.
    /// </summary>
    /// <param name="services">The service collection to add the lifecycle to.</param>
    /// <param name="configure">Optional configuration of <see cref="BlobGcOptions"/>.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddElarionAzureBlobLifecycle(
        this IServiceCollection services,
        Action<BlobGcOptions>? configure = null) {
        ArgumentNullException.ThrowIfNull(services);

        services.AddElarionAzureBlobStore();
        services.TryAddSingleton<IBlobLifecycle>(provider => provider.GetRequiredService<AzureBlobStore>());
        services.AddElarionBlobGarbageCollection(configure);

        return services;
    }

    /// <summary>
    /// The <c>connectionString</c> overload of
    /// <see cref="AddElarionAzureBlobLifecycle(IServiceCollection, Action{BlobGcOptions}?)"/>.
    /// </summary>
    /// <param name="services">The service collection to add the lifecycle to.</param>
    /// <param name="connectionString">The Azure Storage connection string.</param>
    /// <param name="configure">Optional configuration of <see cref="BlobGcOptions"/>.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddElarionAzureBlobLifecycle(
        this IServiceCollection services,
        string connectionString,
        Action<BlobGcOptions>? configure = null) {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.TryAddSingleton(_ => new BlobServiceClient(connectionString));
        return services.AddElarionAzureBlobLifecycle(configure);
    }

    /// <summary>
    /// Replaces the in-memory staged-upload store with the Azure append-blob staging store, registers
    /// the background collector for expired and completed sessions, and wires the Azure blob lifecycle
    /// plus its pending-blob garbage collector.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configureStaging">Optional configuration of <see cref="AzureStagedUploadOptions"/>.</param>
    /// <param name="configureGc">Optional configuration of the session <see cref="StagedUploadGcOptions"/>.</param>
    /// <param name="configureBlobGc">Optional configuration of the pending-blob <see cref="BlobGcOptions"/>.</param>
    /// <returns>The same service collection for chaining.</returns>
    /// <remarks>
    /// A completed staged upload is written (by server-side copy) as a <b>pending</b> blob, so this
    /// method also wires <see cref="AddElarionAzureBlobLifecycle(IServiceCollection, Action{BlobGcOptions}?)"/>
    /// (idempotent) — otherwise an abandoned upload would leak its pending blob forever.
    /// </remarks>
    public static IServiceCollection AddElarionAzureStagedUploads(
        this IServiceCollection services,
        Action<AzureStagedUploadOptions>? configureStaging = null,
        Action<StagedUploadGcOptions>? configureGc = null,
        Action<BlobGcOptions>? configureBlobGc = null) {
        ArgumentNullException.ThrowIfNull(services);

        var stagingOptions = new AzureStagedUploadOptions();
        configureStaging?.Invoke(stagingOptions);
        services.TryAddSingleton(stagingOptions);

        // Replace the in-memory default registered by AddElarionStagedUploads.
        services.RemoveAll<IStagedUploadStore>();
        services.AddSingleton<IStagedUploadStore, AzureStagedUploadStore>();

        services.AddElarionStagedUploadGarbageCollection(configureGc);
        services.AddElarionAzureBlobLifecycle(configureBlobGc);

        return services;
    }

    /// <summary>
    /// The <c>connectionString</c> overload of
    /// <see cref="AddElarionAzureStagedUploads(IServiceCollection, Action{AzureStagedUploadOptions}?, Action{StagedUploadGcOptions}?, Action{BlobGcOptions}?)"/>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="connectionString">The Azure Storage connection string.</param>
    /// <param name="configureStaging">Optional configuration of <see cref="AzureStagedUploadOptions"/>.</param>
    /// <param name="configureGc">Optional configuration of the session <see cref="StagedUploadGcOptions"/>.</param>
    /// <param name="configureBlobGc">Optional configuration of the pending-blob <see cref="BlobGcOptions"/>.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddElarionAzureStagedUploads(
        this IServiceCollection services,
        string connectionString,
        Action<AzureStagedUploadOptions>? configureStaging = null,
        Action<StagedUploadGcOptions>? configureGc = null,
        Action<BlobGcOptions>? configureBlobGc = null) {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.TryAddSingleton(_ => new BlobServiceClient(connectionString));
        return services.AddElarionAzureStagedUploads(configureStaging, configureGc, configureBlobGc);
    }
}
