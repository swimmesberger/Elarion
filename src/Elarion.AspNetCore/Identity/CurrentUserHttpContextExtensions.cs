using System.Security.Claims;
using Elarion.Abstractions.Dispatch;
using Microsoft.AspNetCore.Http;

namespace Elarion.AspNetCore.Identity;

/// <summary>
/// Seeds the request scope with the request's principal, so <c>ICurrentUser</c> and every handler dispatched in the
/// request scope see it.
/// </summary>
public static class CurrentUserHttpContextExtensions {
    /// <summary>
    /// Seeds the request scope with <see cref="HttpContext.User"/> through the registered dispatch-scope initializers,
    /// as <see cref="CurrentUserApplicationBuilderExtensions.UseElarionCurrentUser"/> does for every request.
    /// </summary>
    /// <remarks>
    /// <c>UseElarionCurrentUser</c> runs before routing and sees the principal of the default scheme. A route group that
    /// authenticates later — its own scheme, required by <c>RequireAuthorization</c> and run by the authorization
    /// middleware (HTTP Basic app passwords for a calendar endpoint) — replaces <see cref="HttpContext.User"/> after the
    /// scope was seeded, so its endpoint calls this first; seeding again replaces the earlier principal.
    /// </remarks>
    /// <example>
    /// <code>
    /// app.MapGroup("/sync")
    ///     .RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = "Basic" })
    ///     .MapGet("/{id}", async (HttpContext context, Guid id) =&gt; {
    ///         context.SeedElarionCurrentUser();   // ICurrentUser is now the Basic-authenticated caller
    ///         …
    ///     });
    /// </code>
    /// </example>
    /// <param name="context">The request.</param>
    public static void SeedElarionCurrentUser(this HttpContext context) {
        ArgumentNullException.ThrowIfNull(context);
        var dispatchContext = new DispatchScopeContext();
        dispatchContext.Set<ClaimsPrincipal>(context.User);
        context.RequestServices.SeedScope(dispatchContext);
    }
}
