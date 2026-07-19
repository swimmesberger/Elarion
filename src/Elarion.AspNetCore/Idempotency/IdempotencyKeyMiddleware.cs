using System.Security.Claims;
using Elarion.Abstractions.Dispatch;
using Elarion.Abstractions.Idempotency;
using Microsoft.AspNetCore.Http;

namespace Elarion.AspNetCore.Idempotency;

/// <summary>
/// Captures the HTTP idempotency key (<c>Idempotency-Key</c>, or the legacy <c>X-Idempotency-Key</c>) into the
/// request scope so HTTP <c>[HttpEndpoint]</c> handlers — which run in the request scope — see it through
/// <see cref="IIdempotencyKeyAccessor"/>. Mirrors <c>CurrentUserMiddleware</c>: it seeds the request scope via
/// the same dispatch-scope rail every transport uses.
/// </summary>
/// <remarks>
/// Because seeding runs every <see cref="IDispatchScopeInitializer"/>, the captured context also carries the
/// authenticated principal so the current-user snapshot is preserved across the re-seed. Register
/// <c>UseElarionIdempotencyKey()</c> after authentication (and after <c>UseElarionCurrentUser()</c>).
/// </remarks>
public sealed class IdempotencyKeyMiddleware(RequestDelegate next) {
    /// <summary>Seeds the idempotency key for the active request scope, when one is present.</summary>
    public async Task InvokeAsync(HttpContext context) {
        var key = ReadKey(context.Request);
        if (key is not null) {
            var dispatchContext = new DispatchScopeContext();
            dispatchContext.Set<ClaimsPrincipal>(context.User);
            dispatchContext.Set<IdempotencyKey>(new IdempotencyKey(key));
            context.RequestServices.SeedScope(dispatchContext);
        }

        await next(context);
    }

    private static string? ReadKey(HttpRequest request) {
        if (request.Headers.TryGetValue(IdempotencyKeyNames.HttpHeader, out var value) &&
            value.Count > 0 && !string.IsNullOrWhiteSpace(value[0]))
            return value[0];

        if (request.Headers.TryGetValue(IdempotencyKeyNames.LegacyHttpHeader, out var legacy) &&
            legacy.Count > 0 && !string.IsNullOrWhiteSpace(legacy[0]))
            return legacy[0];

        return null;
    }
}
