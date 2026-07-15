# ADR-0062: Role-affine routing and per-consumer outbox delivery

- Status: Accepted
- Date: 2026-07-15
- Related: [ADR-0049](0049-role-leases.md), [ADR-0050](0050-role-holder-proxy.md),
  [ADR-0061](0061-virtual-sharded-actors.md), and
  [ADR-0022](0022-inbox-idempotent-event-consumers.md)

## Context

A role holder is useful beyond the actor runtime. HTTP ingress may need to reach the holder before
application code executes, while a durable integration-event consumer must execute on the process
that owns its actor, connection, stream sequencer, or other local resource. One event may fan out to
several consumers with different placement requirements, so a single outbox-wide delivery gate has
the wrong grain.

The shared PostgreSQL database already coordinates all Elarion instances. Background delivery does
not need a remote invocation hop: the correct holder can claim the work directly from the database.

## Decision

### Fixed role partitions are a Coordination primitive

`IRolePartition` maps an affinity key to a fixed set of named role leases and returns the role,
holdership, holder id, and advertised address. `AddElarionPostgreSqlRolePartition<TDbContext>()`
registers the partition roles as `{name}:partition-N`. Partition count is independent of process
count, and one process may own several partitions. `IRoleLeaseRegistry` exposes configured leases and
their locally cached `IsHeld` state to role-affine workers.

Actors are an adapter over that primitive. `AddElarionPostgreSqlActorSharding<TDbContext>()` registers
the `"actors"` partition and `IActorPlacementResolver` adds the actor name as an affinity scope. The
same partition can therefore be resolved by actor calls, generated event delivery, and ingress.

### Durable fan-out is one delivery row per consumer

An `OutboxMessage` is the immutable event envelope. Publishing also inserts one `OutboxDelivery`
child per integration consumer, atomically in the publisher's transaction. Each delivery stores:

- a source-generated stable `ConsumerId`;
- an optional `TargetRole` selected from the actual event;
- the envelope's publish time, copied so ordered claims remain a delivery-table index probe;
- its own claim lease, attempts, backoff, completion, and error state.

Workers claim a delivery only when `TargetRole` is null or the local process currently holds that
role. Ordinary consumers remain unbound. Generated actor consumers target the actor-home role for
`SingleHome`, or the key's partition role for `VirtualShards`. Role failover changes claim eligibility
without rewriting pending rows. `OutboxOptions.DeliveryGate` is removed.

Retries and finalization are per consumer. A failed sibling no longer re-runs consumers that already
completed. The original `OutboxMessage.Id` remains the inbox/idempotency key, so at-least-once crash
windows are still absorbed per `(consumer, message)`. Cross-consumer execution order is not promised;
`Order` only makes delivery-row creation deterministic and remains meaningful to the in-memory bus.

Publishing fails before persistence when the event has no registered consumer or a consumer has no
stable id. Every publishing process must therefore have the same generated consumer catalog and the
same role-partition configuration, even when `RunDeliveryWorker` is false. This homogeneous catalog is
the deliberate greenfield contract; dynamically heterogeneous fleets need a real broker/catalog.

### HTTP resolves a role before execution

`UseElarionPartitionHolderProxy(partition, affinityKey, prefixes)` selects a partition role from each
matching request and reuses ADR-0050's one-hop proxy. HTTP needs the holder's advertised address;
outbox delivery does not. The affinity resolver runs before routing and therefore reads the raw path,
query, or headers. A missing key is a `400`; an unknown/unreachable holder remains `503 + Retry-After`.

This is role affinity, not a generic RPC or load-balancing layer. Other legitimate consumers include
device-connection owners, ordered-stream sequencers, and nodes attached to local hardware. Stateless
work and per-item distributed locks remain outside the abstraction.

## Consequences

- Actor `[ConsumeEvent]` works with both `SingleHome` and `VirtualShards` without HTTP forwarding.
- One event can target several roles because placement belongs to each consumer delivery.
- The outbox schema is intentionally breaking: applications add the deliveries table and migrate or
  drain old message-only rows before deployment.
- Role partitions add one heartbeat per partition and are still the small 1–10-node recipe. They do
  not add membership, automatic balancing, activation migration, or transparent actor calls.
- HTTP and background work share target selection but keep transport-specific behavior separate.
