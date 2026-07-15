# ADR-0049: Role leases — the leader-election primitive, extracted from the actor home

- Status: Accepted
- Date: 2026-07-11
- Related: [ADR-0048](0048-single-homed-actors.md) (the actor home, now the first consumer of this
  primitive), [ADR-0025](0025-distributed-scheduler-coordination.md) (coordination on the one
  Postgres; scale positioning), [ADR-0034](0034-abstractions-holds-contracts-not-implementations.md)
  (contract in Abstractions, implementation in an opt-in sibling).

## Context

ADR-0048 shipped leader election on PostgreSQL under an actor-specific name (`IActorHomeLease`,
`elarion_actor_home`). Nothing in the mechanism is actor-specific — the conditional-upsert row, the
application-clock-only rule, the undershooting `IsHeld`, release-on-shutdown, the heartbeat — and
"exactly one instance runs X" is a primitive applications keep reinventing badly (advisory locks that
don't survive connection loss, cron-plus-hope). Elarion also has framework-internal consumers waiting:
the outbox needs a provider-neutral local ownership view for role-affine claims, and the every-node
periodic workers (idempotency purge, blob/staged-upload GC) do duplicated —
safe but pointless — work. A non-actor app wanting leader election should not reference
`Elarion.Actors.PostgreSql`.

This completes a coordination taxonomy on the one Postgres: **scheduler claims** (per work item),
**outbox leases** (per message), **role leases** (per role — "which instance *is* X right now").

## Decision

- **Contract in Abstractions**: `Elarion.Abstractions.Coordination.IRoleLease { Role, IsHeld,
  CurrentHolder }`, registered **keyed by role name** so an application holds a handful of independent
  roles. `IsHeld` answers from local state (no I/O — it sits on hot paths) and must turn false before
  the underlying lease can expire for another instance. Consumers **gate per unit of work** (per call,
  per polling cycle), never start/stop on leadership changes — no lease-change tokens or subscription
  events until a real workload demands them.
- **Implementation in a new sibling**, `Elarion.Coordination.PostgreSql`: the ADR-0048 lease verbatim,
  renamed role-neutral — `elarion_role_leases` table, `UseElarionRoleLeases(modelBuilder)` /
  `[GenerateElarionRoleLeases]` (bundled generator, `ELROLE001` without `[GenerateDbSets]`),
  `AddElarionPostgreSqlRoleLease<TDbContext>(o => o.RoleName = "…")` — once per role, each with its own
  heartbeat; registering one role twice in a process throws (it would compete against itself).
- **Actors keep their seam as a view.** `IActorHomeLease` stays in `Elarion.Actors` — the runtime
  demands *the actor home*, not "some role" — but its default implementation is an adapter over the
  keyed role lease, bound by `AddElarionActorHome(role = "actors")` (core, Abstractions-only).
  `AddElarionPostgreSqlActorHome<TDbContext>()` becomes two-line sugar: add the `"actors"` role lease,
  bind the home. Any future lease backend serves actors with no actor-side change.
- **Non-goal, written down**: this is a *role* lease, not a distributed-lock API. No per-key
  acquisition, no lock scopes, no `AcquireAsync(key)` — coarse, long-lived, observable-in-one-row
  roles only. Per-work-item coordination stays with scheduler claims and outbox leases; per-key
  serialization is the `[Sequential]` trap ADR-0042 already recorded.

## Consequences

- Applications get first-class leader election on the database they already run: register a role,
  inject the keyed `IRoleLease`, and gate work on `IsHeld`. ADR-0062's `IRoleLeaseRegistry` lets the
  outbox claim deliveries targeting any locally held role.
- Framework periodic workers (idempotency purge, blob GC) *can* later accept an optional role gate —
  deliberately not done now; duplication there is idempotent and harmless at the tier.
- One more package pair (runtime + bundled generator), justified by the dependency-direction fix: the
  primitive no longer lives inside an actor package.
- ADR-0048's mechanics are unchanged — its lease section is now implemented by this package; the
  actor-facing semantics (gating, `ActorNotHomedException`, soft-when-unregistered) stay in
  `Elarion.Actors`.
