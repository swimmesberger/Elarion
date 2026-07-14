# ADR-0047: Actor state snapshotting (`IActorState<TState>` + `IActorSnapshotStore`, PostgreSQL default)

- Status: Accepted
- Date: 2026-07-10
- Related: [ADR-0042](0042-in-memory-actors.md) (the in-memory actor runtime; named this seam "the obvious
  phase 2"), [ADR-0023](0023-canonical-json-serialization.md) (snapshots serialize through the canonical
  JSON accessor), [ADR-0025](0025-distributed-scheduler-coordination.md) (scale positioning: the default
  targets the one PostgreSQL the app already runs), [ADR-0038](0038-client-assigned-entity-identity.md)
  (why the snapshot row is *not* a client-assigned-key business entity — it is framework plumbing),
  [ADR-0006](0006-incremental-source-generator-conventions.md) (generator conventions).

## Context

ADR-0042 made actor state deliberately ephemeral: passivation drops it, and "anything that must survive
belongs in `OnDeactivateAsync`-flushed / `OnActivateAsync`-loaded storage." That escape hatch works but
pushes real boilerplate and real traps onto every stateful actor:

- each actor hand-writes load/save plumbing (a `DbContext` query in `OnActivateAsync`, a write somewhere);
- flushing **only** on deactivation is a durability bug that reviewers keep missing — a crash between
  mutation and passivation silently loses state;
- nothing guards against the one deployment mistake the single-node runtime cannot tolerate: two processes
  hosting the same actor key, each flushing over the other's writes, last-writer-wins.

Orleans answers this with `IPersistentState<T>`: declarative persisted state, loaded before activation,
written explicitly, ETag-guarded. ADR-0042 already committed to keeping the actor programming model
Orleans-shaped so an outgrown app can migrate to a real cluster; the persistence seam should keep that
migration honest too.

Requirements:

- **Declarative**: an actor states *that* it has durable state, not *how* it is stored.
- **Loaded before the first turn**, so `OnActivateAsync` and every message observe durable state.
- **Explicit writes**: durability points are visible in the actor code (the Orleans semantics), not implied
  by passivation.
- **Fail-loud concurrency**: a snapshot changing underneath an activation is a misconfiguration signal
  (two nodes hosting one actor key), never a silent lost write.
- **Provider-neutral seam, PostgreSQL default** (ADR-0025): ship the implementation for the one Postgres
  the app already runs; a different backend replaces the seam, not the programming model.

## Decision

### The actor-facing contract: a constructor-declared `IActorState<TState>`

Declaring a constructor parameter of type `IActorState<TState>` (in `Elarion.Actors`) gives the activation
snapshot-persisted state:

```csharp
public sealed record FulfillmentState {
    public required string Stage { get; init; }
    public int Attempts { get; init; }
}

[Actor]
public sealed class OrderFulfillmentActor(
    IActorContext<Guid> context,
    IActorState<FulfillmentState> state) {

    public async Task Ship(ShippingInfo info, CancellationToken ct) {
        state.State = (state.State ?? new FulfillmentState { Stage = "new" }) with { Stage = "shipped" };
        await state.WriteStateAsync(ct);     // the explicit durability point
    }
}
```

- **The member surface mirrors Orleans' `IPersistentState<TState>` by name**
  (`State`/`RecordExists`/`Etag`/`ReadStateAsync`/`WriteStateAsync`/`ClearStateAsync`), because the
  member names are where migration friction lives: every durability point is a call site. With the names
  aligned, moving an outgrown actor to Orleans changes the constructor parameter (and adds
  `[PersistentState]`) while the method bodies compile unchanged. The deltas are deliberate,
  call-site-compatible improvements, not drift: `ValueTask` returns (awaits identically), optional
  `CancellationToken` parameters (a strict superset of Orleans' token-less signatures), and a nullable
  `State` instead of Orleans' default-constructed instance — Elarion state types are `required`-property
  records without parameterless constructors, so the actor constructs the initial value and
  `RecordExists` reports whether a stored snapshot backs the activation. Binary/source *interface*
  compatibility (implementing Orleans' actual interface) was rejected: it would put
  `Microsoft.Orleans.Core.Abstractions` into a dependency-light single-node package.
- `WriteStateAsync` persists the whole snapshot; `ClearStateAsync` deletes it; `ReadStateAsync` re-reads
  (the runtime performs that same read once per activation; actors call it only for deliberate
  refreshes). Nothing is written implicitly — **passivation does not flush**. An actor that wants a
  last-chance flush still owns that decision in `OnDeactivateAsync`; making it automatic would turn
  "process exited" into a silent durability boundary.
- The runtime loads every declared state **after construction and before `OnActivateAsync`** (a load
  failure fails the activation like any other activation error, failing queued calls loudly — an actor
  never runs against half-loaded state). Loading is per-activation: a passivated actor re-reads its
  snapshot on the next activation, which is exactly the virtual-actor lifecycle.
- All members run under the actor's single-threaded guarantee; the state object is confined to the
  activation and needs no synchronization.

Mechanically, the registration generator special-cases the parameter the same way it already special-cases
`IActorContext<TKey>`: the emitted activator calls
`ActorStateFactory.Create<TState, TKey>(serviceProvider, context)`, which binds the state to the
activation's identity (`ActorSnapshotKey(actorName, key.ToString())`), registers it with a scoped
`ActorStateTracker` the cell drains before the first turn, and fails the activation with a pointed message
when no store is registered. Statically-typed emission, no open-generic DI, no reflection — the existing
AOT posture (`Elarion.Actors` stays `IsAotCompatible`). Hand-rolled registrations call the same public
factory from their `Activator` delegate.

### The provider seam: `IActorSnapshotStore`

```csharp
public interface IActorSnapshotStore {
    ValueTask<ActorSnapshot?> ReadAsync(ActorSnapshotKey key, CancellationToken cancellationToken = default);
    ValueTask<string> WriteAsync(ActorSnapshotKey key, string payload, string? expectedETag,
        CancellationToken cancellationToken = default);
    ValueTask ClearAsync(ActorSnapshotKey key, string expectedETag, CancellationToken cancellationToken = default);
}
```

- **Keyed by `(actorName, keyText)`** — the same identity the runtime already uses for telemetry and cell
  routing. Key types therefore need a stable, culture-independent `ToString()` (`Guid`, `string`,
  integers); singletons store under `"singleton"`.
- **The payload is canonical-JSON text by contract.** The runtime owns serialization
  (`IElarionJsonSerialization.GetTypeInfo<TState>()`, ADR-0023 — AOT-strict, so a state type missing from
  every source-generated context fails the activation loudly; register it in the module's
  `JsonSerializerContext` like any other module type). The store owns durability and concurrency only.
  A hypothetical binary-format snapshot store would be a different seam, deliberately: text payloads are
  what make snapshots operator-inspectable, and one canonical serialization is the ADR-0023 invariant.
- **ETag-guarded, designed for the strongest impl**: the tag is an opaque string the runtime round-trips
  (a version column stringifies; an Azure Blob ETag passes through unchanged — the `Elarion.Blobs.Azure`
  precedent). `expectedETag = null` means *create* (the key must have no snapshot); a mismatch anywhere
  throws `ActorSnapshotConcurrencyException`. With a correctly single-homed actor a conflict has exactly
  one honest reading — another process hosts the same actor key — so it must surface as an error on the
  failing call, not be retried away (fail-closed, the ADR-0025 posture).
- **Conflict recovery is passivate-and-re-run: the runtime retries the whole turn once, transparently.**
  A caller can do nothing useful with `ActorSnapshotConcurrencyException` — it isn't the caller's fault,
  and its only move is "call again"; the runtime is the one party that can fix the cause (reload state)
  before retrying. So on a conflicted turn the caller is *not* completed: the cell marks itself
  conflicted, drain-closes once pending work reaches zero (the same zero-pending invariant as idle
  passivation, so a replacement activation never runs turns concurrently with the old one), and after
  closing re-enqueues the conflicted items through the host — the turn re-runs once on a fresh activation
  that loaded the winning snapshot, under the still-armed call timeout. Only a **second consecutive
  conflict** (live contention, sustained double-hosting) surfaces to the caller. The misconfiguration
  signal survives the transparency: every conflict logs a warning and increments the
  `actor.snapshot.conflicts` counter, and under sustained double-hosting callers still see failures.
  This deliberately diverges from Orleans (which deactivates but throws `InconsistentStateException` to
  the caller) — a better default the migration can afford, since code written for "conflicts are
  invisible unless sustained" degrades gracefully to "conflicts throw".
  Two corollaries the docs carry: a retried turn executes **twice** (side effects before the failed write
  are at-least-once — the same contract as outbox redelivery, but now also true for direct facade calls
  on the conflict path; side effects that must not repeat belong after the successful write or must be
  idempotent), and an actor that wants to absorb a conflict *without* re-running the turn can still catch
  it and `ReadStateAsync` + reapply inline (the second attempt's conflict is not retried again).
- Full-snapshot operations only. Incremental persistence / event sourcing is a non-goal of this seam; an
  app that needs an event log models it as regular entities written by handlers.

### The PostgreSQL default: `Elarion.Actors.PostgreSql`

One EF-mapped table on the app's existing Postgres, following the established EF-sibling pattern
(`[GenerateElarionSettings]`/`[GenerateElarionIdempotencyKeys]`):

- `ActorSnapshotEntity` → `elarion_actor_snapshots(actor_name, actor_key, state jsonb, updated_on_utc,
  version bigint)`, composite PK `(actor_name, actor_key)`. `jsonb` keeps actor state inspectable with
  plain SQL — the ops story for "what does this actor believe right now".
- `[GenerateElarionActorSnapshots]` on the `[GenerateDbSets]` context emits the DbSet and the
  `OnEntitiesConfigured_…` model-configuration seam (bundled generator, `ELASN001` when `[GenerateDbSets]`
  is missing); `modelBuilder.UseElarionActorSnapshots()` is the hand-written route.
- `AddElarionPostgreSqlActorSnapshots<TDbContext>()` registers the store.
- The store is a **singleton that opens a fresh DI scope per operation**. This diverges from the
  settings/idempotency stores (scoped, riding the handler's `DbContext`) on purpose: actor turns run
  outside any handler scope, and the activation's own scope lives as long as the activation — pinning a
  `DbContext` to it would hold one context (and its identity map) for hours. Snapshot writes are
  independent single-row transactions; they deliberately do **not** join any business transaction (see
  Consequences).
- Writes are change-tracker-free and version-guarded: create is `INSERT … ON CONFLICT DO NOTHING` (zero
  rows → concurrency conflict, and the `ON CONFLICT` form never poisons an outer transaction), replace is
  `ExecuteUpdate … WHERE version = expected`, clear is `ExecuteDelete … WHERE version = expected`. ETag =
  the version rendered as invariant text. Create mints a **lineage-unique random starting version**
  (not 1): version values never repeat across create → clear → re-create lineages of one key, so a
  stale activation's version-guarded write can never silently match a new lineage (the ABA guard —
  every `IActorSnapshotStore` implementation must uphold this, see the seam's XML docs).

### What was considered and rejected

- **Automatic flush on passivation (write-behind).** Turns process death into silent data loss and makes
  the durability boundary invisible in actor code. Orleans made explicit `WriteStateAsync` the semantics
  for the same reason. `OnDeactivateAsync` remains available for apps that consciously want a last-chance
  flush *in addition to* explicit writes.
- **Open-generic DI registration (`IActorState<>` → implementation) instead of generator emission.**
  Would work reflection-style but needs a scoped identity holder mutated by the cell, and open-generic
  instantiation is exactly the runtime-composition shape the repo avoids on framework paths (AOT/trim).
  The generator already emits the activator; one more special-cased parameter kind is the cheaper, fully
  static answer.
- **`IPersistentState`-style named multi-state (`[PersistentState("name")]`).** One state object per actor
  covers the target tier; a name axis doubles the surface (key schema, generator attribute parsing,
  collision diagnostics) for a need nobody has demonstrated. The snapshot key leaves room (`actor_name` is
  the facade name today; a suffixed name could be added by a future ADR without moving data).
- **Event-sourced actor persistence.** A different feature with a different consistency story; ADR-0042
  already points apps that outgrow snapshots at Orleans/Akka.NET journaling. The seam's full-snapshot
  contract keeps it from growing in that direction by accident (ADR-0025: replace the seam, don't grow
  the default).
- **Storing snapshots through `IBlobStore` or the settings store.** Both exist, neither fits: blobs are
  streaming-first and lifecycle-managed (pending/committed is meaningless here), settings are
  key/value-per-user configuration with change notification. A dedicated table with a version column is
  smaller than bending either seam.

## Consequences

- Stateful actors lose their biggest footgun: state is loaded before the first turn, durability points are
  explicit `WriteStateAsync` calls, and a conflicting write never silently loses an update — the runtime
  passivates the stale activation and transparently re-runs the turn once on the winning snapshot;
  sustained double-hosting still surfaces as `ActorSnapshotConcurrencyException` (plus warnings and the
  `actor.snapshot.conflicts` counter), never as silent divergence.
- **Snapshot writes are not transactional with business writes.** A handler that calls an actor which
  writes a snapshot has two commit points; if atomicity with business data matters, the state belongs in
  regular entities written by the handler inside its transaction — the actor is then a coordinator, not a
  store (the concept doc draws this line). This is inherent to actors-as-state-owners, not an
  implementation gap: the actor's turn, not the caller's transaction, is the unit of consistency.
- The migration seam to Orleans stays honest: `IActorState<TState>` member names match
  `IPersistentState<TState>` exactly, so state call sites survive the migration unchanged — the same way
  the facade maps onto a grain interface. (The storage side is *not* mirrored: at migration the app
  adopts Orleans' own storage providers, so `IActorSnapshotStore` is replaced, not ported.)
- Explicit-write semantics mean a crash loses mutations since the last `WriteStateAsync` — by design.
  The doc guidance: write after every state mutation that must survive; treat anything not yet written
  as recomputable.
- The snapshot being readable outside the actor (SQL over `jsonb`; later `IActorStateReader`,
  ADR-0048) imposes a **design rule the docs state explicitly**: the state record is the query
  contract — interpretation (constants, derived flags) and pure transitions live *on the record*, so
  every deserialization site shares the actor's logic; actor methods only apply a transition, write,
  and perform side effects after the write. Logic left in actor methods becomes home-only and lets
  reader-based queries silently diverge from facade queries. Shape evolution follows the same rule:
  tolerance in the record (optional/defaulted properties), not `OnActivateAsync` migration, which
  would fix only the home's view.
- A snapshot read/write is one extra Postgres round trip per activation / per durability point. At the
  1–10-node tier this is noise; an actor hot enough for it to matter batches its writes (several
  mutations, one `WriteStateAsync`) — which the explicit-write model makes natural.
- `Elarion.Actors` gains three public contracts (`IActorState<TState>`, `IActorSnapshotStore`,
  `ActorStateFactory`) but no new dependency; it stays `IsAotCompatible` and transport-free.
  `Elarion.Actors.PostgreSql` is EF-tied and (like the other EF packages) not AOT-flagged.
- The store seam is single-writer-optimistic, not coordinated: it does not make the actor runtime
  multi-node. Clustered single-activation guarantees remain out of scope (ADR-0042) — outgrow this by
  moving to Orleans, carrying the state types along.
