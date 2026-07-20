# ADR-0069: Producer-owned hot-state primitives — bounded MPSC queue and staged-batch flusher

- Status: Accepted (implemented 2026-07-21)
- Date: 2026-07-21
- Related: [ADR-0055](0055-data-rate-shaping-helpers.md) (the `Elarion.Buffering` family this extends),
  [ADR-0066](0066-low-allocation-connection-dispatch.md) (the game-server-tier motivation: traffic
  deliberately scoped *out* of the handler pipeline),
  [ADR-0042](0042-in-memory-actors.md) (an actor's write-behind of owned in-memory state is the other
  natural flusher consumer).

## Context

The ADR-0066 low-allocation dispatch profile ends at the handler boundary: application-owned in-memory
hot state at gameplay/telemetry rates (a simulation tick loop, a device fan-in loop) was deliberately
left to application code. Field evidence from a simulation-tier consumer shows two generic gaps on that
path, both of the ADR-0055 shape — small, easy-to-get-subtly-wrong concurrency primitives:

1. **Getting commands into a single-writer loop.** Producer threads (connection I/O continuations) must
   hand fixed-size commands to one consumer thread (a tick loop) with zero allocation per message and
   bounded memory. `ConcurrentQueue<T>` allocates segments and is unbounded; `Channel<T>` allocates and
   is async-shaped; both treat backpressure as an afterthought rather than the caller's explicit,
   per-message-class decision.
2. **Getting that state persisted without touching the hot loop.** When state lives in the producer's
   own structures (struct-of-arrays tables, dirty flags) and only the *latest* state matters, neither
   ADR-0055 helper fits: `WriteBehindBuffer<T>` is an append-queue — per-`Add` cost, retains every
   sample, wrong for latest-wins state; `KeyedConflater<TKey, TValue>` conflates per key but emits per
   key — no batched atomic flush, and per-`Post` dictionary work on the hot path. The workload needs:
   mutation cost = one flag write; a periodic application-owned sweep snapshots dirty state into **one
   preallocated batch**; a background writer drains it single-flight. The batch's *shape* is
   application business (the dirty-flag-and-sweep guidance on the data-rate-shaping page stands) —
   what is generic is the ownership/signaling protocol around it.

The second boundary is evidence-backed, not speculative: the originating application added a second
state family with maximally different persistence semantics (per-owner row *sets* with deletions and
replace-set writes, versus row updates), which changed its staging and its SQL — and did not change
the handoff protocol by a line. New families extend the batch, never the protocol.

## Decision

Two additive siblings in `Elarion.Buffering`, following the family rules (BCL-only, no DI, AOT-safe,
loss contract stated first in the XML docs), each independently consumable:

- **`BoundedMpscQueue<T>` where `T : struct`** — a fixed-capacity, array-backed, lock-free queue
  (Vyukov's bounded algorithm: per-slot sequence numbers, CAS on the enqueue cursor) for many producers
  and one consumer. `TryEnqueue`/`TryDequeue` allocate nothing; capacity rounds up to a power of two.
  A full queue returns `false` — *the caller* decides per message class whether full means drop
  (loss-tolerant telemetry/movement) or retry/fail (must-land control messages); there are no blocking
  or waiting APIs, and the queue does not own a loop or thread — the application drains it. Ordering is
  FIFO per producer; cross-producer arrival order is slot-claim order. The single-consumer contract is
  documented and backed by a debug-only reentrancy assertion (a guard, not a thread capture — consumer
  migration between threads is legal as long as calls never overlap; the full MPMC dequeue variant was
  considered and rejected as an unneeded CAS on the consumer path). The enqueue and dequeue cursors are
  padded onto separate cache lines; slots are deliberately unpadded (commands are small; per-slot
  padding would multiply memory). Dequeued slots are cleared when `T` contains references, so the queue
  never keeps a payload alive until the slot's next lap; for pure value types the clear compiles away.
- **`StagedBatchFlusher<TBatch>` where `TBatch : class`** — a single-flight ownership handoff between a
  producer staging into **one preallocated, producer-owned batch object** and a background writer
  draining it via a caller-supplied async delegate. The primitive owns ownership transfer, signaling
  (allocation-free per submit), the writer loop, error policy, and shutdown draining; it never inspects
  the batch. The contract: the producer touches the batch only while `IsIdle` (volatile visibility of
  all writer-side effects); `TrySubmit` transfers ownership without blocking or allocating; `false`
  means skip-and-retry — dirty flags carry the state to the next sweep, which is why there is exactly
  one batch and no internal queue (double-buffering was evaluated and rejected: flags already carry
  state across a busy interval; a second buffer buys latency nobody needs at the cost of ownership
  complexity). A throwing write delegate drops that batch's staged content (latest-wins makes this
  safe), surfaces through `onFlushError`, and never tears down the loop or leaks ownership; an optional
  `reset` callback runs at each ownership return as a convenience. `DisposeAsync` drains: the in-flight
  write completes and a pre-disposal submit is written, uncancelled (they carry the last state of
  departed entities), bounded by an optional generous-by-default `DisposeTimeout`; `FlushAsync` awaits
  idleness at explicit sync points.

The ADR-0055 discipline rule is amended, not repealed: the family is now these **four** shapes —
append-all samples, latest-per-key emits, producer-owned state snapshots, commands into a
single-writer loop — and still not the start of an Rx clone. Windows, joins, or replay remain the
trigger for a real reactive/streaming library.

## Consequences

- Shipped in `Elarion` core (`Elarion.Buffering`); no changes to `WriteBehindBuffer`/`KeyedConflater`
  semantics. The data-rate-shaping capability page gains a selection table across the four primitives;
  its dirty-flag-and-sweep guidance now names the flusher as the generic handoff half while the dirty
  bit, iteration structure, batch shape, and staging stay application code.
- Both hot paths are pinned by deterministic allocation tests (zero bytes per op via
  `GC.GetAllocatedBytesForCurrentThread`), the family's promise, in the normal test suite — benchmarks
  stay the manual measurement tool.
- The queue is transactionality-free and the flusher is loss-tolerant by contract (a crash or failed
  flush loses at most one interval of staged state). Data that must not be lost goes through handlers
  and the outbox, not this family — restated on the capability page for all four primitives.
