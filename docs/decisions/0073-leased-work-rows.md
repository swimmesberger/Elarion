# ADR-0073: Leased work rows are a recognized pattern, extracted on second demand

- Status: Accepted
- Date: 2026-08-15
- Related: [ADR-0049](0049-role-leases.md) (the one-row identity lease and the coordination taxonomy),
  [ADR-0062](0062-role-affine-routing-and-outbox-delivery.md) (role-affine claims over the outbox's
  leased rows), [ADR-0064](0064-acknowledgment-gated-outbound-delivery.md) (the second-demand
  extraction convention this ADR applies).

## Context

Elarion coordinates on the one PostgreSQL the application already runs (ADR-0025), and two distinct
lease *grains* have grown out of that posture:

- **The identity lease — one row, one role.** `IRoleLease` (ADR-0049) answers "which instance *is* X
  right now" from a conditional upsert on a single `elarion_role_leases` row, with a heartbeat,
  release-on-shutdown, and an undershooting local `IsHeld`. It is a first-class, packaged primitive
  with its own contract in Abstractions and a provider in `Elarion.Coordination.PostgreSql`.
- **The work-row lease — many rows, one worker each.** The outbox stamps a claim onto individual
  queue rows so exactly one worker delivers an envelope at a time. It has no packaged existence at
  all: it lives bespoke inside `EfCoreOutboxStore<TDbContext>` plus the lease columns and claim index
  in `UseElarionOutbox`.

ADR-0049 already named this taxonomy ("scheduler claims per work item, outbox leases per message, role
leases per role") but only extracted the role grain, because only the role grain had two consumers.
The work-row grain still has exactly one — and its correctness rests on invariants that are easy to
get subtly wrong the second time someone writes them from memory. This ADR writes those invariants
down, and fixes the trigger that would justify lifting them into a helper.

## Decision

### Name the work-row lease invariants

The pattern, exactly as `EfCoreOutboxStore` implements it over `OutboxMessage`
(`lock_id`, `locked_until_utc`, `processed_on_utc`, `attempts`):

1. **Claim is select → conditional stamp → re-read.** `ClaimPendingAsync` first selects candidate ids
   (`AsNoTracking`, ordered by `occurred_on_utc` then `id`, `Take(batchSize)`) with the eligibility
   predicate: unprocessed, under `MaxDeliveryAttempts`, and lease null or expired. It then issues one
   `ExecuteUpdateAsync` **restating the whole predicate** — `lock_id` and `locked_until_utc` are
   stamped only where the row is still eligible — and finally re-reads the rows it actually won
   (`candidateIds.Contains(id) && LockId == lockId`). The candidate select is advisory; the
   conditional update is the arbiter. Concurrent workers overlapping on candidates is normal and
   harmless: each takes the subset it stamped, and neither blocks the other.
2. **Every finalize is lease-guarded.** `MarkProcessedAsync`, `MarkFailedAsync`,
   `MarkPermanentlyFailedAsync`, and `ReleaseClaimAsync` all filter on `Id == groupId &&
   LockId == lockId` and report `rows > 0`. A worker whose lease expired while it was delivering
   therefore cannot complete, fail, or release a row another worker has since re-stamped — its update
   matches zero rows and it learns it lost the claim. This guard is the invariant; the rest of the
   pattern is bookkeeping around it.
3. **Recovery is by expiry, not by cleanup.** A crashed or partitioned worker releases nothing. Its
   stamps simply lapse when `locked_until_utc` passes, and the next claim's `LockedUntilUtc == null ||
   LockedUntilUtc < now` predicate makes the rows eligible again. There is no reaper process, no
   liveness registry, and no distributed lock to leak — which is exactly why the pattern survives
   `kill -9` at the tier Elarion targets.
4. **The hot path rides a partial index.** `UseElarionOutbox` declares
   `ix_{table}_claim` on `(target_role, occurred_on_utc, id)` filtered `WHERE processed_on_utc IS
   NULL`, alongside `ix_{table}_purge` on `processed_on_utc` filtered `WHERE processed_on_utc IS NOT
   NULL`. The claim index covers only the live queue, so claims stay fast while the table accumulates
   processed history, and purge scans the complement. A leased-work table without the partial index
   degrades exactly as the archive grows — which is when nobody is looking.

Points 1–3 also compose with ADR-0062: workers claim an envelope only when `TargetRole` is null or the
process currently holds that role, and recheck the lease immediately before dispatch, releasing an
unattempted claim if the role moved. Role affinity narrows *eligibility*; it does not replace the
lease guard.

### Do not extract it yet

There is no `Elarion.*` leased-work helper, and this ADR deliberately does not create one. The
extraction convention recorded in ADR-0064 applies: one consumer's mechanism is that consumer's
implementation detail; **two** make a framework seam. A helper designed against the outbox alone would
bake in the outbox's shape — its role-group targeting, its attempt/backoff columns, its
`MessageId`-keyed inbox — and the second consumer would spend more effort escaping the abstraction
than it saved.

### The trigger, and the shape it lifts into

Extraction is warranted the moment a **second queue-shaped consumer** lands in the framework — a
webhook dispatch table, a notification queue, or a durable retry table are the plausible candidates.
The shape to lift is small and EF-oriented, not a job engine:

- `ClaimPendingAsync(lockId, leaseUntil, batchSize, filter)` — the three-step claim of invariant 1,
  with the consumer supplying the extra eligibility `filter` (the outbox's role/attempt predicate
  being one such filter) and the entity supplying the lease columns through a small interface.
- `FinalizeAsync(id, lockId, …)` — the lease-guarded update of invariant 2, returning whether the
  claim was still held, so no consumer can write a finalize that forgets the `LockId` comparison.
- A model-builder extension that adds the lease columns and the partial claim index of invariant 4 to
  an entity, so the index is not something each table remembers to declare.

`EfCoreOutboxStore` becomes the first consumer at that point, and the extraction is only accepted if
its behavior is byte-identical: same SQL shape, same ordering, same return values. An extraction that
changes outbox delivery semantics is a different ADR.

## Alternatives considered

- **Extract the helper now, before a second consumer.** Rejected — speculative generalization. The
  only available design input is the outbox, so the "generic" helper would be the outbox with its
  names filed off, and the first genuinely different consumer would force a redesign anyway. ADR-0064
  recorded the same posture for connection helpers, for the same reason.
- **Leave it entirely undocumented and let the next subsystem copy-paste `EfCoreOutboxStore`.**
  Rejected — copy-paste drops precisely the invariant that matters. The candidate select and the
  stamping update are visibly load-bearing and get copied; the `LockId == lockId` guard on every
  finalize looks like defensive redundancy and gets dropped, and the resulting bug (an expired worker
  completing a row another worker is actively delivering) is a rare, unreproducible duplicate
  delivery. Writing the invariants down is the cheap half of the extraction; the code is the
  expensive half that can wait.
- **Fold work-row leases into `IRoleLease`.** Rejected — different grain, as ADR-0049 already stated
  in its non-goals. A role lease is coarse, long-lived, and observable in one row; a work-row lease is
  per item, short, and exists in its thousands. One contract serving both would be a distributed-lock
  API, which is explicitly not what Elarion offers on the one Postgres.
- **Use `SELECT … FOR UPDATE SKIP LOCKED` instead of stamped leases.** Rejected as the framework
  default: it ties the claim to a held transaction and therefore to a held connection for the whole
  delivery, and it is provider-specific. The stamped lease survives connection loss, process death,
  and delivery work that outlives any sane transaction — and it keeps the store EF-portable.

## Consequences

- The work-row lease is now a *recognized* Elarion pattern with written invariants, so a reviewer can
  check a new queue-shaped table against four concrete properties instead of intuition.
- `EfCoreOutboxStore` remains the single implementation; nothing moves, and no package is added.
- When the second consumer arrives, the extraction is a mechanical, behavior-preserving refactor with
  a pre-agreed API sketch rather than a fresh design argument.
- If no second consumer ever arrives, this ADR has cost nothing but the page — which is the intended
  trade of the second-demand convention.
