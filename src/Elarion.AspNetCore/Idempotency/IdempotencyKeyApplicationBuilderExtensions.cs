using Microsoft.AspNetCore.Builder;

namespace Elarion.AspNetCore.Idempotency;

/// <summary>Middleware registration helper for HTTP idempotency-key capture.</summary>
public static class IdempotencyKeyApplicationBuilderExtensions {
    /// <summary>
    /// Captures the <c>Idempotency-Key</c> (or legacy <c>X-Idempotency-Key</c>) HTTP header into the request
    /// scope for <c>[HttpEndpoint]</c> handlers. Place after authentication and <c>UseElarionCurrentUser()</c>.
    /// </summary>
    public static IApplicationBuilder UseElarionIdempotencyKey(this IApplicationBuilder app) =>
        app.UseMiddleware<IdempotencyKeyMiddleware>();
}
