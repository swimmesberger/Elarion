using Microsoft.AspNetCore.Http;

namespace Elarion.AspNetCore.Identity;

/// <summary>
/// Copies the authenticated ASP.NET principal into the scoped framework current-user snapshot.
/// </summary>
public sealed class CurrentUserMiddleware(RequestDelegate next) {
    /// <summary>Initializes the current-user snapshot for the active request.</summary>
    public async Task InvokeAsync(HttpContext context, CurrentUserSnapshot currentUser) {
        currentUser.Initialize(context.User);

        await next(context);
    }
}
