# ADR-0062: Role-affine routing and target-group outbox delivery

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

### Durable fan-out is one envelope per distinct target

Publishing groups the event's ordered integration consumers by their resolved `TargetRole` and inserts
one `OutboxMessage` envelope per distinct target, atomically in the publisher's transaction. A null role
is one ordinary unbound group. Each envelope stores the payload, target role, claim lease, attempts,
backoff, completion, and error state. Envelopes created by one publish share a logical `MessageId`; their
separate `Id` values are lease/finalize identities. When a publish splits, each envelope also stores the
stable consumer ids in invocation order.

The common case is deliberately the original single-row shape: if no consumer declares role routing,
the immutable catalog answers that in O(1), publishing writes exactly one envelope, and no consumer-id
metadata is serialized. Delivery resolves the catalog's already ordered consumer array, deserializes the
payload once, creates one scope, invokes all consumers sequentially, and performs one finalize. This path
does not depend on process count, so single-node and multi-node deployments have identical behavior.

Workers claim an envelope only when `TargetRole` is null or the local process currently holds that role,
and recheck that lease immediately before dispatch. A claim whose role was lost is released without an
attempt or backoff so the new holder can take it immediately. Ordinary consumers remain unbound. Generated
actor consumers target the actor-home role for `SingleHome`, or the key's partition role for
`VirtualShards`. Role failover changes claim eligibility without rewriting pending rows.
`OutboxOptions.DeliveryGate` is removed.

Retries and finalization are per target group. Consumers sharing a target retain source-generated order
and the original outbox's shared failure boundary: if a later consumer fails, an earlier one may run again
when the group retries. Groups targeting different roles finalize independently and do not replay each
other. The shared `MessageId` remains the inbox/idempotency key, so at-least-once crash and group-retry
windows are absorbed per `(consumer, message)` for handler-form consumers.

The request-path benchmark guards the near-zero-cost constraint. On .NET 10/Apple M4 Pro, the no-routing
path measured 561 ns versus 538 ns for the historical single-row baseline with one consumer (1.04×), and
601 ns versus 546 ns with 30 consumers (1.10×). Both allocated 280 B. The consumer-count increase creates
no rows, objects, or allocations; the remaining delta is the fixed telemetry/catalog branch. The benchmark
is `OutboxPublishBenchmarks` and excludes database I/O, where both paths write the same single row.

Publishing fails before persistence when the event has no registered consumer or a consumer has no
stable id. Every publishing process must therefore have the same generated consumer catalog and the
same role-partition configuration, even when `RunDeliveryWorker` is false. This homogeneous catalog is
the deliberate greenfield contract; dynamically heterogeneous fleets need a real broker/catalog.

Method-form consumer ids contain only the service, method, and event type. Framework-injected context
and cancellation parameters do not participate, so adding one does not rename durable deliveries or
inbox claims. Overloads that would produce the same id are a generator error (`ELEVT006`). Retention
purging is envelope-index-driven and deletes completed groups in bounded batches.

### HTTP resolves a role before execution

`UseElarionPartitionHolderProxy(partition, affinityKey, prefixes)` selects a partition role from each
matching request and reuses ADR-0050's one-hop proxy. HTTP needs the holder's advertised address;
outbox delivery does not. The affinity resolver runs before routing and therefore reads the raw path,
query, or headers. A missing key is a `400`; an unknown/unreachable holder remains `503 + Retry-After`.
The scoped overload `UseElarionPartitionHolderProxy(partition, affinityScope, affinityKey, prefixes)`
uses the same two-component hash as actor placement; virtual-sharded actor ingress passes the logical
actor name as `affinityScope`.

This is role affinity, not a generic RPC or load-balancing layer. Other legitimate consumers include
device-connection owners, ordered-stream sequencers, and nodes attached to local hardware. Stateless
work and per-item distributed locks remain outside the abstraction.

## Consequences

- Actor `[ConsumeEvent]` works with both `SingleHome` and `VirtualShards` without HTTP forwarding.
- One event can target several roles because placement is resolved per consumer and persisted per target group.
- The outbox schema is intentionally breaking, but remains one table: applications migrate or drain old
  message-only rows before deployment.
- The common unbound path remains one row and one dispatch/finalize cycle regardless of consumer count;
  payload duplication and extra rows are proportional only to the number of distinct execution targets.
- Role partitions add one heartbeat per partition and are still the small 1–10-node recipe. They do
  not add membership, automatic balancing, activation migration, or transparent actor calls.
- HTTP and background work share target selection but keep transport-specific behavior separate.
