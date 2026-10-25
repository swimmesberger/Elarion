# ADR-0088: Deletions of marked entities are domain events, published with their database cascades

- Status: Proposed
- Date: 2026-10-25
- Related: [ADR-0001](0001-event-transaction-phase.md) (domain events are inline in the caller's transaction),
  [ADR-0002](0002-cross-module-communication.md) (modules do not reach into each other),
  [ADR-0007](0007-data-is-platform-module-as-plugin.md) (the data layer is shared; modules are plugins over it).

## Context

Notes, files and similar data belong to records of many kinds and point at their owner by `(kind, id)` without a
foreign key, so nothing in the database removes them with the owner. A consuming application first enforced the
cleanup with a test that scanned every delete handler, then replaced it with a registry of record kinds and an EF
interceptor that expanded deletions along the model's cascading foreign keys (querying rows the database would cascade
to before the `DELETE`), removed the owned data to a fixed point, and refused `ExecuteDelete`. It worked, but every
modular application with polymorphic attachments rebuilds that machinery, and its order against Elarion's own
interceptors was undocumented.

## Decision

- `[PublishLifecycleEvents]` (Abstractions) marks an entity whose deletion other modules react to — the registration of
  a soft-reference target. `AddElarionEntityLifecycleEvents<TDbContext>()` (`Elarion.EntityFrameworkCore`) attaches one
  stateless interceptor.
- In `SavingChangesAsync` it collects the tracked deletions of marked entities and of every entity whose cascading
  foreign keys reach one, queries the ids the database will cascade to (query filters ignored), and publishes one
  `EntitiesDeleted<TEntity> { Ids }` per marked type on the **inline domain-event plane**, before the `DELETE`, in the
  same transaction. Consumers' own deletions are published in turn, to a fixed point. Save-time cascades and orphans
  (`OnSaveChanges` timing) are applied first; a tracked dependent the save moves to a surviving principal is not
  reported.
- A consumer may save. The nested save continues the outer publication (each id is published once per outermost
  save), so a reused delete command cannot recurse. It flushes the outer deletion early, so it is refused outside a
  transaction.
- Fail closed where the bookkeeping would be wrong: inheritance hierarchies on a cascade path, cascades to an
  alternate key, and a context not resolved from the consumers' scope (a factory, a pool, singleton options) are
  refused with the entity or the cause named, rather than publishing too little.
- Deletion paths that would bypass the event are refused: `ExecuteDelete` on a set that reaches a marked entity (at
  query compilation) and the synchronous `SaveChanges` of such a deletion.
- Ids are `Guid` (the client-assigned key convention, ADR-0038); a marked entity or a cascade path without a single
  `Guid` key is rejected with the entity named.
- The interceptor order of Elarion's EF features is documented and checked: lifecycle events before tenant stamping
  and auditing, which must see what the consumers change. Building a context with either of those already attached
  throws. Interceptors from `AddDbContext`'s own callback (an application soft delete) keep running first.

## Consequences

- The deleting module knows nothing about the data other modules keep on its records; the owning module reacts with an
  ordinary `[ConsumeEvent]`, gated with its module like any consumer.
- A consumer failure fails the save, by the domain plane's rule. Cleanup is atomic with the deletion.
- Cost: one id query per cascade edge and save that deletes a principal on a cascade path, and one publish per marked
  type. A save that deletes nothing marked costs one scan of the tracked entries.
- The interceptor is a single static instance: an `IQueryExpressionInterceptor` keys EF Core's internal service
  provider, so a per-scope instance would build a provider per request. It reaches the scoped bus through the context's
  application service provider and checks that this provider resolves the very context being saved, which rules out
  `AddDbContextFactory`, `AddDbContextPool` and singleton options.
- Raw SQL deletes are outside the guard; an analyzer for them is not attempted.
- Concurrency is not closed: a soft-referencing row, or a cascading dependent, inserted by another transaction after
  the cleanup query ran survives. Applications that need it lock the owner row in both paths (ADR-0087).
- Inheritance is not supported yet; supporting it means keying deletions by the event's marked type and querying
  cascades per derived type.

## Rejected alternatives

- **Integration events.** Cleanup belongs in the deleting transaction; an after-commit consumer leaves orphans when it
  fails and needs its own idempotency.
- **Typed per-entity keys (`EntitiesDeleted<TEntity, TKey>`).** Every Elarion entity key is a `Guid`; a second type
  parameter would burden every consumer signature for a shape the framework does not use.
- **A consumer-side registration (subscribing implies watching).** The interceptor cannot cheaply learn which closed
  event types have consumers, and the guard against `ExecuteDelete` must hold even while the consuming module is
  disabled — the mark belongs on the shared data layer, which ADR-0007 places outside the modules.
