using Microsoft.AspNetCore.Http;

namespace Elarion.Blobs.AspNetCore;

/// <summary>What every response of the blob transfer endpoints carries.</summary>
internal static class BlobEndpointResponses {
    /// <summary>
    /// <c>X-Content-Type-Options: nosniff</c>: a stored blob is somebody's file served from the application's own
    /// origin, so the browser must take its declared content type and never sniff it into one it renders (an HTML
    /// or script payload uploaded as <c>text/plain</c>). Set before the endpoint decides its answer, so a 404 or a
    /// problem response carries it too.
    /// </summary>
    public static void ForbidSniffing(HttpResponse response) {
        response.Headers.XContentTypeOptions = "nosniff";
    }
}
