using Microsoft.AspNetCore.Builder;

namespace Elarion.AspNetCore.Identity;

/// <summary>
/// Middleware registration helpers for ASP.NET-backed current-user access.
/// </summary>
public static class CurrentUserApplicationBuilderExtensions {
    /// <summary>
    /// Copies <see cref="Microsoft.AspNetCore.Http.HttpContext.User"/> into the scoped framework
    /// current-user snapshot for downstream handlers and services.
    /// </summary>
    public static IApplicationBuilder UseElarionCurrentUser(this IApplicationBuilder app) =>
        app.UseMiddleware<CurrentUserMiddleware>();
}
