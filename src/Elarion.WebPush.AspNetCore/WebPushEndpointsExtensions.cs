using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Elarion.Abstractions;
using Elarion.Abstractions.Dispatch;
using Elarion.Abstractions.Results;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Elarion.WebPush.AspNetCore;

/// <summary>
/// Maps the Web Push subscription endpoints the <c>@swimmesberger/elarion-webpush</c> browser package's
/// default transport calls:
/// <list type="bullet">
/// <item><c>GET {prefix}/public-key</c> → <c>200 {"publicKey":"…"}</c></item>
/// <item><c>POST {prefix}/subscribe</c> with <c>subscription.toJSON()</c> → <c>204</c></item>
/// <item><c>POST {prefix}/unsubscribe</c> with <c>{"endpoint":"…"}</c> → <c>204</c></item>
/// </list>
/// The endpoints are HTTP bindings for three <b>handlers the application declares</b>, not an implementation:
/// each request is dispatched to the <see cref="IHandler{TRequest,TResponse}"/> registered for
/// <see cref="PushSubscriptionRequest"/>, <see cref="WebPushUnsubscribeRequest"/> and
/// <see cref="WebPushPublicKeyRequest"/>, so the full handler pipeline applies — global authorization rules,
/// tenant and realm rules, audit, rate limiting, validation. A handler's failure maps to a bodyless status
/// (<c>400</c> invalid, <c>401</c> anonymous, <c>403</c> forbidden); a malformed body is answered <c>401</c>/<c>403</c>
/// by the handler's admission gate when the caller may not call it at all, otherwise <c>400</c>. The handlers
/// usually delegate to <see cref="WebPushSubscriptionService"/>. <see cref="MapElarionWebPush"/> fails at
/// startup, naming what is missing, when one of the three handlers is not registered — there is no undecorated
/// fallback to bypass the pipeline by accident. Apply endpoint-level conventions
/// (<c>.RequireAuthorization()</c>) to the returned group as for any minimal API.
/// </summary>
public static class WebPushEndpointsExtensions {
    /// <summary>
    /// Maps the endpoints under <paramref name="prefix"/> (requires <c>AddElarionWebPush</c> and the three
    /// application handlers).
    /// </summary>
    /// <param name="endpoints">The endpoint route builder to map onto.</param>
    /// <param name="prefix">The route prefix.</param>
    /// <returns>The route group, so the host can apply conventions (e.g. <c>.RequireAuthorization()</c>).</returns>
    /// <exception cref="InvalidOperationException">A required handler is not registered.</exception>
    /// <example>
    /// <code>
    /// app.MapElarionWebPush().RequireAuthorization();
    /// </code>
    /// </example>
    public static RouteGroupBuilder MapElarionWebPush(
        this IEndpointRouteBuilder endpoints, [StringSyntax("Route")] string prefix = "/webpush") {
        ArgumentNullException.ThrowIfNull(endpoints);
        EnsureHandlersRegistered(endpoints.ServiceProvider);
        var group = endpoints.MapGroup(prefix);
        // The AOT-safe RequestDelegate overloads: a typed delegate would route through the reflection-based
        // RequestDelegateFactory, which is broken under Native AOT for framework-owned call sites (ADR-0071).
        // The metadata restores the request/response shapes ApiExplorer would have inferred.
        group.MapGet("/public-key", (RequestDelegate)GetPublicKeyAsync)
            .WithMetadata(new ProducesResponseTypeMetadata(
                StatusCodes.Status200OK, typeof(WebPushPublicKeyResponse), ["application/json"]))
            .WithMetadata(new ProducesResponseTypeMetadata(StatusCodes.Status401Unauthorized))
            .WithMetadata(new ProducesResponseTypeMetadata(StatusCodes.Status403Forbidden));
        group.MapPost("/subscribe", (RequestDelegate)SubscribeAsync)
            .WithMetadata(new AcceptsMetadata(["application/json"], typeof(PushSubscriptionRequest)))
            .WithMetadata(BodylessResponses());
        group.MapPost("/unsubscribe", (RequestDelegate)UnsubscribeAsync)
            .WithMetadata(new AcceptsMetadata(["application/json"], typeof(WebPushUnsubscribeRequest)))
            .WithMetadata(BodylessResponses());
        return group;
    }

    private static void EnsureHandlersRegistered(IServiceProvider services) {
        var probe = services.GetService<IServiceProviderIsService>();
        if (probe is null) return;

        var missing = new List<string>();
        if (!probe.IsService(typeof(IHandler<PushSubscriptionRequest, Result<Unit>>)))
            missing.Add("IHandler<PushSubscriptionRequest> (subscribe)");
        if (!probe.IsService(typeof(IHandler<WebPushUnsubscribeRequest, Result<Unit>>)))
            missing.Add("IHandler<WebPushUnsubscribeRequest> (unsubscribe)");
        if (!probe.IsService(typeof(IHandler<WebPushPublicKeyRequest, Result<WebPushPublicKeyResponse>>)))
            missing.Add("IHandler<WebPushPublicKeyRequest, Result<WebPushPublicKeyResponse>> (public key)");
        if (missing.Count == 0) return;

        throw new InvalidOperationException(
            "MapElarionWebPush dispatches to handlers the application declares, so the handler pipeline (authorization "
            + "rules, audit, rate limiting) applies to the Web Push endpoints, but no handler is registered for: "
            + string.Join("; ", missing)
            + ". Declare [Handler]s that delegate to WebPushSubscriptionService (see the Web Push capability page).");
    }

    private static object[] BodylessResponses() {
        return [
            new ProducesResponseTypeMetadata(StatusCodes.Status204NoContent),
            new ProducesResponseTypeMetadata(StatusCodes.Status400BadRequest),
            new ProducesResponseTypeMetadata(StatusCodes.Status401Unauthorized),
            new ProducesResponseTypeMetadata(StatusCodes.Status403Forbidden)
        ];
    }

    private static async Task GetPublicKeyAsync(HttpContext context) {
        var handler = context.RequestServices
            .GetRequiredService<IHandler<WebPushPublicKeyRequest, Result<WebPushPublicKeyResponse>>>();
        var result = await handler.HandleAsync(new WebPushPublicKeyRequest(), context.RequestAborted);
        if (!result.IsSuccess) {
            context.Response.StatusCode = ToStatusCode(result.Error);
            return;
        }

        await context.Response.WriteAsJsonAsync(
            result.Value,
            WebPushEndpointJsonContext.Default.WebPushPublicKeyResponse,
            cancellationToken: context.RequestAborted);
    }

    private static async Task SubscribeAsync(HttpContext context) {
        var request = await ReadBodyAsync(context, WebPushEndpointJsonContext.Default.PushSubscriptionRequest);
        if (request is null) {
            await RejectUnboundAsync<PushSubscriptionRequest>(context);
            return;
        }

        var handler = context.RequestServices.GetRequiredService<IHandler<PushSubscriptionRequest, Result<Unit>>>();
        var result = await handler.HandleAsync(
            request with { UserAgent = context.Request.Headers.UserAgent.ToString() }, context.RequestAborted);
        context.Response.StatusCode = result.IsSuccess ? StatusCodes.Status204NoContent : ToStatusCode(result.Error);
    }

    private static async Task UnsubscribeAsync(HttpContext context) {
        var request = await ReadBodyAsync(context, WebPushEndpointJsonContext.Default.WebPushUnsubscribeRequest);
        if (request is null) {
            await RejectUnboundAsync<WebPushUnsubscribeRequest>(context);
            return;
        }

        var handler = context.RequestServices.GetRequiredService<IHandler<WebPushUnsubscribeRequest, Result<Unit>>>();
        var result = await handler.HandleAsync(request, context.RequestAborted);
        context.Response.StatusCode = result.IsSuccess ? StatusCodes.Status204NoContent : ToStatusCode(result.Error);
    }

    // A body that does not bind is a 400 — unless the caller may not call the handler at all, in which case the
    // handler's admission gate answers 401/403 first, so an anonymous caller never learns the request shape.
    private static async Task RejectUnboundAsync<TRequest>(HttpContext context) {
        var denial = await HandlerGates.EvaluateAsync<TRequest>(context.RequestServices, context.RequestAborted);
        context.Response.StatusCode = denial is null ? StatusCodes.Status400BadRequest : ToStatusCode(denial);
    }

    private static async Task<T?> ReadBodyAsync<T>(HttpContext context, JsonTypeInfo<T> typeInfo) where T : class {
        if (!context.Request.HasJsonContentType()) return null;
        try {
            return await context.Request.ReadFromJsonAsync(typeInfo, context.RequestAborted);
        }
        catch (JsonException) {
            return null;
        }
    }

    // Bodyless errors, like the client-events endpoint: a ProblemDetails body would go through the host's
    // HTTP JSON options, which these endpoints must not require.
    private static int ToStatusCode(AppError error) {
        return error.Kind switch {
            ErrorKind.Unauthorized => StatusCodes.Status401Unauthorized,
            ErrorKind.Forbidden => StatusCodes.Status403Forbidden,
            ErrorKind.Validation => StatusCodes.Status400BadRequest,
            ErrorKind.NotFound => StatusCodes.Status404NotFound,
            ErrorKind.Conflict => StatusCodes.Status409Conflict,
            ErrorKind.BusinessRule => StatusCodes.Status422UnprocessableEntity,
            _ => StatusCodes.Status500InternalServerError
        };
    }
}
