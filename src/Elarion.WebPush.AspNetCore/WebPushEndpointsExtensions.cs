using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Elarion.Abstractions;
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
/// Subscriptions belong to the current user; without one the subscribe/unsubscribe endpoints answer
/// <c>401</c>, and a malformed or disallowed subscription <c>400</c>. Apply the host's authorization policy to
/// the returned group. Applications that expose their API as <c>[Handler]</c>s can skip this package and
/// delegate three thin handlers to <see cref="WebPushSubscriptionService"/> instead.
/// </summary>
public static class WebPushEndpointsExtensions {
    /// <summary>Maps the endpoints under <paramref name="prefix"/> (requires <c>AddElarionWebPush</c>).</summary>
    /// <param name="endpoints">The endpoint route builder to map onto.</param>
    /// <param name="prefix">The route prefix.</param>
    /// <returns>The route group, so the host can apply conventions (e.g. <c>.RequireAuthorization()</c>).</returns>
    /// <example>
    /// <code>
    /// app.MapElarionWebPush().RequireAuthorization();
    /// </code>
    /// </example>
    public static RouteGroupBuilder MapElarionWebPush(
        this IEndpointRouteBuilder endpoints, [StringSyntax("Route")] string prefix = "/webpush") {
        ArgumentNullException.ThrowIfNull(endpoints);
        var group = endpoints.MapGroup(prefix);
        // The AOT-safe RequestDelegate overloads: a typed delegate would route through the reflection-based
        // RequestDelegateFactory, which is broken under Native AOT for framework-owned call sites (ADR-0071).
        // The metadata restores the request/response shapes ApiExplorer would have inferred.
        group.MapGet("/public-key", (RequestDelegate)GetPublicKeyAsync)
            .WithMetadata(new ProducesResponseTypeMetadata(
                StatusCodes.Status200OK, typeof(WebPushPublicKeyResponse), ["application/json"]));
        group.MapPost("/subscribe", (RequestDelegate)SubscribeAsync)
            .WithMetadata(new AcceptsMetadata(["application/json"], typeof(PushSubscriptionRequest)))
            .WithMetadata(BodylessResponses());
        group.MapPost("/unsubscribe", (RequestDelegate)UnsubscribeAsync)
            .WithMetadata(new AcceptsMetadata(["application/json"], typeof(WebPushUnsubscribeRequest)))
            .WithMetadata(BodylessResponses());
        return group;
    }

    private static object[] BodylessResponses() {
        return [
            new ProducesResponseTypeMetadata(StatusCodes.Status204NoContent),
            new ProducesResponseTypeMetadata(StatusCodes.Status400BadRequest),
            new ProducesResponseTypeMetadata(StatusCodes.Status401Unauthorized)
        ];
    }

    private static async Task GetPublicKeyAsync(HttpContext context) {
        var service = context.RequestServices.GetRequiredService<WebPushSubscriptionService>();
        var publicKey = await service.GetPublicKeyAsync(context.RequestAborted);
        await context.Response.WriteAsJsonAsync(
            new WebPushPublicKeyResponse(publicKey),
            WebPushEndpointJsonContext.Default.WebPushPublicKeyResponse,
            cancellationToken: context.RequestAborted);
    }

    private static async Task SubscribeAsync(HttpContext context) {
        var request = await ReadBodyAsync(context, WebPushEndpointJsonContext.Default.PushSubscriptionRequest);
        if (request is null) {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var service = context.RequestServices.GetRequiredService<WebPushSubscriptionService>();
        var result = await service.SubscribeAsync(
            request, context.Request.Headers.UserAgent.ToString(), context.RequestAborted);
        context.Response.StatusCode = ToStatusCode(result);
    }

    private static async Task UnsubscribeAsync(HttpContext context) {
        var request = await ReadBodyAsync(context, WebPushEndpointJsonContext.Default.WebPushUnsubscribeRequest);
        if (request is null) {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var service = context.RequestServices.GetRequiredService<WebPushSubscriptionService>();
        var result = await service.UnsubscribeAsync(request.Endpoint ?? "", context.RequestAborted);
        context.Response.StatusCode = ToStatusCode(result);
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
    private static int ToStatusCode(Result result) {
        if (result.IsSuccess) return StatusCodes.Status204NoContent;
        return result.Error.Kind switch {
            ErrorKind.Unauthorized => StatusCodes.Status401Unauthorized,
            ErrorKind.Forbidden => StatusCodes.Status403Forbidden,
            ErrorKind.Validation => StatusCodes.Status400BadRequest,
            _ => StatusCodes.Status500InternalServerError
        };
    }
}
