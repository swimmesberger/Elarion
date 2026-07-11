using Elarion.Abstractions.ClientEvents;
using Elarion.Actors;

namespace LiveQuotes.Api.Modules.Market.Actors;

/// <summary>
/// One symbol's live quote — the actor "shape 2" from the
/// <see href="https://elarion.wimmesberger.dev/docs/concepts/actors">actors concept doc</see>: hot,
/// ephemeral, loss-tolerant in-memory state where a database write per tick would be pure overhead.
/// The mailbox is the whole concurrency story: feed ticks and any other input are applied one turn at a
/// time, first come first served, with no locks in this class.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deliberately no <c>IActorState</c>.</b> The state is transient by contract: after a restart, old
/// prices are worth nothing — the feed re-primes every symbol within a tick interval (a real vendor feed
/// does the same with its snapshot-then-increments protocol). Snapshots would only add write load for
/// data whose durability nobody wants.
/// </para>
/// <para>
/// <b>Conflation is the actor's second job.</b> No dashboard needs every tick; the actor keeps the true
/// current value on every turn but publishes at most one client event per
/// <see cref="PublishInterval"/> per symbol — latest value wins. That bounds the SSE/pg_notify volume to
/// human-readable rates no matter how hot the feed runs, and it needs no timer: the tick stream itself
/// drives the clock.
/// </para>
/// <para>
/// <b>SingleHomed</b> states the topology intent: exactly one instance runs the feed and these actors.
/// In this single-process sample it is unenforced (no home lease registered — you'll see the startup
/// warning); on a worker + web-nodes deployment, module gating places the actors and
/// <c>AddElarionPostgreSqlActorHome</c> turns a wrong-instance call into a pointed error.
/// </para>
/// </remarks>
[Actor(SingleHomed = true)]
public sealed class StockQuoteActor(
    IActorContext<string> context,
    IClientEventPublisher clientEvents,
    TimeProvider timeProvider) {
    /// <summary>Minimum spacing between pushed updates per symbol (the conflation window).</summary>
    public static readonly TimeSpan PublishInterval = TimeSpan.FromMilliseconds(250);

    private decimal _price;
    private decimal _sessionOpen;
    private long _seq = -1;
    private long _lastPublishTimestamp;
    private bool _hasValue;

    /// <summary>
    /// Applies one feed tick. Out-of-order delivery (possible when one symbol arrives over multiple
    /// feed channels) is dropped by the sequence guard — the mailbox serializes processing, the
    /// sequence number preserves the feed's order.
    /// </summary>
    public async Task Apply(QuoteTick tick, CancellationToken cancellationToken) {
        if (tick.Seq <= _seq) {
            return;
        }

        _seq = tick.Seq;
        _price = tick.Price;
        if (!_hasValue) {
            _hasValue = true;
            _sessionOpen = tick.Price;
        }

        // Conflate: always keep the current value, push at most every PublishInterval.
        var now = timeProvider.GetTimestamp();
        if (_lastPublishTimestamp != 0 &&
            timeProvider.GetElapsedTime(_lastPublishTimestamp, now) < PublishInterval) {
            return;
        }

        _lastPublishTimestamp = now;
        // The ephemeral publish tier: the payload carries the value (nothing to re-query), scoped to
        // this symbol so only its watchers receive it.
        await clientEvents.PublishAsync(ToQuoteChanged(tick.At), ClientEventScope.Resource(context.Key), cancellationToken);
    }

    /// <summary>The current value, for the pull-based query path (initial page load / reconnect converge).</summary>
    public Task<Quote?> GetQuote() =>
        Task.FromResult(_hasValue
            ? new Quote(context.Key, _price, ChangePercent, _seq, timeProvider.GetUtcNow())
            : null);

    private decimal ChangePercent =>
        _sessionOpen == 0 ? 0 : Math.Round((_price - _sessionOpen) / _sessionOpen * 100m, 2);

    private QuoteChanged ToQuoteChanged(DateTimeOffset at) => new() {
        Symbol = context.Key,
        Price = _price,
        ChangePercent = ChangePercent,
        Seq = _seq,
        At = at
    };
}
