using System.Text;
using Elarion.Abstractions.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
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
    // Room for the multipart boundaries, part headers and small fields around the file part.
    private const long FormOverhead = 1024 * 1024;

    /// <summary>
    /// Maps the upload (<c>POST</c>/<c>PUT</c>) and cancel (<c>DELETE</c>) endpoints under the configured
    /// route prefix.
    /// </summary>
    /// <remarks>
    /// An upload may name an <see cref="UploadPolicy"/> with <c>?policy=</c> (and the record it is for with
    /// <c>&amp;resourceId=</c>): its limits replace the endpoint's, and its check runs before the body is read. The
    /// request's body-size limit is raised to the cap that applies when the server's is lower, so a cap above the
    /// server's default works for this request without raising it for every route.
    /// </remarks>
    /// <param name="endpoints">The endpoint route builder to map onto.</param>
    /// <returns>
    /// The mapped route group, so the host can apply conventions (most importantly
    /// <c>.RequireAuthorization(...)</c>, since the upload bakes the current user's id into the blob).
    /// </returns>
    public static RouteGroupBuilder MapElarionBlobUploads(this IEndpointRouteBuilder endpoints) {
        ArgumentNullException.ThrowIfNull(endpoints);

        var options = endpoints.ServiceProvider.GetRequiredService<BlobUploadEndpointOptions>();
        var group = endpoints.MapGroup(options.RoutePrefix);

        // AOT-safe RequestDelegate overloads: typed method groups here would route through the
        // reflection-based RequestDelegateFactory, which is broken under Native AOT for framework-owned
        // call sites (ADR-0071). The shape MethodInfos keep ApiExplorer describing the endpoints exactly
        // as the typed signatures did.
        var uploadShape =
            ((Func<HttpRequest, ICurrentUser, IBlobStore, BlobUploadEndpointOptions, TimeProvider,
                CancellationToken, Task<IResult>>)UploadApiShape).Method;
        // The caller's authenticated session is the upload's authorization, so the cookie/form CSRF
        // defense does not apply to this API-style endpoint.
        group.MapPost("", (RequestDelegate)UploadAsync).DisableAntiforgery().WithMetadata(uploadShape);
        group.MapPut("", (RequestDelegate)UploadAsync).DisableAntiforgery().WithMetadata(uploadShape);
        group.MapDelete("/{blobId}", (RequestDelegate)CancelAsync)
            .WithMetadata(
                ((Func<string, ICurrentUser, IBlobStore, BlobUploadEndpointOptions, CancellationToken,
                    Task<IResult>>)CancelApiShape).Method);

        return group;
    }

    private static async Task UploadAsync(HttpContext context) {
        BlobEndpointResponses.ForbidSniffing(context.Response);
        var services = context.RequestServices;
        var result = await UploadCoreAsync(
            context.Request,
            services.GetRequiredService<ICurrentUser>(),
            services.GetRequiredService<IBlobStore>(),
            services.GetRequiredService<BlobUploadEndpointOptions>(),
            services.GetRequiredService<TimeProvider>(),
            context.RequestAborted);
        await result.ExecuteAsync(context);
    }

    private static async Task CancelAsync(HttpContext context) {
        BlobEndpointResponses.ForbidSniffing(context.Response);
        var blobId = context.Request.RouteValues["blobId"]!.ToString()!;
        var services = context.RequestServices;
        var result = await CancelCoreAsync(
            blobId,
            services.GetRequiredService<ICurrentUser>(),
            services.GetRequiredService<IBlobStore>(),
            services.GetRequiredService<BlobUploadEndpointOptions>(),
            context.RequestAborted);
        await result.ExecuteAsync(context);
    }

    /// <summary>
    /// OpenAPI shape for the blob-upload endpoints — never invoked; ApiExplorer reads parameters from
    /// this signature.
    /// </summary>
    private static Task<IResult> UploadApiShape(
        HttpRequest request,
        ICurrentUser currentUser,
        IBlobStore blobStore,
        BlobUploadEndpointOptions options,
        TimeProvider timeProvider,
        CancellationToken cancellationToken) {
        throw new NotSupportedException("OpenAPI metadata shape only; requests execute through the RequestDelegate.");
    }

    /// <summary>
    /// OpenAPI shape for the blob-cancel endpoint — never invoked; ApiExplorer reads parameters from
    /// this signature.
    /// </summary>
    private static Task<IResult> CancelApiShape(
        string blobId,
        ICurrentUser currentUser,
        IBlobStore blobStore,
        BlobUploadEndpointOptions options,
        CancellationToken cancellationToken) {
        throw new NotSupportedException("OpenAPI metadata shape only; requests execute through the RequestDelegate.");
    }

    private static async Task<IResult> UploadCoreAsync(
        HttpRequest request,
        ICurrentUser currentUser,
        IBlobStore blobStore,
        BlobUploadEndpointOptions options,
        TimeProvider timeProvider,
        CancellationToken cancellationToken) {
        if (!currentUser.IsAuthenticated) return Results.Unauthorized();

        var maxBytes = options.MaxContentLength;
        var allowedContentTypes = options.AllowedContentTypes;
        if (request.Query["policy"].ToString() is { Length: > 0 } policyName) {
            var policies = request.HttpContext.RequestServices.GetService<UploadPolicies>();
            if (policies is null || !policies.TryGet(policyName, out var policy))
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest, detail: $"Unknown upload policy '{policyName}'.");

            // Before the body is read: a refused upload never sends its bytes to the store. Not found, so the record
            // the request names is not confirmed to exist.
            var allowed = await policy.AllowsAsync(
                new UploadPolicyContext {
                    Policy = policyName,
                    ResourceId = request.Query["resourceId"].ToString() is { Length: > 0 } resourceId ? resourceId : null,
                    User = currentUser,
                    Services = request.HttpContext.RequestServices,
                    Length = request.ContentLength
                },
                cancellationToken);
            if (!allowed) return Results.NotFound();

            maxBytes = policy.MaxBytes ?? maxBytes;
            allowedContentTypes = policy.ContentTypes ?? allowedContentTypes;
        }

        if (maxBytes is long bodyCap)
            RaiseRequestBodyLimit(request.HttpContext, request.HasFormContentType ? bodyCap + FormOverhead : bodyCap);

        string contentType;
        string clientName;
        long? lengthHint;
        Stream source;

        if (request.HasFormContentType) {
            IFormCollection form;
            try {
                // The file part is capped while it is read instead of after the whole form is buffered.
                form = maxBytes is long partCap
                    ? await request.ReadFormAsync(new FormOptions { MultipartBodyLengthLimit = partCap }, cancellationToken)
                    : await request.ReadFormAsync(cancellationToken);
            }
            catch (Exception ex) when (maxBytes is long formCap && IsTooLarge(ex)) {
                return TooLarge(formCap);
            }

            var file = form.Files.GetFile("file") ?? (form.Files.Count > 0 ? form.Files[0] : null);
            if (file is null) return Results.BadRequest("No file part was found in the multipart form.");

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
            // A raw body's Content-Length is passed through so the store can stream without buffering
            // (stores verify the actual bytes against the hint); only a chunked body is measured while
            // read, capped below.
            lengthHint = request.ContentLength;
            source = request.Body;
        }

        if (!IsAllowedContentType(contentType, allowedContentTypes))
            return Results.BadRequest($"Content type '{contentType}' is not allowed.");

        if (maxBytes is long cap) {
            // Reject early when the declared length already exceeds the cap (multipart, or a raw body with
            // a Content-Length); otherwise cap the unknown-length stream while it is read.
            var declared = lengthHint ?? request.ContentLength;
            if (declared is long length && length > cap) return TooLarge(cap);

            if (lengthHint is null) source = new LengthCappingStream(source, cap);
        }

        var uploadRequest = new BlobUploadRequest {
            Container = options.Container,
            Name = BuildStorageName(currentUser.UserId, clientName),
            ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType,
            ContentLength = lengthHint,
            InitialState = BlobLifecycleState.Pending,
            ExpiresAt = timeProvider.GetUtcNow() + options.Ttl,
            OwnerId = currentUser.UserId
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
        catch (BadHttpRequestException ex) when (maxBytes is long readCap
                                                 && ex.StatusCode == StatusCodes.Status413PayloadTooLarge) {
            // The server's body limit (raised to the cap above) stopped a body without a declared length.
            return TooLarge(readCap);
        }
    }

    // Raises this request's body-size limit to the cap when the server's is lower, so the cap can be reached. Never
    // lowers it: the endpoint enforces the cap itself, and a server limit above the cap lets the server drain an
    // over-cap body after the 413, so the client reads the answer instead of a closed connection. Only before the
    // first read, and only where the server lets the application change it.
    private static void RaiseRequestBodyLimit(HttpContext context, long limit) {
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false, MaxRequestBodySize: { } current } feature
            && current < limit)
            feature.MaxRequestBodySize = limit;
    }

    // A form part over MultipartBodyLengthLimit, or a body over the request's limit.
    private static bool IsTooLarge(Exception exception) {
        return exception is InvalidDataException
               || exception is BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge };
    }

    private static async Task<IResult> CancelCoreAsync(
        string blobId,
        ICurrentUser currentUser,
        IBlobStore blobStore,
        BlobUploadEndpointOptions options,
        CancellationToken cancellationToken) {
        if (!currentUser.IsAuthenticated) return Results.Unauthorized();

        var blobRef = new BlobRef { Value = blobId };
        var metadata = await blobStore.GetMetadataAsync(blobRef, cancellationToken);

        // Only the owner may cancel their own upload, and only while it is pending: a committed blob belongs to the
        // record that kept it, and deleting it here would break that record. A missing, unowned or committed blob is
        // reported as not found so ownership is never leaked.
        if (metadata is null
            || metadata.Container != options.Container
            || metadata.State != BlobLifecycleState.Pending
            || !BlobEndpointOwnership.IsOwnedBy(metadata, currentUser.UserId))
            return Results.NotFound();

        await blobStore.DeleteAsync(blobRef, cancellationToken);
        return Results.NoContent();
    }

    private static string BuildStorageName(string ownerId, string clientName) {
        return $"{ownerId}/{Guid.CreateVersion7():N}/{SanitizeFileName(clientName)}";
    }

    private static string SanitizeFileName(string name) {
        if (string.IsNullOrWhiteSpace(name)) return "upload";

        // Keep only the leaf and drop path separators so the owner/guid prefix stays the only structure.
        var leaf = name.Replace('\\', '/');
        var slash = leaf.LastIndexOf('/');
        if (slash >= 0) leaf = leaf[(slash + 1)..];

        // Strip control characters and double quotes: a CR/LF or quote in the stored name would later be
        // rejected by the server when rendered into the download Content-Disposition header, leaving the
        // blob permanently undownloadable. (Mirrored in the tus transport's sanitizer.)
        var builder = new StringBuilder(leaf.Length);
        foreach (var ch in leaf)
            if (!char.IsControl(ch) && ch != '"')
                builder.Append(ch);

        leaf = builder.ToString();
        return string.IsNullOrWhiteSpace(leaf) ? "upload" : leaf;
    }

    private static string NormalizeContentType(string? contentType) {
        if (string.IsNullOrWhiteSpace(contentType)) return string.Empty;

        var semicolon = contentType.IndexOf(';');
        return (semicolon >= 0 ? contentType[..semicolon] : contentType).Trim();
    }

    private static bool IsAllowedContentType(string contentType, IReadOnlyCollection<string>? allowed) {
        return allowed is null || allowed.Count == 0 || allowed.Contains(contentType, StringComparer.OrdinalIgnoreCase);
    }

    private static IResult TooLarge(long cap) {
        return Results.Problem(
            statusCode: StatusCodes.Status413PayloadTooLarge,
            detail: $"The upload exceeded the maximum allowed size of {cap} bytes.");
    }
}
