# ADR-0061: Virtual-sharded actors — fixed role partitions without a cluster

- Status: Accepted
- Date: 2026-07-15
- Related: [ADR-0042](0042-in-memory-actors.md) (actor runtime), [ADR-0048](0048-single-homed-actors.md)
  (single actor home), [ADR-0049](0049-role-leases.md) (role lease primitive), and
  [ADR-0050](0050-role-holder-proxy.md) (role-holder ingress forwarding)

## Context

The single actor home is intentionally simple, but it puts every single-homed actor on one process.
Some applications want a small, static spread — for example, five processes should be able to host
different sets of keyed actors — without configuring the process count or adopting a cluster runtime.

The useful part of that request is ownership partitioning, not automatic balancing. A process can own
several partitions, a new process can acquire an unowned partition after failover, and no activation
directory or migration protocol is needed.

## Decision

`Elarion.Actors` adds an opt-in placement mode:

```csharp
[Actor(Placement = ActorPlacementMode.VirtualShards)]
public sealed class OrderActor(IActorContext<Guid> context) { … }
```

The actor must be keyed. The PostgreSQL recipe
`AddElarionPostgreSqlActorSharding<TDbContext>()` registers 16 virtual shards by default as ordinary
role leases named `actors:shard-0` through `actors:shard-15`. The process count is irrelevant to the
topology. Every process registers the same role set and independently acquires any roles it can hold;
therefore one process may own multiple virtual shards.

The shard is selected by a stable FNV-1a hash of the actor's logical name and canonical key text.
`ActorVirtualShard.GetShardIndex` is public so a shard-aware ingress can calculate the same result.
The runtime checks the corresponding locally cached role lease on every call. A call on a non-owner
fails with `ActorNotHomedException`, including the role and any advertised holder address; actor calls
are never forwarded or retried.

`SingleHome` and `VirtualShards` are placement alternatives. Virtual-sharded actors cannot declare
`[ConsumeEvent]` because the existing outbox delivery path is not shard-routed. Feed them from
shard-aware ingress, or use `SingleHome` plus the existing outbox `DeliveryGate` when integration-event
delivery is the desired topology.

Changing the shard count or role prefix is a topology migration: it changes key ownership. There is no
rebalancing, activation draining, or process-count-based configuration. Needing those features, or
transparent calls from any process, is the Orleans/Akka.NET/Proto.Actor trigger.

## Consequences

- The common actor and single-home defaults do not change.
- A deployment can add processes without changing the configured shard count.
- Role leases provide failover and let one process hold several shards, but they do not guarantee an
  even distribution; ownership follows lease acquisition order.
- Shard-aware HTTP, connection, or worker ingress remains application-composed. The resolver seam is
  provider-neutral, so another coordination backend can replace the PostgreSQL recipe.
- Each virtual shard adds one role heartbeat. The default of 16 is sized for the 1–10-node tier and
  can be adjusted when a workload needs fewer or more partitions.
