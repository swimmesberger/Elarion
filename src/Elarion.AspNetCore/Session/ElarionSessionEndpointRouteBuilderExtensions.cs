using Elarion.Abstractions;
using Elarion.Session;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Elarion.AspNetCore;

/// <summary>
/// Maps the framework-shipped client-capability bootstrap onto a REST endpoint. This is a deliberately
/// <b>concrete</b>, hand-authored mapping (not a generic helper) so ASP.NET Core's Request Delegate Generator keeps
/// it Native-AOT and trim-safe — the same reason <c>[HttpEndpoint]</c> handlers emit concrete lambdas. See
/// <c>ADR-0031</c>.
/// </summary>
public static class ElarionSessionEndpointRouteBuilderExtensions {
    /// <summary>
    /// Maps <c>GET {route}</c> (default <c>/session</c>) to the session bootstrap, returning the
    /// <see cref="SessionResponse"/> snapshot for the current user. Register the handler with
    /// <c>AddElarionSession(...)</c> first.
    /// </summary>
    /// <example>
    /// <code>
    /// app.MapElarionSession();              // GET /session
    /// app.MapElarionSession("/api/session").RequireAuthorization();
    /// </code>
    /// </example>
    public static RouteHandlerBuilder MapElarionSession(
        this IEndpointRouteBuilder endpoints, string route = "/session") {
        ArgumentNullException.ThrowIfNull(endpoints);
        return endpoints.MapGet(
                route,
                static async (
                        [FromServices] IHandler<SessionRequest, Result<SessionResponse>> handler,
                        CancellationToken ct) =>
                    ElarionHttpResults.ToResult(await handler.HandleAsync(new SessionRequest(), ct)))
            .WithName("ElarionSession")
            .WithDescription("Returns the client-capability snapshot for the current user and deployment.")
            .Produces<SessionResponse>(200)
            .ProducesElarionErrors();
    }
}
