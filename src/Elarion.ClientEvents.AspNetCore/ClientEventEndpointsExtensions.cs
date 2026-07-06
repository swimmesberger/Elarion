using System.Diagnostics.CodeAnalysis;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Elarion.Abstractions.Authorization;
using Elarion.Abstractions.ClientEvents;
using Elarion.Abstractions.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Elarion.ClientEvents.AspNetCore;

/// <summary>
/// Maps the client-event Server-Sent Events endpoint: one <c>GET</c> connection multiplexing the caller's
/// authorized subscriptions, each event framed as <c>event: {topic}</c> / <c>data: {canonical JSON}</c> via
/// the framework's native SSE result. Control signals are named <c>elarion.*</c> events:
/// <c>elarion.connected</c> once the stream is live (treat every occurrence as "you may have missed events" —
/// re-query), and <c>elarion.keepAlive</c> on idle so proxies keep the connection open.
/// </summary>
/// <remarks>
/// <para>
/// Subscriptions arrive as the <c>subscriptions</c> query parameter — a URL-encoded JSON array of
/// <c>{"topic":"…","resource":"…?"}</c> — because <c>EventSource</c> cannot POST; changing the subscription
/// set means reconnecting. Authorization is <b>fail-closed</b> at subscribe time: unauthenticated → 401;
/// an unknown topic, a failed topic requirement, or a resource scope without a passing
/// <see cref="IClientEventSubscriptionAuthorizer"/> → 404, so a topic's existence is never leaked. User
/// scope is always the caller's own. Delivery is at-most-once.
/// </para>
/// </remarks>
public static class ClientEventEndpointsExtensions {
    private const string ConnectedEventType = "elarion.connected";
    private const string KeepAliveEventType = "elarion.keepAlive";

    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Maps the SSE endpoint at <paramref name="pattern"/> (requires <c>AddElarionClientEvents</c>).
    /// </summary>
    /// <param name="endpoints">The endpoint route builder to map onto.</param>
    /// <param name="pattern">The route pattern.</param>
    /// <returns>The route builder, so the host can apply conventions (e.g. <c>.RequireAuthorization()</c>).</returns>
    public static RouteHandlerBuilder MapElarionClientEvents(
        this IEndpointRouteBuilder endpoints, [StringSyntax("Route")] string pattern = "/events") {
        ArgumentNullException.ThrowIfNull(endpoints);
        return endpoints.MapGet(pattern, HandleAsync);
    }

    private static async Task<IResult> HandleAsync(
        HttpContext context, string? subscriptions, CancellationToken cancellationToken) {
        var services = context.RequestServices;
        var currentUser = services.GetService<ICurrentUser>();
        if (currentUser is null || !currentUser.IsAuthenticated) {
            return Results.Unauthorized();
        }

        // Bodyless 400s: response bodies would go through the host's HTTP JSON options, which this endpoint
        // must not require (the repo default runs with reflection-based serialization off).
        if (string.IsNullOrEmpty(subscriptions)) {
            return Results.BadRequest();
        }

        ClientEventSubscriptionRequest[]? requests;
        try {
            requests = JsonSerializer.Deserialize(
                subscriptions, ClientEventEndpointJsonContext.Default.ClientEventSubscriptionRequestArray);
        }
        catch (JsonException) {
            return Results.BadRequest();
        }

        if (requests is null || requests.Length == 0) {
            return Results.BadRequest();
        }

        var catalog = services.GetRequiredService<ClientEventTopicCatalog>();
        var resolved = new List<ClientEventSubscription>(requests.Length + 1);
        var authorizedTopics = new HashSet<string>(StringComparer.Ordinal);

        foreach (var request in requests) {
            if (string.IsNullOrEmpty(request.Topic)) {
                return Results.BadRequest();
            }

            // Unknown, disabled, and denied topics are indistinguishable from the outside: not found.
            var topic = catalog.FindByName(request.Topic);
            if (topic is null) {
                return Results.NotFound();
            }

            if (authorizedTopics.Add(topic.Name)) {
                var denied = await AuthorizeTopicAsync(services, topic.Requirements, cancellationToken);
                if (denied is not null) {
                    return denied;
                }
            }

            if (request.Resource is null) {
                resolved.Add(new ClientEventSubscription { Topic = topic.Name, Scope = ClientEventScope.Global });
                if (currentUser.UserId is { Length: > 0 } userId) {
                    resolved.Add(new ClientEventSubscription { Topic = topic.Name, Scope = ClientEventScope.User(userId) });
                }
                continue;
            }

            var subscription = new ClientEventSubscription {
                Topic = topic.Name,
                Scope = ClientEventScope.Resource(request.Resource),
            };
            // Resource scopes are fail-closed: no registered authorizer denies, and denial reads as not found.
            var authorizer = services.GetService<IClientEventSubscriptionAuthorizer>();
            if (authorizer is null || !await authorizer.AuthorizeAsync(subscription, cancellationToken)) {
                return Results.NotFound();
            }
            resolved.Add(subscription);
        }

        var source = services.GetRequiredService<IClientEventSubscriptionSource>();
        return TypedResults.ServerSentEvents(StreamAsync(source.Subscribe(resolved), context.RequestAborted));
    }

    private static async ValueTask<IResult?> AuthorizeTopicAsync(
        IServiceProvider services, AuthorizationRequirements requirements, CancellationToken ct) {
        // "Authenticated" is already established for the whole request; only richer requirements need the
        // IAuthorizer. Requirements with no evaluator fail closed.
        var beyondAuthenticated = requirements.Permissions.Count > 0 || requirements.Roles.Count > 0
            || requirements.Claims.Count > 0 || requirements.Policies.Count > 0 || requirements.Resources.Count > 0;
        if (!beyondAuthenticated) {
            return null;
        }

        var authorizer = services.GetService<IAuthorizer>();
        if (authorizer is null) {
            return Results.NotFound();
        }

        var error = await authorizer.AuthorizeAsync(requirements, resource: null, ct);
        return error is null ? null : Results.NotFound();
    }

    /// <summary>
    /// Adapts the subscription's channel to the SSE item stream: payloads are already canonical JSON, so
    /// items are <see cref="SseItem{T}"/> of <see cref="string"/> (written as-is, never re-serialized).
    /// Owns the handle for the connection's lifetime.
    /// </summary>
    private static async IAsyncEnumerable<SseItem<string>> StreamAsync(
        ClientEventSubscriptionHandle handle, [EnumeratorCancellation] CancellationToken ct) {
        using var subscription = handle;
        yield return new SseItem<string>(string.Empty, ConnectedEventType);

        var reader = subscription.Events;
        Task<bool>? pendingRead = null;
        while (!ct.IsCancellationRequested) {
            StreamSignal signal;
            (signal, pendingRead) = await WaitForNextAsync(reader, pendingRead, ct);
            if (signal == StreamSignal.Completed) {
                yield break;
            }
            if (signal == StreamSignal.KeepAlive) {
                yield return new SseItem<string>(string.Empty, KeepAliveEventType);
                continue;
            }

            while (reader.TryRead(out var envelope)) {
                yield return new SseItem<string>(envelope.Payload, envelope.Topic) {
                    EventId = envelope.Id.ToString(),
                };
            }
        }
    }

    // yield is illegal inside try/catch, so the waiting (and its cancellation handling) lives here. The
    // pending read is threaded through because a keep-alive tick must not abandon it: a second concurrent
    // WaitToReadAsync on a single-reader channel is invalid.
    private static async Task<(StreamSignal Signal, Task<bool>? PendingRead)> WaitForNextAsync(
        ChannelReader<ClientEventEnvelope> reader, Task<bool>? pendingRead, CancellationToken ct) {
        try {
            pendingRead ??= reader.WaitToReadAsync(ct).AsTask();
            using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var winner = await Task.WhenAny(pendingRead, Task.Delay(KeepAliveInterval, delayCts.Token));
            if (winner != pendingRead) {
                return (StreamSignal.KeepAlive, pendingRead);
            }
            delayCts.Cancel();
            return (await pendingRead ? StreamSignal.Event : StreamSignal.Completed, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            // The client disconnected — the normal end of an SSE stream, not an error.
            return (StreamSignal.Completed, null);
        }
    }

    private enum StreamSignal {
        Event,
        KeepAlive,
        Completed,
    }
}
