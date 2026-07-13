# ADR-0055: Data-rate shaping helpers — write-behind buffer and keyed conflater (deferred)

- Status: Proposed (nothing ships; build alongside the first gateway port)
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

## Decision (pre-decided shape, deferred)

Two opt-in helpers, BCL-only, living beside the existing helper family (exact home decided at build
time; no DI requirement):

- **`WriteBehindBuffer<T>`**: `Add(item)`; flushes via an async delegate when `MaxItems` or
  `FlushInterval` is reached (whichever first); bounded (drop-oldest beyond capacity — these are
  loss-tolerant samples by contract); explicit `FlushAsync` and flush-on-dispose; single-flight flushes.
  The delegate's natural body is ADR-0051 `ExecuteInsertAsync`.
- **`KeyedConflater<TKey, TValue>`**: `Post(key, value)` (latest wins per key); emits per key at most
  once per `MinInterval` via an async delegate (the natural body: `IClientEventPublisher.PublishAsync`);
  a quiet key emits its final value once the interval elapses — conflation never ends on a stale value.

Discipline rule: these two shapes and no more — this is not the start of an Rx clone. A need beyond
them (windows, joins, replay) is the trigger for a real reactive/streaming library, adopted whole.

## Consequences

- Nothing ships now. Trigger: the first gateway port — its telemetry path exercises both on day one.
- Until then, the documented composition is an actor owning a plain buffer + a scheduled/interval flush
  (the ADR-0042 write-behind use case) and publish-throttling in the twin.
