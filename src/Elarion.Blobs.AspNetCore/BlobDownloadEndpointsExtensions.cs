using Elarion.Abstractions.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Elarion.Blobs.AspNetCore;

/// <summary>
/// Maps the owner-scoped, streaming blob-download endpoint (<c>GET {prefix}/{blobId}</c>) — the read side of
/// the direct transfer surface, and the download leg of the <b>staged-blob file tier</b>: a handler writes a
/// large export as a pending blob owned by the current user and returns its <see cref="BlobRef"/>; the client
/// streams it from here. Pending blobs expire through the blob garbage collector, so a never-collected export
/// cleans itself up. Small payloads don't need any of this — they ride <c>ElarionFile</c> directly.
/// </summary>
public static class BlobDownloadEndpointsExtensions {
    /// <summary>
    /// Maps the download (<c>GET /{blobId}</c>) endpoint under the configured route prefix (shared with
    /// <c>MapElarionBlobUploads</c> via <see cref="BlobUploadEndpointOptions"/>; requires
    /// <c>AddElarionBlobUploads</c>).
    /// </summary>
    /// <param name="endpoints">The endpoint route builder to map onto.</param>
    /// <returns>
    /// The mapped route group, so the host can apply conventions (most importantly
    /// <c>.RequireAuthorization(...)</c>, since access is scoped to the blob's recorded owner).
    /// </returns>
    public static RouteGroupBuilder MapElarionBlobDownloads(this IEndpointRouteBuilder endpoints) {
        ArgumentNullException.ThrowIfNull(endpoints);

        var options = endpoints.ServiceProvider.GetRequiredService<BlobUploadEndpointOptions>();
        var group = endpoints.MapGroup(options.RoutePrefix);
        // The AOT-safe RequestDelegate overload: a typed method group here would route through the
        // reflection-based RequestDelegateFactory, which is broken under Native AOT for framework-owned
        // call sites (ADR-0071). The shape MethodInfo keeps ApiExplorer describing the endpoint (route
        // parameter and response) exactly as the typed signature did.
        group.MapGet("/{blobId}", (RequestDelegate)DownloadAsync)
            .WithMetadata(
                ((Func<string, HttpContext, ICurrentUser, IBlobStore, BlobUploadEndpointOptions,
                    CancellationToken, Task<IResult>>)DownloadApiShape).Method);

        return group;
    }

    private static async Task DownloadAsync(HttpContext context) {
        var blobId = context.Request.RouteValues["blobId"]!.ToString()!;
        var services = context.RequestServices;
        var result = await DownloadCoreAsync(
            blobId,
            context,
            services.GetRequiredService<ICurrentUser>(),
            services.GetRequiredService<IBlobStore>(),
            services.GetRequiredService<BlobUploadEndpointOptions>(),
            context.RequestAborted);
        await result.ExecuteAsync(context);
    }

    /// <summary>
    /// OpenAPI shape for the blob-download endpoint — never invoked; ApiExplorer reads parameters from
    /// this signature.
    /// </summary>
    private static Task<IResult> DownloadApiShape(
        string blobId,
        HttpContext httpContext,
        ICurrentUser currentUser,
        IBlobStore blobStore,
        BlobUploadEndpointOptions options,
        CancellationToken cancellationToken) {
        throw new NotSupportedException("OpenAPI metadata shape only; requests execute through the RequestDelegate.");
    }

    private static async Task<IResult> DownloadCoreAsync(
        string blobId,
        HttpContext httpContext,
        ICurrentUser currentUser,
        IBlobStore blobStore,
        BlobUploadEndpointOptions options,
        CancellationToken cancellationToken) {
        if (!currentUser.IsAuthenticated) return Results.Unauthorized();

        var blobRef = new BlobRef { Value = blobId };
        var metadata = await blobStore.GetMetadataAsync(blobRef, cancellationToken);

        // Only the exact recorded owner may read; a missing, foreign-container, or unowned blob is reported
        // as not found so ownership is never leaked.
        if (metadata is null
            || metadata.Container != options.Container
            || !BlobEndpointOwnership.IsOwnedBy(metadata, currentUser.UserId))
            return Results.NotFound();

        var download = await blobStore.OpenReadAsync(blobRef, cancellationToken);
        if (download is null) return Results.NotFound();

        // The download handle (the content stream plus any backend reader it owns) lives exactly until the
        // response completes — streamed end to end, never buffered here.
        httpContext.Response.RegisterForDisposeAsync(download);
        return Results.Stream(download.Content, download.Metadata.ContentType, LeafName(download.Metadata.Name));
    }

    // Storage names are "{owner}/{guid}/{leaf}"; the leaf is the client-facing download name.
    private static string? LeafName(string storageName) {
        var slash = storageName.LastIndexOf('/');
        var leaf = slash >= 0 ? storageName[(slash + 1)..] : storageName;
        return leaf.Length == 0 ? null : leaf;
    }
}
