using System.Globalization;
using Elarion.Abstractions.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Elarion.Blobs.Tus;

/// <summary>
/// Maps the tus 1.0 resumable-upload endpoints (Core + Creation + Expiration + Termination) — a pure
/// protocol adapter over the <see cref="IStagedUploadStore"/> seam. A completed upload produces a pending
/// blob whose reference is returned in the <c>Elarion-Blob-Ref</c> response header (also available on
/// <c>HEAD</c>); the client passes that reference when creating the owning entity, and an uncommitted
/// upload is reclaimed by garbage collection.
/// </summary>
public static class TusEndpointsExtensions {
    /// <summary>
    /// Maps the tus endpoints under the configured route prefix and returns the route group so the host can
    /// apply conventions (most importantly <c>.RequireAuthorization(...)</c>).
    /// </summary>
    public static RouteGroupBuilder MapElarionTus(this IEndpointRouteBuilder endpoints) {
        ArgumentNullException.ThrowIfNull(endpoints);

        var options = endpoints.ServiceProvider.GetRequiredService<TusOptions>();
        var group = endpoints.MapGroup(options.RoutePrefix);

        group.MapMethods("", ["OPTIONS"], OptionsAsync);
        group.MapMethods("/{id}", ["OPTIONS"], OptionsAsync);
        group.MapPost("", CreateAsync);
        group.MapMethods("/{id}", ["HEAD"], HeadAsync);
        group.MapMethods("/{id}", ["PATCH"], PatchAsync);
        group.MapDelete("/{id}", DeleteAsync);

        return group;
    }

    private static Task OptionsAsync(HttpContext context, TusOptions options) {
        var response = context.Response;
        response.Headers[TusProtocol.Resumable] = TusProtocol.Version;
        response.Headers[TusProtocol.VersionHeader] = TusProtocol.Version;
        response.Headers[TusProtocol.Extension] = TusProtocol.Extensions;
        if (options.MaxSize is long max) {
            response.Headers[TusProtocol.MaxSize] = max.ToString(CultureInfo.InvariantCulture);
        }

        response.StatusCode = StatusCodes.Status204NoContent;
        return Task.CompletedTask;
    }

    private static async Task CreateAsync(
        HttpContext context,
        ICurrentUser currentUser,
        IStagedUploadStore store,
        TusOptions options,
        TimeProvider timeProvider,
        CancellationToken cancellationToken) {
        var response = context.Response;
        SetResumable(response);

        if (!currentUser.IsAuthenticated) {
            response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        if (!TryGetLong(context.Request.Headers[TusProtocol.UploadLength], out var length) || length < 0) {
            response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        if (options.MaxSize is long max && length > max) {
            response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }

        var rawMetadata = context.Request.Headers[TusProtocol.UploadMetadata].ToString();
        var metadata = TusMetadata.Parse(rawMetadata);
        var fileName = SanitizeFileName(Pick(metadata, "filename", "name"));
        var contentType = Pick(metadata, "filetype", "type") is { Length: > 0 } type
            ? type
            : "application/octet-stream";

        var upload = await store.CreateAsync(
            new StagedUploadCreation {
                Container = options.Container,
                Name = $"{currentUser.UserId}/{Guid.NewGuid():N}/{fileName}",
                Length = length,
                ContentType = contentType,
                Metadata = string.IsNullOrEmpty(rawMetadata) ? null : rawMetadata,
                OwnerId = currentUser.UserId,
                ExpiresAt = timeProvider.GetUtcNow() + options.UploadExpiry,
            },
            cancellationToken);

        // A zero-length upload is complete on creation (tus needs no PATCH), so seal it now — completion
        // yields the blob reference the same way a final PATCH would.
        if (length == 0) {
            upload = await CompleteAsync(store, upload.Id, options, timeProvider, cancellationToken);
        }

        response.Headers.Location = BuildLocation(context.Request, options.RoutePrefix, upload.Id);
        response.Headers[TusProtocol.UploadOffset] = upload.Offset.ToString(CultureInfo.InvariantCulture);
        SetExpires(response, upload);
        SetBlobRef(response, upload);
        response.StatusCode = StatusCodes.Status201Created;
    }

    private static async Task HeadAsync(
        HttpContext context,
        string id,
        ICurrentUser currentUser,
        IStagedUploadStore store,
        TusOptions options,
        TimeProvider timeProvider,
        CancellationToken cancellationToken) {
        var response = context.Response;
        SetResumable(response);

        var upload = await store.GetAsync(id, cancellationToken);
        if (upload is null || !IsOwner(upload, currentUser)) {
            response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        // Self-heal the crash window between the last append and completion: a session that has all its
        // bytes but no blob reference is completed (idempotently) right here, so the client's status probe
        // yields the reference instead of a forever-stuck "done but ref-less" upload.
        if (upload is { Length: long total, IsComplete: false } && upload.Offset >= total) {
            try {
                upload = await CompleteAsync(store, id, options, timeProvider, cancellationToken);
            }
            catch (StagedUploadConflictException) {
                // Raced by a concurrent completion or delete; serve the current state.
                upload = await store.GetAsync(id, cancellationToken);
                if (upload is null || !IsOwner(upload, currentUser)) {
                    response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }
            }
        }

        response.Headers.CacheControl = "no-store";
        response.Headers[TusProtocol.UploadOffset] = upload.Offset.ToString(CultureInfo.InvariantCulture);
        if (upload.Length is long length) {
            response.Headers[TusProtocol.UploadLength] = length.ToString(CultureInfo.InvariantCulture);
        }

        if (upload.Metadata is not null) {
            response.Headers[TusProtocol.UploadMetadata] = upload.Metadata;
        }

        SetExpires(response, upload);
        SetBlobRef(response, upload);
        response.StatusCode = StatusCodes.Status200OK;
    }

    private static async Task PatchAsync(
        HttpContext context,
        string id,
        ICurrentUser currentUser,
        IStagedUploadStore store,
        TusOptions options,
        TimeProvider timeProvider,
        CancellationToken cancellationToken) {
        var response = context.Response;
        SetResumable(response);

        if (!currentUser.IsAuthenticated) {
            response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        if (!IsOffsetContentType(context.Request.ContentType)) {
            response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
            return;
        }

        var upload = await store.GetAsync(id, cancellationToken);
        if (upload is null || !IsOwner(upload, currentUser)) {
            response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (upload.ExpiresAt <= timeProvider.GetUtcNow()) {
            response.StatusCode = StatusCodes.Status410Gone;
            return;
        }

        if (!TryGetLong(context.Request.Headers[TusProtocol.UploadOffset], out var offset)) {
            response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        if (offset != upload.Offset) {
            response.StatusCode = StatusCodes.Status409Conflict;
            return;
        }

        StagedUpload updated;
        try {
            updated = await store.AppendAsync(id, offset, context.Request.Body, cancellationToken);

            // tus 1.0 declares the length up front, so the append that reaches it completes the upload —
            // the explicit-completion analog of the IETF draft's Upload-Complete flag.
            if (updated is { Length: long total, IsComplete: false } && updated.Offset >= total) {
                updated = await CompleteAsync(store, id, options, timeProvider, cancellationToken);
            }
        }
        catch (StagedUploadConflictException) {
            response.StatusCode = StatusCodes.Status409Conflict;
            return;
        }

        response.Headers[TusProtocol.UploadOffset] = updated.Offset.ToString(CultureInfo.InvariantCulture);
        SetExpires(response, updated);
        SetBlobRef(response, updated);
        response.StatusCode = StatusCodes.Status204NoContent;
    }

    private static async Task DeleteAsync(
        HttpContext context,
        string id,
        ICurrentUser currentUser,
        IStagedUploadStore store,
        CancellationToken cancellationToken) {
        var response = context.Response;
        SetResumable(response);

        var upload = await store.GetAsync(id, cancellationToken);
        if (upload is null || !IsOwner(upload, currentUser)) {
            response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await store.DeleteAsync(id, cancellationToken);
        response.StatusCode = StatusCodes.Status204NoContent;
    }

    // Completion policy is the endpoint's: the pending blob's time-to-live and the completed session's
    // retention window are computed here and handed to the store as data.
    private static Task<StagedUpload> CompleteAsync(
        IStagedUploadStore store,
        string id,
        TusOptions options,
        TimeProvider timeProvider,
        CancellationToken cancellationToken) {
        var now = timeProvider.GetUtcNow();
        return store.CompleteAsync(
            id,
            new StagedUploadCompletion {
                SessionExpiresAt = now + options.CompletedSessionRetention,
                BlobExpiresAt = now + options.Ttl,
            },
            cancellationToken);
    }

    private static void SetResumable(HttpResponse response) =>
        response.Headers[TusProtocol.Resumable] = TusProtocol.Version;

    private static void SetExpires(HttpResponse response, StagedUpload upload) =>
        response.Headers[TusProtocol.UploadExpires] = upload.ExpiresAt.ToString("R", CultureInfo.InvariantCulture);

    private static void SetBlobRef(HttpResponse response, StagedUpload upload) {
        if (upload.BlobRef is { } blobRef) {
            response.Headers[TusProtocol.BlobRef] = blobRef.Value;
        }
    }

    // Owner-scoped operations require an authenticated caller whose id matches the upload's recorded owner.
    // A session with no recorded owner is denied to everyone (fail closed), matching the direct-upload
    // endpoint's stance, so a null/empty owner id can never be matched by an unauthenticated caller.
    private static bool IsOwner(StagedUpload upload, ICurrentUser currentUser) =>
        currentUser.IsAuthenticated
        && upload.OwnerId is not null
        && string.Equals(upload.OwnerId, currentUser.UserId, StringComparison.Ordinal);

    private static bool IsOffsetContentType(string? contentType) =>
        contentType is not null
        && contentType.StartsWith(TusProtocol.OffsetContentType, StringComparison.OrdinalIgnoreCase);

    private static bool TryGetLong(string? value, out long result) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);

    private static string? Pick(IReadOnlyDictionary<string, string> metadata, string primary, string fallback) {
        if (metadata.TryGetValue(primary, out var value) && value.Length > 0) {
            return value;
        }

        return metadata.TryGetValue(fallback, out var alternate) && alternate.Length > 0 ? alternate : null;
    }

    private static string SanitizeFileName(string? name) {
        if (string.IsNullOrWhiteSpace(name)) {
            return "upload";
        }

        var leaf = name.Replace('\\', '/');
        var slash = leaf.LastIndexOf('/');
        if (slash >= 0) {
            leaf = leaf[(slash + 1)..];
        }

        return string.IsNullOrWhiteSpace(leaf) ? "upload" : leaf;
    }

    private static string BuildLocation(HttpRequest request, string routePrefix, string id) =>
        $"{request.Scheme}://{request.Host}{request.PathBase}{routePrefix}/{id}";
}
