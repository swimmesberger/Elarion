using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Elarion;
using Elarion.Abstractions;
using Elarion.Abstractions.Dispatch;
using Elarion.Abstractions.Serialization;
using Elarion.Streams;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Elarion.AspNetCore.Streams;

/// <summary>
/// Maps an ordered stream (ADR-0052) as a Server-Sent-Events endpoint: each element's canonical JSON is
/// one SSE event whose <c>id:</c> is the <see cref="StreamItem{T}.Sequence"/>, so the browser's automatic
/// <c>Last-Event-ID</c> reconnect header resumes exactly where the connection dropped (the hub replays
/// from its ring; a gap that outran the ring shows as a sequence jump, never a silent hole). The rest of
/// the path is one TCP connection — ordered end-to-end with zero extra infrastructure. Route the pattern
/// to the producer's node (the role-holder proxy / your ingress) in multi-node deployments.
/// </summary>
public static class StreamEndpointRouteBuilderExtensions {
    // Mirrors ClientEventControlEvents.KeepAlive + the client-events endpoint's interval
    // (Elarion.ClientEvents.AspNetCore/ClientEventEndpointsExtensions) — one keep-alive wire contract
    // across both SSE surfaces. Deliberately duplicated: the packages share no home below ASP.NET
    // (referencing Elarion.AspNetCore would drag Elarion.JsonRpc into the client-events transport).
    // Change one → change the other.
    private const string KeepAliveEventType = "elarion.keepAlive";
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Maps a GET SSE endpoint over an ordered stream. The subscribe delegate receives the resume point —
    /// the reconnect's <c>Last-Event-ID</c> header, or an explicit <c>?after=</c> query — and returns the
    /// subscription, typically an actor stream method
    /// (<c>actors.Get&lt;IStockQuote&gt;(symbol).Watch(resumeAfter)</c>); <see langword="null"/> → 404.
    /// Authorization is the host's job: check inside the delegate or chain <c>.RequireAuthorization()</c>.
    /// </summary>
    /// <typeparam name="T">The element type; must be in a registered JSON source-gen context.</typeparam>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="pattern">The route pattern (route values are read from the <see cref="HttpContext"/>).</param>
    /// <param name="subscribe">Creates the subscription for one connection, or <see langword="null"/> for 404.</param>
    public static RouteHandlerBuilder MapElarionStream<T>(
        this IEndpointRouteBuilder endpoints,
        [StringSyntax("Route")] string pattern,
        Func<HttpContext, long?, IAsyncEnumerable<StreamItem<T>>?> subscribe) {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(subscribe);

        return endpoints.MapGet(pattern, (HttpContext context) => {
            var stream = subscribe(context, ResumePoint(context.Request));
            if (stream is null) {
                return Results.NotFound();
            }

            var typeInfo = context.RequestServices.GetRequiredService<IElarionJsonSerialization>().GetTypeInfo<T>();
            return (IResult)TypedResults.ServerSentEvents(
                StreamAsync(stream, typeInfo, GetTimeProvider(context), context.RequestAborted));
        });
    }

    /// <summary>
    /// Maps a request-driven <see cref="IStreamHandler{TRequest,TItem}"/> as SSE. The host owns binding,
    /// routing, and authorization through <paramref name="requestFactory"/>; this adapter owns only the typed
    /// stream invocation and canonical JSON wire leg. Startup failures are written as normal Elarion problems
    /// before SSE headers are committed.
    /// </summary>
    /// <typeparam name="TRequest">The handler request type.</typeparam>
    /// <typeparam name="TItem">The streamed item type, present in a canonical source-generated JSON context.</typeparam>
    public static RouteHandlerBuilder MapElarionHandlerStream<TRequest, TItem>(
        this IEndpointRouteBuilder endpoints,
        [StringSyntax("Route")] string pattern,
        Func<HttpContext, CancellationToken, ValueTask<TRequest>> requestFactory)
        where TRequest : notnull {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(requestFactory);

        return (RouteHandlerBuilder)endpoints.MapGet(pattern, async context => {
            var request = await requestFactory(context, context.RequestAborted).ConfigureAwait(false);
            var dispatch = new DispatchScopeContext();
            dispatch.Set<ClaimsPrincipal>(context.User);
            var started = await StreamHandlerInvoker.InvokeAsync<TRequest, TItem>(
                context.RequestServices, request, dispatch, context.RequestAborted).ConfigureAwait(false);
            if (!started.IsSuccess) {
                await ElarionHttpResults.ToProblem(started.Error).ExecuteAsync(context).ConfigureAwait(false);
                return;
            }

            await using var invocation = started.Value;
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";
            var typeInfo = context.RequestServices.GetRequiredService<IElarionJsonSerialization>().GetTypeInfo<TItem>();
            try {
                await WriteHandlerStreamAsync(context.Response, invocation, typeInfo, GetTimeProvider(context), context.RequestAborted)
                    .ConfigureAwait(false);
            } catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) {
                // Browser disconnects are normal termination; the invocation finally releases the stream scope.
            } catch {
                // SSE cannot change to an HTTP problem once items have been written. Abort to make the terminal
                // fault visible to the client instead of falsely signalling a clean completion.
                context.Abort();
            }
        });
    }

    // SSE data is line-oriented. WriteIndented JSON contains newlines, and an unprefixed continuation line would
    // terminate the event or be interpreted as a field by an EventSource client. Prefix every serialized line;
    // accept either LF or CRLF from a caller-configured canonical JsonSerializerOptions instance.
    private static async ValueTask WriteSseDataAsync(
        HttpResponse response,
        string payload,
        CancellationToken ct,
        string? eventType = null) {
        if (eventType is not null) {
            await response.WriteAsync("event: ", ct).ConfigureAwait(false);
            await response.WriteAsync(eventType, ct).ConfigureAwait(false);
            await response.WriteAsync("\n", ct).ConfigureAwait(false);
        }

        var offset = 0;
        while (offset <= payload.Length) {
            var newline = payload.IndexOfAny(['\r', '\n'], offset);
            var end = newline < 0 ? payload.Length : newline;
            await response.WriteAsync("data: ", ct).ConfigureAwait(false);
            await response.WriteAsync(payload.Substring(offset, end - offset), ct).ConfigureAwait(false);
            await response.WriteAsync("\n", ct).ConfigureAwait(false);

            if (newline < 0)
                break;

            offset = newline + 1;
            if (payload[newline] == '\r' && offset < payload.Length && payload[offset] == '\n')
                offset++;
        }

        await response.WriteAsync("\n", ct).ConfigureAwait(false);
    }

    private static long? ResumePoint(HttpRequest request) {
        // EventSource re-sends the last seen id automatically on reconnect; `?after=` is the manual form.
        string? raw = request.Headers["Last-Event-ID"];
        raw ??= request.Query["after"];
        return long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var sequence)
            ? sequence
            : null;
    }

    private static async IAsyncEnumerable<SseItem<string>> StreamAsync<T>(
        IAsyncEnumerable<StreamItem<T>> source,
        JsonTypeInfo<T> typeInfo,
        TimeProvider timeProvider,
        [EnumeratorCancellation] CancellationToken ct) {
        using var enumerationCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await using var enumerator = source.GetAsyncEnumerator(enumerationCts.Token);
        Task<bool>? pendingMove = null;
        try {
            while (true) {
                pendingMove ??= enumerator.MoveNextAsync().AsTask();
                var wait = await WaitForNextAsync(pendingMove, timeProvider, ct).ConfigureAwait(false);
                if (wait == MoveWaitResult.Cancelled)
                    yield break;
                if (wait == MoveWaitResult.KeepAlive) {
                    yield return new SseItem<string>(string.Empty, KeepAliveEventType);
                    continue;
                }

                var hasNext = await pendingMove.ConfigureAwait(false);
                pendingMove = null;
                if (!hasNext)
                    yield break;

                var item = enumerator.Current;
                yield return new SseItem<string>(JsonSerializer.Serialize(item.Value, typeInfo)) {
                    EventId = item.Sequence.ToString(CultureInfo.InvariantCulture),
                };
            }
        } finally {
            // A response-writer failure can dispose this iterator while a keep-alive leaves MoveNext pending.
            // Cancel and settle that exact move before the await-using disposes the enumerator: concurrent
            // MoveNextAsync/DisposeAsync calls are outside the IAsyncEnumerator contract.
            await SettlePendingMoveAsync(pendingMove, enumerationCts).ConfigureAwait(false);
        }
    }

    // yield is illegal inside try/catch, so the waiting lives here. The caller owns the one pending MoveNext
    // across keep-alive ticks: starting a second concurrent move or disposing before that move settles is invalid.
    // The enumerator-shaped twin of ClientEventEndpointsExtensions.WaitForNextAsync (channel-shaped) —
    // fix a bug in one, check the other.
    private static async Task<MoveWaitResult> WaitForNextAsync(
        Task<bool> pendingMove,
        TimeProvider timeProvider,
        CancellationToken ct) {
        if (pendingMove.IsCompleted)
            return ct.IsCancellationRequested ? MoveWaitResult.Cancelled : MoveWaitResult.MoveCompleted;

        using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var delay = Task.Delay(KeepAliveInterval, timeProvider, delayCts.Token);
        var winner = await Task.WhenAny(pendingMove, delay).ConfigureAwait(false);
        if (ct.IsCancellationRequested)
            return MoveWaitResult.Cancelled;
        if (winner != pendingMove)
            return MoveWaitResult.KeepAlive;

        delayCts.Cancel();
        return MoveWaitResult.MoveCompleted;
    }

    internal static async Task WriteHandlerStreamAsync<T>(
        HttpResponse response,
        IAsyncEnumerable<T> source,
        JsonTypeInfo<T> typeInfo,
        TimeProvider timeProvider,
        CancellationToken ct) {
        using var enumerationCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await using var enumerator = source.GetAsyncEnumerator(enumerationCts.Token);
        Task<bool>? pendingMove = null;
        try {
            while (true) {
                pendingMove ??= enumerator.MoveNextAsync().AsTask();
                var wait = await WaitForNextAsync(pendingMove, timeProvider, ct).ConfigureAwait(false);
                if (wait == MoveWaitResult.Cancelled)
                    return;
                if (wait == MoveWaitResult.KeepAlive) {
                    await WriteSseDataAsync(response, string.Empty, ct, KeepAliveEventType).ConfigureAwait(false);
                    await response.Body.FlushAsync(ct).ConfigureAwait(false);
                    continue;
                }

                var hasNext = await pendingMove.ConfigureAwait(false);
                pendingMove = null;
                if (!hasNext)
                    return;

                await WriteSseDataAsync(response, JsonSerializer.Serialize(enumerator.Current, typeInfo), ct).ConfigureAwait(false);
                await response.Body.FlushAsync(ct).ConfigureAwait(false);
            }
        } finally {
            await SettlePendingMoveAsync(pendingMove, enumerationCts).ConfigureAwait(false);
        }
    }

    private static async ValueTask SettlePendingMoveAsync(Task<bool>? pendingMove, CancellationTokenSource enumerationCts) {
        if (pendingMove is null)
            return;

        try {
            enumerationCts.Cancel();
        } catch {
            // A cancellation callback failure must not skip settling the active MoveNext. The terminal response
            // path already owns the primary failure; safety here is serializing cleanup and observing the task.
        }
        try {
            _ = await pendingMove.ConfigureAwait(false);
        } catch {
            // The terminal path already owns the primary response/timer/cancellation outcome. Awaiting here is
            // solely to serialize enumerator cleanup and observe the pending task before DisposeAsync.
        }
    }

    private enum MoveWaitResult {
        MoveCompleted,
        KeepAlive,
        Cancelled,
    }

    private static TimeProvider GetTimeProvider(HttpContext context) =>
        context.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System;
}
