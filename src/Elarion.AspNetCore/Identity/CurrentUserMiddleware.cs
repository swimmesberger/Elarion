using System.Security.Claims;
using Elarion.JsonRpc;
using Microsoft.AspNetCore.Http;

namespace Elarion.AspNetCore.Identity;

/// <summary>
/// Captures the authenticated ASP.NET principal into the request scope so HTTP <c>[HttpEndpoint]</c> handlers
/// — which run in the request scope, not a dispatch child scope — see it through <c>ICurrentUser</c>.
/// </summary>
/// <remarks>
/// Seeds via the same dispatch-scope rail every transport uses: it puts <see cref="HttpContext.User"/> into a
/// <see cref="DispatchScopeContext"/> and runs the registered <see cref="IDispatchScopeInitializer"/> instances
/// against the request scope. JSON-RPC and MCP capture their principal the same way into a fresh per-call scope.
/// </remarks>
public sealed class CurrentUserMiddleware(RequestDelegate next) {
    /// <summary>Seeds the current-user state for the active request scope.</summary>
    public async Task InvokeAsync(HttpContext context) {
        var dispatchContext = new DispatchScopeContext();
        dispatchContext.Set<ClaimsPrincipal>(context.User);
        context.RequestServices.SeedScope(dispatchContext);

        await next(context);
    }
}
