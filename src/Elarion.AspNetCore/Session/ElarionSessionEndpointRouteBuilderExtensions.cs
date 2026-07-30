using Elarion.Abstractions;
using Elarion.Session;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Elarion.AspNetCore;

/// <summary>
/// Maps the framework-shipped client-capability bootstrap onto a REST endpoint. Like every framework-owned
/// registration, it uses the AOT-safe <c>Map*(string, RequestDelegate)</c> overload: ASP.NET Core's Request
/// Delegate Generator cannot see call sites inside a compiled framework package, so a typed-lambda mapping
/// would silently fall back to the reflection-based <c>RequestDelegateFactory</c>. See <c>ADR-0071</c>.
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
    public static IEndpointConventionBuilder MapElarionSession(
        this IEndpointRouteBuilder endpoints, string route = "/session") {
        ArgumentNullException.ThrowIfNull(endpoints);
        return endpoints.MapGet(route, (RequestDelegate)HandleAsync)
            .WithName("ElarionSession")
            .WithDescription("Returns the client-capability snapshot for the current user and deployment.")
            .WithMetadata(new ProducesResponseTypeMetadata(200, typeof(SessionResponse), ["application/json"]))
            .ProducesElarionErrors()
            // ApiExplorer describes an endpoint only when a MethodInfo rides its metadata; the shape method
            // mirrors the classic typed signature so the OpenAPI document stays identical (ADR-0071).
            .WithMetadata(
                ((Func<IHandler<SessionRequest, Result<SessionResponse>>, CancellationToken, Task<IResult>>)
                    ApiShape).Method);
    }

    private static async Task HandleAsync(HttpContext context) {
        var handler = context.RequestServices
            .GetRequiredService<IHandler<SessionRequest, Result<SessionResponse>>>();
        var result = ElarionHttpResults.ToResult(
            await handler.HandleAsync(new SessionRequest(), context.RequestAborted));
        await result.ExecuteAsync(context);
    }

    /// <summary>
    /// OpenAPI shape for 'ElarionSession' — never invoked; ApiExplorer reads parameters from this signature.
    /// </summary>
    private static Task<IResult> ApiShape(
        [FromServices] IHandler<SessionRequest, Result<SessionResponse>> handler,
        CancellationToken cancellationToken) {
        throw new NotSupportedException("OpenAPI metadata shape only; requests execute through the RequestDelegate.");
    }
}
