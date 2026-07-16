using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
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

    internal static IResult CreateHandlerStreamResult<T>(
        IAsyncEnumerable<T> source,
        JsonTypeInfo<T> typeInfo,
        TimeProvider timeProvider,
        CancellationToken ct) =>
        TypedResults.ServerSentEvents(StreamHandlerEventsAsync(source, typeInfo, timeProvider, ct));

    internal static async IAsyncEnumerable<SseItem<string>> StreamHandlerEventsAsync<T>(
        IAsyncEnumerable<T> source,
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

                yield return new SseItem<string>(JsonSerializer.Serialize(enumerator.Current, typeInfo));
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
