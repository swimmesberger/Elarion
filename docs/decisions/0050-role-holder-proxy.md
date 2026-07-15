# ADR-0050: The role-holder proxy — an in-app ingress rule, not a cluster

- Status: Accepted
- Date: 2026-07-11
- Related: [ADR-0049](0049-role-leases.md) (the role lease whose row carries the holder's address),
  [ADR-0048](0048-single-homed-actors.md) (single-homed actors — whose endpoints this makes reachable
  from every instance), [ADR-0025](0025-distributed-scheduler-coordination.md) (scale positioning).

## Context

With single-homed actors, "live" endpoints (facade-backed queries, the SSE bootstrap) exist or work
only on the role holder. The static answer — route those paths to the worker at the ingress — is
correct but hostile to getting started: a developer who scales a homogeneous fleet
(`docker compose up --scale app=3`) gets `ActorNotHomedException`/404 on two of three instances until
they configure a load balancer. ADR-0048 drew a red line at transparent *actor-call* forwarding
(placement directories, per-method wire contracts, membership — the Orleans product); the question was
whether a DX bridge exists on the near side of that line.

It does, because every hard part of "forwarding" is already solved at a different layer: the endpoints
are **HTTP** (wire contracts exist), the **role lease elects the holder** (no membership protocol), and
both ends are **the same application** (the caller's `Authorization` header replays verbatim — no
inter-node trust to build). The only missing piece was an address.

## Decision

### The holder advertises its address on the lease row

`elarion_role_leases` gains one nullable `address` column. The holder writes it on every
acquire/renew; non-holders — which already read the row on every failed renewal — get
`IRoleLease.CurrentHolderAddress` refreshed at heartbeat cadence. **A hub, not a mesh**: instances
never discover each other, only the holder, and the row remains the entire coordination surface.

The address comes from `RoleLeaseOptions.AdvertisedAddress` (explicit — NAT, proxies, HTTPS between
instances) or, absent that, the new `IInstanceAddressProvider` seam (contract in
`Elarion.Abstractions.Coordination`): `AddElarionInstanceAddress()` in `Elarion.AspNetCore` registers
the best-effort default — the server's first bound endpoint, wildcard hosts replaced by the machine's
first non-loopback IPv4 — the flat-network happy path. Consulted per renewal, never on a request path
(server addresses only exist after the host starts listening; the row fills in on a later heartbeat).

### `UseElarionRoleHolderProxy(role, prefixes)` — the in-app ingress rule

```csharp
app.UseElarionRoleHolderProxy("actors", "/quotes", "/events");
```

Placed **before routing**: the holder falls through after one lock-free `IsHeld` check; a non-holder
forwards matching requests to `CurrentHolderAddress` — method, path, query, headers, streamed body —
and streams the response back (flush-per-read, so SSE flows). Nothing executes locally on the proxy
path, which is what makes it safe: there is no double-execution hazard because there is no partial
local execution to duplicate. The prefix list is deliberately explicit — it *is* the ingress rule the
app will eventually give a load balancer; migration is copying the list and deleting the line.

Failure modes are bounded and loud, never clever:

- **One hop, ever.** Forwarded requests carry `Elarion-Role-Proxied`; an instance receiving one while
  not holding (the lease is mid-failover) answers `503 + Retry-After`, never re-forwards.
- **Holder unknown / not advertising** → 503 naming `AddElarionInstanceAddress`.
- **Holder unreachable** → 503; the address refreshes each renew interval, failover is bounded by the
  lease duration. The proxy never retries or queues.

### Cost model (the standing theme: opt-in cost only)

Apps that never call `UseElarionRoleHolderProxy` are untouched. Apps that call it **without a
registered role lease get an untouched pipeline too** — the extension installs nothing and logs one
line (single-instance mode), so the LiveQuotes sample ships the call from day one at zero cost. With a
lease, the holder pays one volatile-read check per request; only the proxy path (non-holders, matching
prefixes) pays the forwarding hop — the inefficiency is the explicitly accepted price of not
configuring an ingress yet.

### Partition-aware ingress (ADR-0062)

`UseElarionPartitionHolderProxy(partition, affinityKey, prefixes)` applies the same before-routing,
one-hop rule after resolving a fixed role partition from each request key. The resolver reads raw
path/query/header state because routing has not run. A missing key returns 400; a known key follows
the selected role's holder address and loop/failover behavior. This shares role selection with actors
and outbox delivery without turning the actor runtime into a remote-call subsystem. The overload with
an `affinityScope` hashes scope and key as separate components; virtual-sharded actor ingress passes the
logical actor name so HTTP and actor activation cannot select different shards for the same key.

### What stays out

- **Actor-call forwarding** — unchanged red line (ADR-0048). This proxies HTTP requests to a role
  holder; the partition overload may route by an ingress key, but it does not serialize actor calls.
- **Exception-driven transparent replay** (catch `ActorNotHomedException`, re-send the request):
  rejected — by then the handler may have executed side effects locally; replaying means double
  execution. Routing the decision before anything runs is the entire safety argument.
- **Retries, queueing, health-checking, multiple upstreams** — that's a real reverse proxy (YARP, the
  ingress); this is a bridge to it, not a replacement.

## Consequences

- A homogeneous fleet works out of the box: `--scale app=N` serves the "live" prefixes from every
  instance, one hop slower off-home, with the load-balancer migration path spelled out in the call
  itself. The frustrating first contact with single-homing disappears.
- The lease row now carries a reachable address — mild information disclosure inside the database
  trust boundary (same place the outbox payloads live); instance-to-instance traffic defaults to
  plain HTTP on the app's network, with `AdvertisedAddress` as the HTTPS/NAT override.
- Brief failover windows surface as 503s with `Retry-After` on proxied paths — consistent with every
  other lease-derived behavior (delivery gating, actor gating), bounded by `LeaseDuration`.
- The proxy is deliberately unsuitable as a permanent topology at scale: every off-home request costs
  a hop through one instance. The docs frame it as the bridge it is.
