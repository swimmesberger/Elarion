using System.Diagnostics.CodeAnalysis;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
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
/// <see cref="IClientEventSubscriptionAuthorizer"/> → 404, so a topic's existence is never leaked (a topic
/// declaring <c>AllowAnyResource</c> skips the authorizer — its resource segment is a routing key). User
/// scope is always the caller's own. Delivery is at-most-once.
/// </para>
/// </remarks>
public static class ClientEventEndpointsExtensions {
    private const string ConnectedEventType = ClientEventControlEvents.Connected;
    private const string KeepAliveEventType = ClientEventControlEvents.KeepAlive;

    // The ordered-streams endpoint (Elarion.AspNetCore/Streams/StreamEndpointRouteBuilderExtensions)
    // mirrors this keep-alive wire contract (event name + interval) and the WaitForNextAsync pattern —
    // deliberately duplicated, no shared home below ASP.NET. Change one → change the other.
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
        // Pre-checked here (the resolver checks too) so an unauthenticated caller with a malformed
        // subscriptions parameter still gets 401, not 400 — response precedence is part of the contract.
        var currentUser = services.GetService<ICurrentUser>();
        if (currentUser is null || !currentUser.IsAuthenticated) return Results.Unauthorized();

        // Bodyless 400s: response bodies would go through the host's HTTP JSON options, which this endpoint
        // must not require (the repo default runs with reflection-based serialization off).
        if (string.IsNullOrEmpty(subscriptions)) return Results.BadRequest();

        ClientEventSubscriptionRequest[]? requests;
        try {
            requests = JsonSerializer.Deserialize(
                subscriptions, ClientEventEndpointJsonContext.Default.ClientEventSubscriptionRequestArray);
        }
        catch (JsonException) {
            return Results.BadRequest();
        }

        if (requests is null) return Results.BadRequest();

        // The subscribe pipeline (catalog lookup, fail-closed authorization, scope expansion) is the shared
        // transport-neutral resolver, so this endpoint and connection adapters can never drift apart.
        var resolver = services.GetRequiredService<ClientEventSubscriptionResolver>();
        var resolution = await resolver.ResolveAsync(requests, cancellationToken);
        switch (resolution.Status) {
            case ClientEventSubscriptionStatus.Unauthenticated:
                return Results.Unauthorized();
            case ClientEventSubscriptionStatus.InvalidRequest:
                return Results.BadRequest();
            case ClientEventSubscriptionStatus.NotFound:
                return Results.NotFound();
        }

        var source = services.GetRequiredService<IClientEventSubscriptionSource>();
        return TypedResults.ServerSentEvents(
            StreamAsync(source.Subscribe(resolution.Subscriptions), context.RequestAborted));
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
            if (signal == StreamSignal.Completed) yield break;
            if (signal == StreamSignal.KeepAlive) {
                yield return new SseItem<string>(string.Empty, KeepAliveEventType);
                continue;
            }

            while (reader.TryRead(out var envelope))
                yield return new SseItem<string>(envelope.Payload, envelope.Topic) {
                    EventId = envelope.Id.ToString()
                };
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
            if (winner != pendingRead) return (StreamSignal.KeepAlive, pendingRead);
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
        Completed
    }
}
