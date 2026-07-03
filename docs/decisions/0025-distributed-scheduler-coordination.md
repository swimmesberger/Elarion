# ADR-0025: Cross-instance scheduler coordination (per-occurrence claims over EF Core/PostgreSQL)

- Status: Accepted
- Date: 2026-07-02
- Related: [ADR-0017](0017-dependency-light-core.md) (seam in Abstractions, provider-backed default in an
  opt-in package), [ADR-0021](0021-idempotency.md) (the unique-constrained claim row as a lock-free fence),
  [ADR-0022](0022-inbox-idempotent-event-consumers.md) (the inbox that now dedups the phase-2 job-envelope
  consumer by default — see the Phase 2 addendum), [ADR-0024](0024-postgres-listen-notify-settings-changes.md)
  (the settings change source whose live rescheduling this composes with), and the outbox lease pattern in
  `EfCoreOutboxStore`.

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
- **Placement is a property of the job, not of the seam.** `[ScheduledJob(Placement = …)]`
  (`JobPlacement.Cluster` default / `EveryNode`) lets a job that maintains process-local state — an
  in-memory lookup table, a per-node cache — opt out of coordination: under a cluster coordinator, a
  coordinated refresh job would run on one node and leave the other nodes silently stale, which is worse
  than the N× duplication coordination fixes. The scheduler simply never consults the coordinator for
  `EveryNode` jobs (exactly like `OneTime` schedules and runtime one-offs), so every coordinator
  implementation — including future external-engine adapters — stays a dumb "may I run this occurrence?"
  oracle. A third `SingleNodeSticky` value (node affinity for state) was considered and rejected: it is
  leader election through the back door, and at the target tier "state needing node affinity" belongs in
  the database.
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

## Phase 2: durable one-shots and downtime catch-up — can the outbox subsume them? (validated 2026-07-02)

The original phase-2 sketch was a bespoke `elarion_scheduler_jobs` table copying the outbox's delivery shape.
Before building that, the question "can the existing transactional outbox provide phase 2 with little or no new
implementation?" was validated against the actual outbox code. Findings first, direction after — the findings
stand on their own regardless of the direction chosen.

### Finding 1 — immediate durable one-shots: the outbox already provides the hard 80%

Publishing an integration event *is* a durable, cluster-executed, commit-gated one-shot: a row atomic with the
caller's transaction, claimed under a lease by any node's delivery worker, retried with exponential backoff
(`BaseRetryDelay 5s × 2ⁿ`, capped 1 h, 10 attempts), and parked for inspection when permanently failing
(verified: `MarkPermanentlyFailedAsync` sets `Attempts = MaxDeliveryAttempts`, permanently excluding the row
from claims while keeping it queryable). Placement inverts exactly as phase 2 wants: the receiving node only
records; whichever node claims executes. At-least-once with mandatory consumer idempotency — the phase-2
contract already decided above.

### Finding 2 — but "zero implementation" is false; the savings are the plumbing, not the whole feature

Itemizing what phase 2 needs against what the outbox supplies:

| Needed for durable one-shots | Outbox route | Bespoke store route |
| --- | --- | --- |
| Durable row, lease claim, retry/backoff, parking, purge | **Exists** | Copy from outbox (~the plumbing) |
| Future-dated eligibility | New: `DeliverAfterUtc` column + one poll clause | New: `DueTimeUtc` column + clause |
| Typed payload round-trip (AOT-safe serialize `TPayload`, resolve descriptor, invoke) | **New — identical work** (a job-envelope event + one framework consumer) | **New — identical work** |
| `IJobScheduler.ScheduleAsync` kept as the API | New: thin adapter → envelope event | New: store write |
| Cancel a pending job | New (cancel-pending-row API) | New (same) |
| `JobId`/handle + inspector visibility | New (or dropped) | New (or dropped) |
| Publish surface for "deliver later" | New: optional `IntegrationPublishOptions` on `PublishAsync` (decided below) | None (API is the store's own) |

The genuinely shared, unavoidable work is the payload envelope and dispatch; the outbox route saves the
lease/delivery/purge machinery and its tests. A real saving — roughly half the feature, not 95% of it.

### Finding 3 — semantic deltas of running jobs through the outbox pipeline (verified in code)

- **Delivery is sequential per node**: `OutboxDeliveryService` iterates its claimed batch one consumer scope at
  a time. Jobs share that single-file pipeline with business events — a slow job delays event delivery on its
  node. Worse: the whole claimed batch is stamped with **one** `leaseUntil`, so a job outrunning
  `LeaseDuration` (2 min default) lets the *other* messages in its batch expire mid-flight and be reclaimed by
  other nodes — correct under at-least-once, but it converts "one slow job" into elevated duplicate delivery
  for unrelated events. **Long-running jobs do not belong in the outbox pipeline.**
- **One global retry policy**: per-message exponential backoff, no per-job `ResiliencePolicy`, no
  overlap/serialization groups, no `MaxConcurrentRuns`, and the scheduler's capacity limit does not govern
  outbox consumers.
- **Scope and gating**: outbox publish rides the caller's `DbContext` scope and is commit-gated — usually an
  *improvement* (today `ScheduleAsync` fires even if the surrounding transaction rolls back — arguably a bug),
  but it is a semantic change, and scheduling from a scope-less singleton needs a fresh scope.
- **Modeling**: a job is an instruction, not a fact; the event bus is pub/sub-only (ADR-0010). A
  single-consumer job-envelope event is the standard task-queue-over-outbox shape and violates no contract
  (no reply), but the tension is real and should be documented rather than hidden.

### Finding 4 — full-cluster-downtime catch-up **cannot** ride the outbox

The outbox delivers rows that were already written; if the whole cluster is down over 03:00, no process was
alive to write the 03:00 row. The one theoretical outbox construction — a **chain of delayed events** (each
occurrence, when delivered, publishes the next occurrence as a future-dated event, so the 03:00 row exists
from 02:00 and survives downtime) — was examined and rejected on three verified grounds:

1. **Chain death**: a permanently failing occurrence is parked (`Attempts = max`, never claimed again) — and
   with it dies the publish of its successor. The recurring job silently stops forever; repairing that needs a
   watchdog comparing chains against schedules, i.e. durable schedule state anyway.
2. **Live rescheduling**: a `${…}` variable change must supersede already-written future rows — cancel/update
   of pending rows keyed by job, new machinery the outbox deliberately lacks (rows are immutable post-write).
3. **Seeding races**: N nodes starting fresh must not seed N chains — an idempotent per-occurrence insert,
   which is precisely the claims table again.

Catch-up is a *state* problem, not a *delivery* problem. The claims table **is** the needed state — a durable
"last fired occurrence per job" written as a by-product of phase 1. Design: at startup (and optionally per
claim), compare the latest claim against the previous grid slot; if older, enqueue a catch-up occurrence whose
due time *is* the missed slot, so the ordinary claim fence serializes multi-node catch-up and the existing
misfire policy (`FireOnce`/`Skip`/`CatchUp`) decides how much to replay. Caveats: the catch-up horizon is
bounded by `ClaimRetention` (7 d default); **no prior claim ⇒ no catch-up** (a fresh deployment must not fire
every cron job on first boot); at-most-once is preserved (a claim written by a winner that crashed mid-run is
not re-fired — consistent with phase 1); `EveryNode`/`OneTime` jobs are out of scope. The real new surface is a
scheduler integration point: `EnqueueRecurringJobs` today computes `GetFirstDueTime(now)` only and needs a way
to receive an overdue slot.

### Revised direction

- **Durable one-shots: outbox-first.** Add delayed delivery (`DeliverAfterUtc`) to the outbox, a job-envelope
  event + framework consumer, and a thin `IJobScheduler` adapter — accepting the Finding-3 constraints, which
  fit the 1–10-node positioning: durable one-shots there are typically *short, idempotent, modest-volume*
  ("send this email at 09:00"). The bespoke `elarion_scheduler_jobs` store is **demoted to a fallback**,
  warranted only if job volume, run duration, or per-job semantics outgrow the shared pipeline — at which
  point an external engine through the seams deserves equal consideration.
- **Downtime catch-up: claims-based**, independent of the outbox, as sketched in Finding 4.
- **The delay surface lives on the seam itself.** `IIntegrationEventBus.PublishAsync` grows an optional
  options bag — `PublishAsync<TEvent>(TEvent @event, IntegrationPublishOptions? options = null,
  CancellationToken ct = default)` with `DeliverAfterUtc` as its first member — not an outbox-specific side
  API. The first draft of this section kept delay off the seam because "the in-memory tier cannot durably
  honor it"; that reasoning was **rejected on review** as a false constraint: seam contracts are designed for
  the most capable implementation, and a weaker tier implements the closest semantics and documents the delta
  rather than vetoing the parameter. The in-memory tier can honor delay *non-durably* (timer-buffered, lost
  on crash) — precisely as durable as everything else it does, already covered by its documented best-effort
  contract. Pre-1.0, backward compatibility is likewise no reason to avoid the parameter. The options record
  also gives future publish options (dedup keys, priorities) an additive home without further seam churn.

Open questions carried into phase 2 implementation: whether `EnqueueAsync` (run-now) keeps its in-memory fast
path with durability opt-in via `ScheduledJobOptions` (current lean: yes — pay-for-what-you-use); the payload
compatibility contract when a row is claimed by a newer binary; and the shape of the scheduler's catch-up
entry point. One wiring fact to not trip over: the ADR-0022 inbox auto-attaches via
`HandlerRegistrationGenerator`, which only sees handlers in app assemblies — the **framework-shipped**
job-envelope consumer is hand-wired (the ADR-0031 pattern), so its pipeline must hand-compose the
`IdempotencyDecorator` with a `Consumer`-scoped policy (small; consider promoting a public reusable
`Result`-payload inbox policy at that point rather than pre-shipping one).

### Addendum (2026-07-04): the ADR-0022 inbox shifts the phase-2 math

The findings above were validated two days before the inbox
([ADR-0022](0022-inbox-idempotent-event-consumers.md)) shipped. They stand, but three read differently now:

- **Finding 1's contract improves by default.** "At-least-once with mandatory consumer idempotency" assumed
  dedup was the consumer author's burden. The phase-2 job-envelope consumer is precisely a handler-form
  integration consumer, so the **default-on inbox dedups its transactional effect automatically** — a
  redelivered envelope (lease race, crash between the consumer's commit and the finalize) replays the committed
  claim instead of re-executing the job. "Mandatory idempotency" narrows to the foreign-side-effect window
  only (pass `IEventContext.MessageId` — the envelope's durable id — as the downstream's dedup key). This
  further favors the outbox-first direction.
- **Finding 3's batch-lease hazard downgrades from duplicate *execution* to wasted *redelivery*.** A slow job
  letting its batch-mates' leases expire still causes reclaims, but each already-succeeded consumer's inbox
  claim absorbs the re-invocation. The latency argument is untouched — long-running jobs still do not belong in
  the shared single-file pipeline.
- **The delivery side needs no bespoke per-execution fence.** The inbox claim, committed atomically with the
  job's writes, *is* the fence: a failed or crashed job rolls its claim back and retries under the outbox
  backoff; a completed job never re-runs. `DeliverAfterUtc` interacts benignly with inbox retention — the claim
  is written at delivery time, not at publish, so a far-future one-shot does not age against `RetentionHours`.

Unchanged: Finding 4's catch-up analysis (chain death, live rescheduling, and seeding races are about durable
*schedule state*, which the inbox does not provide) and the claims-based catch-up direction.

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
