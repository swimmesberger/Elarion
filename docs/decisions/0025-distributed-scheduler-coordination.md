# ADR-0025: Cross-instance scheduler coordination (per-occurrence claims over EF Core/PostgreSQL)

- Status: Accepted
- Date: 2026-07-02
- Related: [ADR-0017](0017-dependency-light-core.md) (seam in Abstractions, provider-backed default in an
  opt-in package), [ADR-0021](0021-idempotency.md) (the unique-constrained claim row as a lock-free fence),
  [ADR-0024](0024-postgres-listen-notify-settings-changes.md) (the settings change source whose live
  rescheduling this composes with), and the outbox lease pattern in `EfCoreOutboxStore`.

## Context

The scheduler is the last stateful subsystem without a durable rung: settings, events (outbox), idempotency,
blobs, and caching all have a database-backed option, but `InMemoryScheduler` is purely per-process. On N
nodes, **every recurring job runs N times** — N daily reports, N cleanup sweeps — and runtime-scheduled
one-shot jobs (`IJobScheduler.ScheduleAsync`) do not survive a restart. The July 2026 soundness audit rated
this the largest parity gap.

Two distinct problems hide in "durable scheduler":

1. **Coordination** of compile-time recurring jobs — each node already knows the full job set (the
   source-generated descriptors) and computes due times locally; the only missing piece is *which node fires
   a given occurrence*.
2. **Durability** of runtime-created one-shot jobs — payloads must be persisted, claimed for execution with
   a lease, retried across restarts, and inspectable cluster-wide.

The audit gap is (1). This ADR decides (1) fully and shapes (2) as a follow-up phase.

A load-bearing detail of the existing schedule semantics: **cron occurrences are wall-clock deterministic**
(every node computes the same instants from the same expression), while **fixed-rate grids are anchored at
each node's start** (`GetFirstDueTime` returns `now` / `now + interval`) and **fixed-delay chains are
anchored at each node's own run completions** — so fixed-rate/fixed-delay due times *differ across nodes* by
construction. Any coordination design must handle both shapes.

## Decision

### Per-occurrence claims, not leader election

A node that is about to execute a recurring occurrence first **claims** it in the database; exactly one node
wins, the others record the occurrence as skipped (`claimed-elsewhere`) and carry on with their local chains.
The claim row is the fence — no leader, no failover protocol, no extra always-on component:

- **Leader election** (a single scheduling node) was rejected: it needs lease renewal, failover detection,
  and a demotion path; a mid-tier "the leader died 30 s ago" window either double-fires or misses
  occurrences; and it serializes *all* jobs through one node. The claim model degrades per-occurrence
  instead of per-cluster and reuses the pattern the repo already trusts twice (idempotency keys, outbox
  leases).
- **Per-job lease rows with a store-owned next-due** (the store as the single source of scheduling truth)
  was rejected for phase 1: it would move due-time computation, misfire policy, and the live-reschedule
  logic behind a store seam — a rewrite of the scheduler's core loop — for no additional exactly-once
  strength over claims. It remains the natural shape for phase 2's durable one-shot jobs, where the row
  must exist anyway (the payload lives there).

### Claim semantics per schedule kind

The seam hands the coordinator a `ScheduledOccurrence` — job name, due time, and an optional
**dedupe window**:

| Kind | Cross-node determinism | Claim |
| --- | --- | --- |
| Cron | Same instants on every node | **Exact slot**: unique `(job, occurrence)` insert; `ON CONFLICT DO NOTHING` — the primary key serializes racers. |
| FixedRate | Node-local grid | **Sliding window** (window = interval): the claim succeeds only if no claim for the job exists within one interval before the due time — at most one run per interval cluster-wide. |
| FixedDelay | Node-local chain | Sliding window (window = delay) — at most one run per delay window cluster-wide; every node keeps polling, one wins each pass. |
| OneTime | Startup work | **Not coordinated** — runs on every node by design (cache warm-up et al.). A cluster-wide "exactly once ever" belongs to an `[Idempotent]` handler or a phase-2 durable one-shot. |

The window claim cannot rely on `NOT EXISTS` alone (two nodes inserting *different* due times in the same
window pass the check concurrently under read committed), so the EF/PostgreSQL implementation takes a
**per-job transactional advisory lock** (`pg_advisory_xact_lock(hashtextextended(job, 0))`) around the
conditional insert — cheap, self-releasing on commit/rollback, and scoped to one job so unrelated jobs never
contend. Exact-slot claims need no lock; the primary key is the fence.

### The seam and where things live

- `IScheduledOccurrenceCoordinator` + `ScheduledOccurrence` live in `Elarion.Abstractions.Scheduling`
  (transport- and provider-neutral, mirroring `IAuthorizer`/`IFeatureFlagService`).
- `Elarion` core ships `LocalScheduledOccurrenceCoordinator` (always claims — single-node semantics are
  byte-for-byte unchanged), registered by `AddElarionScheduler` via `TryAdd`.
- `InMemoryScheduler` claims **at the fire point** (after the local chain has already advanced, right before
  the overlap/concurrency gate), so a lost claim behaves exactly like the existing overlap skip: the
  occurrence is recorded as `Skipped`, fixed-delay successors are still scheduled, and the inspector shows
  what happened. Everything else — dispatch, capacity, overlap, misfire evaluation, deferred retry,
  telemetry, the inspector — stays local and unchanged.
- `Elarion.Scheduling.EntityFrameworkCore` (opt-in, targets PostgreSQL like the idempotency store) ships the
  claims table with the full ADR-item-3 wiring parity — `UseElarionSchedulerClaims(tableName?, schema?,
  snakeCase)`, `[GenerateElarionSchedulerClaims]` with a bundled generator (`ELSCH001`) — the
  `EfCoreScheduledOccurrenceCoordinator`, and a retention purge worker
  (`AddElarionSchedulerEntityFrameworkCore<TDbContext>` replaces the local coordinator).

### Failure semantics

- **Coordinator failure (database unreachable):** the node logs and **skips** the occurrence (fail-closed).
  Duplicate suppression is the whole point of opting into coordination; running anyway would reintroduce
  N× exactly when the shared database — which the job almost certainly also needs — is down. The next
  occurrence retries naturally.
- **Winner crashes mid-run:** the occurrence is *at-most-once*. Claims are not leased for re-execution —
  by the time a lease could expire, the losing nodes have already advanced past the occurrence, and re-running
  half-executed job work is exactly what `[Idempotent]` handlers are for. The next occurrence fires normally
  on any node. (Phase 2's one-shot rows *are* leased, outbox-style, because their losers still hold the row.)
- **Live rescheduling (H8) and clock skew:** after a variable change, nodes recompute grids anchored at
  their own `now`, so grids briefly disagree (they converge; with ADR-0024 the change itself reaches all
  nodes promptly). Window claims absorb exactly this: whatever instants the nodes compute, at most one run
  per window fires. Cron claims are keyed by the (deterministic) instant, so a reschedule that changes the
  expression simply starts keying new instants. NTP-level clock skew is far below practical windows and cron
  granularity; window claims tolerate it by construction.

### Retention

One claim row per fired occurrence per job accrues (a one-second cron writes ~86 k rows/day). The purge
worker deletes claims older than `ClaimRetention` (default 7 days) in batches; the retention must exceed the
largest dedupe window in use (enforced by documentation — windows are typically minutes, retention days).

## Scale positioning

Elarion's defaults target **small-to-mid deployments: ~1–10 nodes, vertical-first, on the one PostgreSQL the
application already runs**. The claim design above is sized for exactly that tier (a one-second cron across
ten nodes is ten tiny inserts per second) and deliberately does not chase larger tiers — a deployment beyond
it replaces the strategy through the seams (`IScheduledOccurrenceCoordinator` today, the phase-2 store seam
later) rather than the default growing configuration surface.

For the same reason, **adopting a dedicated job engine (Quartz.NET, Hangfire, TickerQ) as the framework
default was evaluated and rejected**: Quartz and Hangfire are built on runtime reflection / serialized type
names and expression-tree serialization — incompatible with the repo's AOT posture and source-generated
invocation descriptors — and all three would make every scheduler user inherit their schema, storage
opinions, and release/license cadence, against ADR-0017. Their coordination models also validate this
design's shape: Quartz serializes trigger acquisition through database locks (`QRTZ_LOCKS`,
`SELECT … FOR UPDATE` — locking *the scheduler* where we lock *the occurrence*), Hangfire uses competing
workers with invisibility timeouts (at-least-once — a different contract than our at-most-once), and TickerQ
independently landed on "the EF Core row is the lock". A Quartz/Hangfire-backed adapter package behind the
seams remains the supported escape hatch for teams beyond the target tier; teams needing queue-style
background processing at volume should run Hangfire *beside* Elarion rather than through it.

## Phase 2 (designed, not implemented): durable one-shot jobs and downtime catch-up

Runtime `ScheduleAsync`/`EnqueueAsync` jobs get a `elarion_scheduler_jobs` table: serialized payload
(canonical JSON via `IElarionJsonSerialization`, typed through the generated descriptor so AOT-safe),
due time, attempt counters, and outbox-style `lock_id`/`locked_until` lease claims with lease-guarded
finalize; deferred retries update the row instead of re-queuing in memory, so retries survive restarts, and
`IJobSchedulerInspector` gains a store-backed view. This is deliberately the outbox delivery shape — the
code to copy already exists — but it is a separate, larger change and ships separately.

Decisions already taken for phase 2, and the scenarios that drove them:

- **Placement inverts.** Today a runtime job executes on the node that accepted the call and dies with it
  (the "schedule for tomorrow, pod recycled tonight" scenario). With the row store, the receiving node only
  *records* the job; **whichever node claims it at due time executes it** — work survives deploys and
  migrates off dead nodes, and cancellation/inspection become cluster-wide (the row is the state, not a
  dictionary on one node).
- **Durable one-shots are at-least-once**, unlike recurring occurrences (at-most-once). A leased row whose
  owner crashes is still visible to the other nodes, so an expired lease is reclaimed and the job re-runs —
  the outbox trade. Durable one-shot jobs must therefore be idempotent (or `[Idempotent]`-guarded); the
  docs must say so as loudly as the outbox docs do.
- **Recurring downtime catch-up joins phase 2.** The claims model suppresses duplicates but does not
  guarantee execution: if the whole cluster is down over 03:00, the 03:00 occurrence never fires (each node
  re-derives its schedule from *now* at start). This is the one capability worth adopting from Quartz's
  persistent triggers — and it matters *most* at the 1–10 node tier, where a maintenance window routinely
  takes down the entire cluster. Design sketch: a durable last-completed-occurrence per recurring job (the
  claims table nearly is one already); at startup/claim time, a node that finds the last completed
  occurrence older than the previous grid slot applies the job's existing misfire policy (`FireOnce` /
  `Skip` / `CatchUp`) against the *durable* record instead of only in-process state.

Open questions deferred to phase 2 implementation:

- **Does `EnqueueAsync` (run-now) go through the table?** Current lean: no — the caller's node is
  demonstrably alive and a database round-trip per fire-and-forget is real overhead; keep the in-memory
  fast path as the default with durability opt-in via `ScheduledJobOptions` (pay-for-what-you-use).
- **Payload versioning** — a durable row may be claimed by a node running a newer binary; the canonical
  JSON pipeline tolerates additive change, but the ADR for phase 2 should state the compatibility contract.
- **Retention and dead-lettering** for rows that exhaust their attempts (the outbox's `MaxDeliveryAttempts`
  shape, plus an inspector surface for "why didn't my job run").
- **Whether the durable store seam subsumes `IScheduledOccurrenceCoordinator`** or composes beside it —
  a store-backed scheduler could carry claims as a by-product; keep the coordinator seam public either way,
  since it is also the natural adapter point for external engines.

## Consequences

- Recurring jobs on N nodes execute once per occurrence (cron) or once per window (interval kinds) —
  the audit's parity gap #1 closes for compile-time jobs, with an audit trail (`claimed-elsewhere` skips) in
  the snapshot/outcomes.
- Single-node hosts see zero change (local coordinator short-circuits, no database traffic).
- The coordinated cluster gains a hard dependency of the fire path on the claims database (fail-closed by
  design, per above).
- Interval jobs are coordinated by window, not by slot — under pathological pauses a window boundary can
  admit two runs closer than the interval (one late, one on time); jobs needing hard exactly-once semantics
  should be `[Idempotent]` or cron-scheduled.
- Runtime one-shot jobs remain in-memory until phase 2; the `IJobScheduler` docs already state that contract.
