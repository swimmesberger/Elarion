# ADR-0048: Single-homed actors — a PostgreSQL home lease, not a cluster

- Status: Accepted
- Date: 2026-07-11
- Addendum: the lease mechanism described here was extracted into the generic role-lease primitive by
  [ADR-0049](0049-role-leases.md) (`IRoleLease` in Abstractions, `Elarion.Coordination.PostgreSql`,
  table `elarion_role_leases`, marker `[GenerateElarionRoleLeases]`); `IActorHomeLease` remains the
  actor-facing view and `AddElarionPostgreSqlActorHome` is now sugar over the `"actors"` role lease.
  The semantics below are unchanged.
- Related: [ADR-0042](0042-in-memory-actors.md) (single-node by design; clustering is the Orleans
  trigger), [ADR-0047](0047-actor-state-snapshotting.md) (snapshot ETag + transparent conflict retry —
  the safety net this feature leans on), [ADR-0025](0025-distributed-scheduler-coordination.md) (the
  claim/lease-on-the-one-Postgres pattern this reuses; scale positioning), ADR-0022/0021 (outbox
  delivery + leases).

## Context

The actor runtime has no placement and no forwarding: `IActorSystem.Get<T>(key)` always resolves an
activation on the calling instance, so an app scaled to N instances gets up to N independent activations
per key. ADR-0047 made that *safe* (one shared snapshot row, ETag-serialized, conflicts absorbed by a
transparent retry) but not *single*: for coordinator-style actors — an escalation latch, a per-entity
saga — one authoritative activation is the model, and N-way optimistic writing is just conflict churn
plus at-least-once side effects.

The obvious-looking answer — "light" Proto.Actor-style clustering: a placement directory plus gRPC
forwarding to the owning node — was considered and rejected. Its parts unpack into the full
distributed-systems bill: placement needs membership and failure detection (split-brain included),
forwarding changes call semantics (retries, at-most/at-least-once ambiguity, cross-node backpressure,
mTLS), every actor method's parameters become versioned wire contracts, and deploys need activation
handoff. That is Orleans/Proto.Actor's whole product; rebuilding it "light" erases ADR-0042's crispest
line and would consume the maintenance budget of everything else. Notably, "a cluster on the one
PostgreSQL the app already runs" *already exists*: Orleans' ADO.NET clustering — and the facade and
`IActorState` surfaces were deliberately shaped so that migration is mechanical.

What the 1–10-node tier actually needs is much smaller: a way to say "these actors run on exactly one
instance", enforced, self-electing, with automatic failover — and a way to route the *work that feeds
them* (integration events, above all) to that same instance.

## Decision

### One role lease on Postgres — leader election as a single row

`Elarion.Actors.PostgreSql` gains an **actor home lease**: one row per role (one role, `"actors"`, is
the model) in `elarion_actor_home(role PK, owner, expires_on_utc)`. Acquisition is a conditional upsert
— renew when you already own the row, take over when the previous hold expired — so exactly one
instance holds the role at a time, with **no membership protocol: the row is the membership** (the
ADR-0025 pattern, same family as scheduler claims and outbox leases). A heartbeat hosted service renews
on `RenewInterval` (default 10 s) against a `LeaseDuration` (default 30 s = the failover bound); on
graceful shutdown it expires its own row so handover is immediate.

Two clock rules keep it honest:

- **The application clock is the only clock.** Expiry instants are written and compared as parameters
  from `TimeProvider`; the database clock is never consulted. (Also what makes the lease protocol
  deterministically testable with `FakeTimeProvider` against real Postgres.)
- **`IsHeld` undershoots.** The locally cached hold ends `HeldSafetyMargin` (default 5 s) before the
  stored expiry and is anchored to a monotonic timestamp captured *before* the acquire round trip — the
  old home stops acting before a new one can legitimately start. The remaining pathology (GC pause,
  extreme clock skew) degrades to brief double-holding, which ADR-0047's ETag + transparent retry
  absorbs losslessly. A database outage degrades to "nobody is home" — fail-closed, never two homes.

### `[Actor(Placement = ActorPlacementMode.SingleHome)]` — the per-actor declaration

The core seam is `IActorHomeLease { bool IsHeld; string? CurrentHolder; }` in `Elarion.Actors`. A
`SingleHome` actor's calls are gated per enqueue: on a non-holding instance the call fails immediately
with `ActorNotHomedException` naming the holder. Gating per call (not per activation) means a lease
lost mid-activation stops *new* work at once; in-flight turns finish under the ETag safety net.

**Soft when unregistered**: with no `IActorHomeLease` in DI, the declaration is not enforced (the
single-instance / local-dev case) — the inbox precedent (no store → un-deduped, never a resolution
failure). The actor system logs one warning per such actor so a multi-instance misconfiguration is
visible.

### The work follows the lease — per-consumer role affinity, not call forwarding

ADR-0062 replaces the original whole-worker `DeliveryGate`. Generated actor consumer descriptors name
the actor-home role, and publishing persists it on that consumer's independent `OutboxDelivery`. A
worker can claim the delivery only while its local role registry says it holds `"actors"`. Other
consumers of the same event remain unbound or target their own roles.

```csharp
services.AddElarionPostgreSqlActorHome<AppDbContext>();
services.AddElarionOutbox<AppDbContext>();
```

The outbox and actors remain decoupled through general descriptor/role contracts. Failover transfers
claim eligibility, bounded by `LeaseDuration`, without remote actor calls or an HTTP hop.

### Reads run anywhere — `IActorStateReader`

Queries must not need the home. `IActorStateReader.ReadAsync<TState>(key)` reads the latest snapshot
without activating anything — durable truth, on any instance (registered by
`AddElarionPostgreSqlActorSnapshots`). Commands go through the actor; queries read the snapshot (or
regular entities). Mutations the actor has not yet written are invisible by contract — under the
explicit-write model that is read isolation, not staleness: `WriteStateAsync` is the actor publishing.

The reader deliberately does **not** run actor logic, which imposes the modeling rule the concept doc
states as mandatory: interpretation (constants, derived flags) and pure transitions live on the state
record — a shared type that carries its logic to every deserialization site — while actor methods only
apply-write-side-effect. With that rule, off-home reads lose nothing; without it, reader queries and
facade queries can silently disagree. The rejected alternative — read-only replica activations that
load the snapshot and run query methods off-home — would need a hard read-only `IActorState` to be
safe (a non-conflicting write from a replica would silently violate single-homing, and constructors /
`OnActivateAsync` would run their side effects off-home); deferred unless a real workload demonstrates
a query the rich-record rule cannot express.

### What stays out

- **Transparent call forwarding** — the red line; needing it is the Orleans trigger. A direct facade
  call to a single-homed actor from a non-home instance *fails with directions*, never silently hops
  nodes.
- **Per-key leases / placement directory** — without forwarding, per-key ownership turns event
  redelivery into retry roulette (random node per attempt); with forwarding it's a cluster. One role
  for the whole actor tier matches how the tier deploys.
- **Multiple named roles** — deferred until demand; the schema (`role` PK) leaves room without moving
  data.
- **Proactive drain of live single-homed activations on lease loss** — new work is gated immediately;
  in-flight turns are covered by ETag + retry. A drain hook can come later if a real workload needs it.

## Consequences

- Five identical instances self-organize: one is home (actors + event delivery), all publish, reads run
  anywhere; failover is automatic within `LeaseDuration` (immediate on graceful shutdown). No new
  infrastructure, no membership system, no wire contracts for actor methods.
- The failure mode of calling a single-homed facade from the wrong instance is a **pointed error**, not
  mystery latency: `ActorNotHomedException` names the holder and the sanctioned patterns (events,
  `IActorStateReader`).
- Failover has an honest gap: up to `LeaseDuration` with a crashed holder, during which single-homed
  calls fail everywhere and event delivery pauses (messages queue in the outbox — nothing is lost).
  Tighten `LeaseDuration`/`RenewInterval` to trade heartbeat traffic for failover speed.
- Brief double-holding under pathological pauses is tolerated by design and absorbed by ADR-0047; the
  `actor.snapshot.conflicts` counter is the observable if it ever actually happens.
- Scheduled jobs run on a claim-elected node that is generally *not* the home; a job that feeds a
  single-homed actor should publish an integration event (delivered on the home) rather than call the
  facade. Documented; a lease-aware job placement could follow if demand appears.
- The Orleans migration seam is intact: `SingleHome` maps conceptually onto "any grain" (Orleans
  places it), the lease infrastructure and generated delivery-role metadata are deleted rather than ported.
