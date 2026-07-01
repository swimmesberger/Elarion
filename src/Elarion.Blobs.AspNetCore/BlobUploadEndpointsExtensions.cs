using Elarion.Abstractions.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Elarion.Blobs.AspNetCore;

/// <summary>
/// Maps the direct blob-upload endpoints: a pre-upload transport that produces pending blobs the client
/// later references, and a cancel endpoint. This is the smallest "accept bytes, return a reference"
/// surface — not a protocol — and fits FilePond's <c>process</c>/<c>revert</c> and plain
/// <c>fetch</c>/<c>&lt;form&gt;</c> clients. For resumable, large, or browser-close-resilient uploads, use
/// the tus transport instead.
/// </summary>
public static class BlobUploadEndpointsExtensions {
    /// <summary>
    /// Maps the upload (<c>POST</c>/<c>PUT</c>) and cancel (<c>DELETE</c>) endpoints under the configured
    /// route prefix.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder to map onto.</param>
    /// <returns>
    /// The mapped route group, so the host can apply conventions (most importantly
    /// <c>.RequireAuthorization(...)</c>, since the upload bakes the current user's id into the blob).
    /// </returns>
    public static RouteGroupBuilder MapElarionBlobUploads(this IEndpointRouteBuilder endpoints) {
        ArgumentNullException.ThrowIfNull(endpoints);

        var options = endpoints.ServiceProvider.GetRequiredService<BlobUploadEndpointOptions>();
        var group = endpoints.MapGroup(options.RoutePrefix);

        // The caller's authenticated session is the upload's authorization, so the cookie/form CSRF
        // defense does not apply to this API-style endpoint.
        group.MapPost("", UploadAsync).DisableAntiforgery();
        group.MapPut("", UploadAsync).DisableAntiforgery();
        group.MapDelete("/{blobId}", CancelAsync);

        return group;
    }

    private static async Task<IResult> UploadAsync(
        HttpRequest request,
        ICurrentUser currentUser,
        IBlobStore blobStore,
        BlobUploadEndpointOptions options,
        TimeProvider timeProvider,
        CancellationToken cancellationToken) {
        if (!currentUser.IsAuthenticated) {
            return Results.Unauthorized();
        }

        string contentType;
        string clientName;
        long? lengthHint;
        Stream source;

        if (request.HasFormContentType) {
            var form = await request.ReadFormAsync(cancellationToken);
            var file = form.Files.GetFile("file") ?? (form.Files.Count > 0 ? form.Files[0] : null);
            if (file is null) {
                return Results.BadRequest("No file part was found in the multipart form.");
            }

            contentType = NormalizeContentType(file.ContentType);
            clientName = file.FileName;
            // Known length over a seekable form stream lets the store write without buffering.
            lengthHint = file.Length;
            source = file.OpenReadStream();
        }
        else {
            contentType = NormalizeContentType(request.ContentType);
            clientName = request.Headers["X-Elarion-File-Name"].ToString() is { Length: > 0 } header
                ? header
                : request.Query["fileName"].ToString();
            // Raw bodies are buffered to measure, capped while read.
            lengthHint = null;
            source = request.Body;
        }

        if (!IsAllowedContentType(contentType, options.AllowedContentTypes)) {
            return Results.BadRequest($"Content type '{contentType}' is not allowed.");
        }

        if (options.MaxContentLength is long cap) {
            // Reject early when the declared length already exceeds the cap (multipart, or a raw body with
            // a Content-Length); otherwise cap the unknown-length stream while it is read.
            var declared = lengthHint ?? request.ContentLength;
            if (declared is long length && length > cap) {
                return TooLarge(cap);
            }

            if (lengthHint is null) {
                source = new LengthCappingStream(source, cap);
            }
        }

        var uploadRequest = new BlobUploadRequest {
            Container = options.Container,
            Name = BuildStorageName(currentUser.UserId, clientName),
            ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType,
            ContentLength = lengthHint,
            InitialState = BlobLifecycleState.Pending,
            ExpiresAt = timeProvider.GetUtcNow() + options.Ttl,
            OwnerId = currentUser.UserId,
        };

        try {
            var blobRef = await blobStore.SaveAsync(uploadRequest, source, cancellationToken);
            // Plain-text id: FilePond reads the response body as the file id, and any client can read it
            // directly. The id is the reference the client passes when creating the owning entity.
            return Results.Text(blobRef.Value, "text/plain");
        }
        catch (BlobUploadTooLargeException ex) {
            return TooLarge(ex.Limit);
        }
    }

    private static async Task<IResult> CancelAsync(
        string blobId,
        ICurrentUser currentUser,
        IBlobStore blobStore,
        BlobUploadEndpointOptions options,
        CancellationToken cancellationToken) {
        if (!currentUser.IsAuthenticated) {
            return Results.Unauthorized();
        }

        var blobRef = new BlobRef { Value = blobId };
        var metadata = await blobStore.GetMetadataAsync(blobRef, cancellationToken);

        // Only the owner may cancel their own upload; a missing or unowned blob is reported as not found so
        // ownership is never leaked. Cancel is intended for a pending blob before it is committed.
        if (metadata is null
            || metadata.Container != options.Container
            || !IsOwnedBy(metadata, currentUser.UserId)) {
            return Results.NotFound();
        }

        await blobStore.DeleteAsync(blobRef, cancellationToken);
        return Results.NoContent();
    }

    private static string BuildStorageName(string ownerId, string clientName) =>
        $"{ownerId}/{Guid.NewGuid():N}/{SanitizeFileName(clientName)}";

    // Ownership is compared against the recorded owner id exactly, not parsed from the storage name, so an
    // owner id that happens to contain the naming separator cannot be forged. A blob with no recorded owner
    // is denied to everyone (fail closed).
    private static bool IsOwnedBy(BlobMetadata metadata, string ownerId) =>
        metadata.OwnerId is not null && string.Equals(metadata.OwnerId, ownerId, StringComparison.Ordinal);

    private static string SanitizeFileName(string name) {
        if (string.IsNullOrWhiteSpace(name)) {
            return "upload";
        }

        // Keep only the leaf and drop path separators so the owner/guid prefix stays the only structure.
        var leaf = name.Replace('\\', '/');
        var slash = leaf.LastIndexOf('/');
        if (slash >= 0) {
            leaf = leaf[(slash + 1)..];
        }

        return string.IsNullOrWhiteSpace(leaf) ? "upload" : leaf;
    }

    private static string NormalizeContentType(string? contentType) {
        if (string.IsNullOrWhiteSpace(contentType)) {
            return string.Empty;
        }

        var semicolon = contentType.IndexOf(';');
        return (semicolon >= 0 ? contentType[..semicolon] : contentType).Trim();
    }

    private static bool IsAllowedContentType(string contentType, IReadOnlyCollection<string>? allowed) =>
        allowed is null || allowed.Count == 0 || allowed.Contains(contentType, StringComparer.OrdinalIgnoreCase);

    private static IResult TooLarge(long cap) =>
        Results.Problem(
            statusCode: StatusCodes.Status413PayloadTooLarge,
            detail: $"The upload exceeded the maximum allowed size of {cap} bytes.");
}
