# ADR-0055: Data-rate shaping helpers — write-behind buffer and keyed conflater

- Status: Accepted (implemented 2026-07-13); amended by [ADR-0069](0069-producer-owned-hot-state-buffering.md)
  (two producer-owned hot-state siblings join the family; the "two shapes and no more" rule becomes four)
- Date: 2026-07-13
- Related: [ADR-0051](0051-postgresql-bulk-insert.md) (the natural flush target),
  [ADR-0043](0043-client-events.md) (the natural publish target),
  [ADR-0042](0042-in-memory-actors.md) (write-behind buffers are a named actor use case),
  [ADR-0053](0053-bidirectional-client-connections.md) (the `ConnectionInbox`/`ConnectionPendingRequests`
  precedent: small BCL-only helpers proven by two field implementations).

## Context

Both field gateways hand-roll the same two data-rate primitives between "device produces samples" and
"database/UI consume them":

1. **A write-behind buffer** — accumulate high-frequency samples and flush in batches (by count and/or
   interval) to the database; loss-tolerant by declaration, flushed on shutdown. One project throttles
   storage to one sample per 10 s while pushing every tick to realtime; the other routes everything
   through a batched database writer.
2. **A keyed conflater** — latest-wins per key with a maximum publish rate: the "live visualization"
   primitive that keeps a hot value stream from drowning client events or the UI, while never showing a
   stale key forever (the newest value always eventually publishes).

Both are ~100-line, easy-to-get-subtly-wrong concurrency helpers (flush/publish races, shutdown flush,
timer lifecycle) — the same shape as the shipped conversation helpers.

## Decision

Two opt-in helpers, BCL-only, in `Elarion` core under the `Elarion.Buffering` namespace — beside the
`Elarion.Streams` helper family, with no DI requirement (constructed by whoever owns the data path,
typically an actor or gateway component):

- **`WriteBehindBuffer<T>`**: `Add(item)`; flushes via an async delegate when `MaxItems` or
  `FlushInterval` is reached (whichever first); bounded (drop-oldest beyond `Capacity` — these are
  loss-tolerant samples by contract, and `DroppedCount` meters the pressure); explicit `FlushAsync` and
  flush-on-dispose; single-flight flushes (items arriving during a flush coalesce into the next drain
  pass instead of stacking calls). A failed flush drops its batch — rethrown from the explicit
  `FlushAsync`, routed to the optional `onFlushError` callback on background/dispose flushes. The
  delegate's natural body is ADR-0051 `ExecuteInsertAsync`.
- **`KeyedConflater<TKey, TValue>`**: `Post(key, value)` (latest wins per key); emits per key at most
  once per `MinInterval` via an async delegate (the natural body: `IClientEventPublisher.PublishAsync`) —
  leading edge immediate on an idle key, trailing edge for the conflated latest; a quiet key emits its
  final value once the interval elapses — conflation never ends on a stale value. Emissions for one key
  never overlap (a slow publish lowers the effective rate instead of stacking calls); idle keys retire
  automatically, so unbounded key spaces don't leak; dispose flushes every pending latest. Publish
  failures drop that emission to the optional `onPublishError` callback — these are at-most-once hints
  and the next post heals, matching the ADR-0043 client-event contract.

Both take a `TimeProvider` (in their options) so tests drive them deterministically, follow the
"shutdown flushes rather than cancels" rule (the final flush is uncancellable; the delegate bounds its
own work), and drop `Add`/`Post` calls after dispose silently — producers racing a shutdown must not
crash the receive path.

Discipline rule: these two shapes and no more — this is not the start of an Rx clone. A need beyond
them (windows, joins, replay) is the trigger for a real reactive/streaming library, adopted whole.

## Consequences

- Shipped in `Elarion` core (`Elarion.Buffering`); dependency-free beyond the BCL, `IsAotCompatible`.
- The recommended composition stays the ADR-0042 shape — an actor owns the buffer/conflater as
  activation state and disposes it in `OnDeactivateAsync` — but the primitives no longer need to be
  hand-rolled there.
- Failure observability is opt-in by callback (`onFlushError`/`onPublishError`); without one, failures
  are swallowed under the loss-tolerance contract. Supply the callback in anything beyond a prototype.
