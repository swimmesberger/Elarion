# ADR-0053: Bidirectional client connections — a transport-neutral connection seam; adapters adopted whole

- Status: Accepted (foundation + WebSocket and TCP adapters + conversation helpers + idle hook shipped;
  SignalR adapter and a JSON-RPC-over-connection protocol deliberately deferred until demand)
- Date: 2026-07-13
- Related: [ADR-0044](0044-streaming-requests-and-responses.md) (pre-decided: bidirectional arrives as a
  whole transport over the dispatch seam, never grown default complexity),
  [ADR-0043](0043-client-events.md) (server→client facts; SignalR rejected as the *default*),
  [ADR-0035](0035-protocol-neutral-staged-upload-seam.md) (the precedent this ADR copies: protocol-neutral
  seam, protocol adapters, a second adapter as the seam-validation exercise),
  [ADR-0042](0042-in-memory-actors.md) (use-case #1: an actor owns a stateful connection),
  [ADR-0048](0048-single-homed-actors.md)/[ADR-0050](0050-role-holder-proxy.md) (where a connection-owning
  actor lives and how calls reach it), [ADR-0031](0031-imperative-handler-transport-mapping.md) (transports
  stay concrete and AOT-safe), [ADR-0021](0021-idempotency.md) (the server-side duplicate fence lossy
  transports rely on), [ADR-0052](0052-ordered-streams.md) (sequence-numbered resume — the loss-visible
  ordering vocabulary).

## Context

ADR-0043 and ADR-0044 rejected SignalR **as a default** and pre-decided that a bidirectional transport, if
ever needed, "arrives as a new transport package over the existing dispatch seam — adopted whole." This ADR
is that arrival plan. It exists to settle two things before any code:

1. **The litmus test** — when a bidirectional connection is justified at all, so the topic is not
   relitigated per feature request (the actors "when (not) to use" doctrine, applied to transports).
2. **The foundation contract** — and its one hard rule: **the foundation carries zero transport
   specifics.** SignalR is merely the first adapter. A raw WebSocket adapter, a TCP line-protocol adapter
   for devices, even a UDP adapter for loss-tolerant telemetry must compose over the identical seams
   without the foundation learning a single hub-, socket-, or datagram-ism. This mirrors ADR-0035 exactly:
   `IStagedUploadStore` knows nothing of tus, and `Elarion.Blobs.Azure` proved it by implementing the seam
   with zero protocol knowledge.

What is genuinely unsolved today — the gaps a connection fills and nothing else does:

- **High-frequency client→server traffic where per-message round-trip latency matters** (collaborative
  editing operations, live cursor/input, telemetry ingest at rates where the ADR-0044 batch-command
  pattern's flush window is itself too much latency). The browser makes this structural: `fetch` duplex
  streaming is Chromium-only, so the only interoperable client→server stream from a browser is a
  WebSocket (ADR-0044).
- **The connection as domain state** — presence, device links, protocols where connect/disconnect are
  business events. SSE subscriptions have node-local *interest*, but no first-class connection identity
  with a lifecycle to hang state on.
- **Server→client RPC** — addressing one specific connected client and awaiting its answer. Nothing in
  Elarion does this; SSE is one-way and client events are deliberately fire-and-forget hints.

Everything else SignalR markets is already answered: server→client facts (client events + `pg_notify`),
ordered resumable streams (ADR-0052 — a *better* resume story than SignalR, which has none), client→server
request/reply (JSON-RPC/HTTP/MCP), scale-out (the Postgres fan-out vs. a Redis backplane), transport
fallback (irrelevant in 2026).

A structural observation drives the design: **the framework's guarantee vocabulary is already
lossy-transport-shaped.** Client events are at-most-once hints healed by re-query; ordered streams make
loss *visible* as a sequence gap rather than promising it away; commands are fenced server-side by
`[Idempotent]`. That vocabulary was chosen for flaky browser connections — and it is exactly what an
unreliable datagram transport needs. The foundation therefore promises **no delivery or ordering guarantee
beyond what each existing primitive already states**, which is what keeps UDP honestly implementable.

## Decision

Adapters register a connection first, preserving the established observer view of the live registry,
then await `IClientConnectionProtocol.OnOpenedAsync` exactly once before they deliver regular frames.
Observer failures remain isolated; an opening failure is connection-fatal and follows the normal
`OnClosedAsync` then unregister teardown.

The TCP adapter's establishment order is fixed: raw TCP → optional TLS handshake → application framer →
framed application authentication → registration → `OnOpenedAsync` → regular messages. A TLS failure never
reaches the framer, application authenticator, registry, or lifecycle observers, and a TLS-configured dialer
never retries the same endpoint as plaintext. Platform certificate validation remains fail-closed; an
explicit validation bypass is test-only configuration, not a production convenience.

A connection may register anonymously and later perform one atomic, one-way promotion to an authenticated
identity. Stable connection facts (`ConnectionId`, transport, and establishment time) never change. The
principal, principal id, identity metadata, and revision form one immutable snapshot and are replaced
together through a registry-owned `ClientConnectionState`. Adapters expose that state but cannot mutate it;
only the registry's internal friend API can register, promote, or mark it disconnected. Promotion accepts only anonymous/no-id →
authenticated/non-empty-id; demotion, user switching, and a second promotion are rejected. Inputs are cloned
and metadata is defensively copied and bounded. An in-flight dispatch retains the snapshot captured at its
boundary; a later dispatch observes the promoted snapshot. Promotion observers run after commit with the
same failure isolation as lifecycle observers, and a failure cannot roll the identity back. Existing client-
event subscriptions are disposed on promotion so the peer must resubscribe under the new authorization
context.

TCP framing limits count total unconsumed wire-frame bytes, including prefix, variable header, body, and
trailer. Custom framers are stateless and thread-safe when shared by an endpoint; negotiated state belongs in
a per-connection framer. The adapter validates every framer result before advancing indices, rejects complete
oversized frames as well as incomplete ones, and never grows or reads beyond the configured frame budget.
Outbound framed bytes have a separate hard maximum.

One internal lifetime controller owns each TCP connection's `Open → Closing → Closed` transition, first close
reason, I/O cancellation, writer admission and drain, raw transport abort, once-only protocol close,
once-only unregistration, disposal, and terminal completion. Endpoint shutdown stops accept/redial first,
requests graceful close, waits its configured grace period, force-aborts remaining raw transports, and then
awaits every runner task. It never returns while hidden connection tasks remain alive.

Outbound TCP delivery is bounded FIFO with one physical writer. Capacity includes in-progress work;
saturation fails deterministically and conversation/RPC frames are never silently dropped. Send completion
means the complete framed message was written to the stream, not merely queued. Cancellation before dequeue
withdraws the frame; cancellation or failure during a physical write aborts the connection because a partial
frame may have corrupted stream boundaries. Closing settles every admitted send exactly once. A correlated
request registers before send and removes/faults its pending entry when admission or writing fails.

Connection-to-handler dispatch remains an explicit adapter operation over the existing dispatch rails. It
captures `IClientConnectionSink.Connection` exactly once, seeds the exact `ClaimsPrincipal` and
`ClientConnection` snapshot plus typed application call metadata, and invokes typed unary/stream handlers
through `HandlerInvoker`/`StreamHandlerInvoker`. Named decoded dispatch first requires a route exposed through
`HandlerTransports.Connection`. The helper does not serialize, map protobuf, construct request types through
reflection, translate protocol status codes, or create a second route registry.

### Doctrine: when a connection is justified (mandatory, the transport litmus)

**Default to request/reply + client events.** A bidirectional connection is justified only when the need
matches one of:

1. **Interactive rate** — client→server messages are frequent *and* per-message latency is part of the UX
   (collaboration ops, live input). If a list-carrying batch command flushed every N items / M
   milliseconds is acceptable, it wins: one authorization, one validation pass, one transaction per batch,
   visible in the schema, working over every transport today (ADR-0044).
2. **The connection is the state** — connect/disconnect are domain events, or the link fronts a stateful
   device/session protocol. This is simultaneously actor use-case #1 (ADR-0042): the connection-owning
   actor is the recommended state home.
3. **Server→client RPC** — the server must address one specific client and await a result.

Litmus phrasing: "notify clients that X changed" → client events; "stream ordered outputs" → ordered
streams; "send facts up" → command (batched if hot); only **"the conversation itself is stateful or
latency-interactive"** → connection.

### The foundation: `Elarion.Abstractions.Connections` + `Elarion.Connections`

The neutral tier defines *what* travels — named requests, replies, topic-scoped events — never *how it is
framed, encoded, negotiated, or reconnected*. Contracts (Abstractions, per ADR-0034):

- **`ClientConnection`** — stable connection facts plus the current immutable, revisioned identity snapshot:
  opaque `ConnectionId`, transport, `ConnectedAt`, principal, principal id, and an opaque bounded metadata bag
  (`IReadOnlyDictionary<string, string>` — adapters stuff their specifics here, the foundation never reads
  it). No socket, no hub context, no transport handle. The initial snapshot may be anonymous; promotion
  atomically replaces the whole identity snapshot exactly once.
- **`IClientConnectionRegistry`** — **node-local by design** (the same posture as client-event interest):
  register/unregister, lookup by id, enumerate by principal. It is deliberately *not* a cluster directory —
  authoritative cross-node addressing composes from single-homed actors + the role-holder proxy
  (ADR-0048/0050); wanting a replicated connection directory is the Orleans/SignalR-Service trigger, per
  the ADR-0025 rule.
- **`IClientConnectionObserver`** — `OnConnectedAsync`/`OnDisconnectedAsync` lifecycle seam (Rx-shaped, no
  Rx dep — the client-events observer pattern). Presence, device registration, and the connection-owning
  actor all hang off this.
- **`IClientConnectionSink`** — the per-connection outbound port: `SendAsync(name, payload)` +
  `InvokeAsync<TResponse>(name, payload, ct)` (server→client RPC). The adapter implements it; correlation,
  timeouts-on-the-wire, and framing are adapter-owned. The foundation's only promise: `InvokeAsync`
  completes with the client's reply, a timeout, or a disconnect fault — never silently.
- **`IClientConnectionProtocol`** (kernel, `Elarion.Connections`) — the app-owned **codec seam**, and it is
  deliberately transport-neutral: adapters deliver *complete inbound messages, sequentially, in receive
  order*, and the codec owns the wire encoding behind the sink's neutral legs (fail-loud defaults for the
  legs a codec doesn't speak). What differs per transport is *how bytes become messages* — WebSocket frames
  natively, a TCP adapter owns the framing (length-prefix/delimiter) before the codec sees anything, a
  datagram transport delivers each datagram as one message — so the same codec runs over any adapter, and
  only the handshake surface and raw send legs are per-adapter types.

`Elarion.Connections` (the neutral runtime kernel, dependency-light) ships the registry default, the
observer dispatch, and the client-events bridge below. It references Abstractions,
`Microsoft.Extensions.*` Abstractions, and `Elarion.ClientEvents` (itself transport-neutral and
AOT-compatible — the bridge composes the shared subscribe resolver rather than forking it) — no ASP.NET,
no SignalR, no sockets.

### Inbound: nothing new — the dispatch seam already is the foundation

A client→server message over a connection is a **named dispatch**, and that seam exists:

- **Request/reply** routes through the shared `HandlerDispatcher` — the same registry JSON-RPC and MCP
  adapt, built once by `RegisterHandlers`. The full decorator pipeline (tracing → authorization → feature
  gate → validation → `[DefaultPipeline]`) runs per dispatch; module gating comes for free. A new
  transport-neutral flag **`HandlerTransports.Connection`** (included in `All`) names the exposure surface
  once for *all* connection adapters — there is deliberately no per-protocol flag, because "callable over a
  live connection" is the semantic decision; which framing carries it is not.
- **Fire-and-forget ingest** is a command whose reply the adapter discards (JSON-RPC notification shape).
  ADR-0044's rule stands: a fact arriving from a client is a command; the connection removes per-call HTTP
  overhead, it does not create a new handler contract. No `ChannelReader` handlers.
- **Principal seeding rides the existing dispatch-scope rail**: the adapter captures the current immutable
  identity snapshot at the message boundary and seeds that principal and `ClientConnection` into the dispatch
  scope via `DispatchScopeContext`/`IDispatchScopeInitializer` — the same mechanism the outbox uses to seed
  `MessageId`. The initial snapshot may come from connect-time authentication or be anonymous until a framed
  application exchange promotes it. Authorization evaluates per dispatch against the captured claims; a
  promotion racing that dispatch affects the next call, never changes the current scope underneath it.
  Credential *expiry* terminating the connection is adapter policy. Client-event subscription authorization
  is reevaluated by disposing subscriptions on promotion and requiring the peer to resubscribe.

Corollary worth naming: because `JsonRpcDispatcher` is ASP.NET-free by design (ADR-0017's protocol/host
split), a socket adapter that frames JSON-RPC over TCP or WebSocket needs almost nothing beyond the
connection contracts — JSON-RPC has always been transport-agnostic (LSP runs it over stdio). That is the
cheapest possible seam validation, and the reason inbound gets no new machinery.

### Outbound: client events gain a delivery leg, not a competitor

Server→client facts keep exactly one semantic model. A connection adapter registers as an **additional
local delivery leg for client events**: subscriptions arriving over a connection go through the same topic
catalog, the same fail-closed subscribe-time authorization, the same greeting/interest lifecycle, and the
same `pg_notify` fan-out as SSE subscribers — the node holding the connection delivers, so **no SignalR
backplane exists in this design**; the Postgres broadcaster already is one. The SSE transport and a
connection adapter are peers over `IClientEventLocalDelivery`-level seams; a browser may hold either. No
second topic system, no hub "groups" (a group is a topic; a per-resource group is a `Resource` scope).

Ordered streams stay as decided in ADR-0052 (sequence-numbered, resume from the producer's ring); a
connection adapter may carry a stream's frames, but sequencing/resume semantics live in `StreamHub<T>`,
never in the transport.

### Server→client RPC: node-local port, home-routed composition

`IClientConnectionSink.InvokeAsync` is the new primitive, and it is deliberately **node-local**: you can
invoke a client you hold. Reaching a client held elsewhere is not a foundation feature — it composes from
the shipped pieces: the connection-owning actor is `[Actor(Placement = ActorPlacementMode.SingleHome)]`, inbound decisions route
to the home via the role-holder proxy, and the home node holds both the actor and the connection. A
replicated invoke-anyone directory is the cluster trigger (swap to Orleans/SignalR Service), not a
foundation growth path.

Two multi-node deltas are acknowledged and deliberately deferred, not designed away:

- **Connection ingress must co-locate with the home.** A single-homed actor and a connection that landed
  on a different node do not meet (no actor forwarding by design, ADR-0048). The documented posture is
  that connection routes belong under the ADR-0050 home-routed prefixes; whether the role-holder proxy
  forwards a WebSocket *upgrade* (it streams SSE today) is unverified — until it is, the multi-node
  answer for device links is "point devices at the home" and the single-node answer covers the target
  tier (1–10 nodes, and the IoT shape is 1,000–3,000 devices — one node carries it).
- **No actor placement or balancing.** Single-homing puts *all* actors of a role on the one lease-holding
  node; there is no "spread 3,000 device twins evenly across 3 nodes." That is sharding — a placement
  directory, rebalancing on membership change, and connection draining — and it will not grow out of the
  single-home default (ADR-0025). When a deployment actually needs it, the choices are, in order:
  use a fixed role partition (ADR-0062 — `"actors:partition-0"`…`"actors:partition-2"`, key→partition by
  stable hash, each shard's prefix home-routed), or the Orleans trigger (real placement, activation
  rebalancing, and directory — adopted whole). ADR-0061 now promotes the fixed partitioned-role
  pattern as an opt-in actor recipe; connection ingress still needs an application-composed
  shard-aware route and remains outside the connection kernel.

### Guarantees and lossy transports

The foundation states, per primitive, only what already holds: events over a connection are at-most-once
hints (re-query heals); streams are sequence-numbered (loss is a visible gap); inbound commands are fenced
by `[Idempotent]` when the adapter may retransmit; `InvokeAsync` is at-most-once with an explicit fault. A
reliable adapter (SignalR, TCP) simply never exercises the loss cases; a UDP adapter implements the closest
semantics and documents the delta — the "seams designed for the strongest impl" rule read in reverse:
the *guarantee floor* is set so the weakest honest transport can stand on it.

### Observability and sensitive data

Connection telemetry uses bounded stage and outcome vocabularies. TLS handshake, framing rejection, identity
promotion, outbound saturation, idle activity, and graceful/forced shutdown are observable, but metric tags
never contain connection or principal ids, payloads, endpoint addresses, operation names, certificate
subjects/thumbprints, arbitrary exception type names, or raw exception messages. Duration histograms record
floating-point seconds with semantic-convention bucket advice. Sensitive TLS material, credentials, private
keys, certificate passwords, and decrypted application bytes are never logged.

### Encoding

The neutral tier is encoding-agnostic: contracts are the typed DTOs + wire names + DataAnnotations already
exported to `rpc-schema.json`. JSON adapters bridge `IElarionJsonSerialization` (canonical JSON, AOT-strict).
A binary adapter (MessagePack, a device line protocol) owns its encoding but encodes the *same* contracts —
the schema remains the single contract description. No foundation serializer interface beyond what exists.

### Field pressure: what two real gateways confirm and what they defer

Two production-shaped IoT gateways (private consuming projects, deliberately unnamed here) were held
against the contracts: a consumer-appliance hub bridging a Bluetooth device over WSS, and an industrial
equipment gateway speaking a vendor telegram protocol over raw TCP — delimiter-framed ASCII telegrams,
per-direction crash-surviving sequence numbers, one-confirmed-in-flight, and four parallel channels per
device (control / visualization / condition-monitoring / logging). Confirmed: multi-channel conflation is
the `PrincipalId` model (each channel one registered connection under the device's id; cross-channel
ordering is *not* required, per-connection ordering is — the adapter's single receive loop); framing is
adapter-owned; the twin/projection push to UIs is the client-events story. Learned, and recorded as
adapter-tier requirements or explicit non-absorptions:

- **Dial-out connections.** A gateway may *initiate* the TCP connection to the device (with
  reconnect/backoff), not just accept. The kernel is indifferent — registration is identical — but a TCP
  adapter must ship both listen and dial modes; "accept loop" generalizes to "establishment loop."
  (Shipped: `Elarion.Connections.Tcp` has both, dial-out with jittered exponential backoff.)
- **Synthetic connections are first-class.** The industrial gateway has a proxy tap that injects telegrams
  arriving via a side channel (no socket, no adapter framing). This works *because* the kernel contracts never assume a
  socket: any app object implementing `IClientConnectionSink` can register, observe, and bridge. Keep it
  that way — it is a load-bearing property, not an accident.
- **`InvokeAsync` is the simple tier, deliberately.** Real device conversations can be multi-telegram
  flows with terminal-observation semantics (wait-for-any, race, step sequences) over stateful send
  coordination (persisted sequence numbers, SYN re-sync, confirmation timeouts). None of that is
  absorbable without owning the protocol; it lives inside the codec/actor stack, for which the codec seam
  is the mounting point. The kernel ships two **optional** conversation helpers both gateways hand-rolled —
  `ConnectionInbox<TMessage>` (predicate waiters over buffered inbound messages, timeout, completion
  faulting) and `ConnectionPendingRequests<TKey, TResponse>` (the sequence-number → completion map, the
  natural backing for a codec's `InvokeAsync`) — helpers a codec may use, never machinery the foundation
  requires.
- **Idle hook.** Both gateways generate protocol-level keepalives from an idle timer (20 s poll; 60 s
  DUM). Shipped as the optional `IdleTimeout` adapter option calling the codec's `OnIdleAsync` per elapsed
  idle window (default no-op; throw there to declare the link dead) — the pending read is threaded across
  idle ticks, never abandoned.

### Package layout and adapter rules (pre-decided)

- `Elarion.Abstractions.Connections` — contracts above; no new deps.
- `Elarion.Connections` — neutral kernel: registry default, observer dispatch, client-events bridge.
- `Elarion.Connections.AspNetCore` — **the first adapter, and the seam-validation exercise (the ADR-0035
  Azure move), device-shaped by demand**: a raw WebSocket endpoint (`MapElarionConnectionSocket`-style)
  whose accept loop does exactly what every device gateway hand-writes today — handshake via an app-owned
  authenticator seam (HTTP-level token or in-socket challenge/response; the seam hands back the
  authenticated principal + principal id, the adapter mints the `ClientConnection` and registers it),
  framing hooks for inbound dispatch and the outbound sink, and the client-events bridge wired through.
  Real IoT consumers (device links over WSS with proprietary payloads) reduce to
  codec + authenticator; the accept/register/bridge boilerplate becomes framework. Per-connection
  settings (`ConfigureConnectionAsync(HttpContext)` — limits/idle/keep-alive/transport from the upgrade
  request) mirror the TCP adapter's; the runtime endpoint manager deliberately does not — for HTTP
  transports the route *is* the binding, so bindings-as-data compose from a wildcard route + a
  per-connection configuration lookup, no port management needed. Because it is a real
  second-dissimilar transport over the identical seams, it *is* the neutrality proof — the foundation is
  not done until it exists.
- `Elarion.Connections.Tcp` — the raw-socket adapter for proprietary device protocols: hosted **listener**
  and **dial-out** (jittered exponential reconnect) services over the same handler/codec seams; because
  TCP has no message boundaries, the adapter owns a framing seam (`TcpMessageFramer`) with
  length-prefixed and delimited built-ins — framing is boundaries only: bytes are bytes on TCP, every inbound message is a raw slice on the codec's binary leg, and text decoding is the codec's own one-liner (WebSocket keeps both legs because text-vs-binary is a real frame property there). Framing and
  limits are configurable **per connection** (`ConfigureConnectionAsync` — resolved from the peer before
  any byte, so one ingress port serves differently-framed device families; a field requirement of the
  industrial gateway's binding configuration). Bindings-as-data are first-class: the
  `TcpConnectionEndpoints` runtime manager applies/removes named endpoints from configuration at any
  time — re-applying reconnects under the new settings, **including flipping a binding's direction**
  (listen ↔ dial), and every endpoint **advertises its state** (`Statuses`/`StatusChanged`: bind failures
  surface as `Faulted` with the reason, dial retries as `Dialing` with the last error — operator-visible
  state, not just a log line) — both field requirements of the industrial gateway's binding model. BCL sockets only — no ASP.NET.
- `Elarion.Connections.Simulation` — the simulation/test tier both field gateways hand-rolled as
  simulators: an in-memory sink double (valid *because* the contracts never assume a socket), awaitable
  lifecycle observers, and a framed TCP simulator client over the same framer seam.
- `Elarion.AspNetCore.SignalR` — **adopted whole, after the socket adapter**: one framework-owned hub
  adapting inbound to `HandlerDispatcher` and outbound to `IClientConnectionSink`, serialization bridged
  to canonical JSON, auth from the host's ASP.NET authentication at negotiate/connect. It contains every
  SignalR-ism (negotiate, hub protocol, reconnect tokens) and *only* SignalR-isms. Not `IsAotCompatible`
  if the hub layer demands it — the foundation stays clean either way.
- Diagnostics prefix `ELCON`. Naming note: "session" is deliberately avoided (`Elarion.Session` is the
  ADR-0030 capability bootstrap); the concept is a **connection**.

## Alternatives considered

- **SignalR as its own integration, contracts shaped by hub concepts** (hubs, groups, `IHubContext` in app
  code). Rejected — every consumer would couple to one vendor protocol; groups duplicate the topic catalog;
  `IHubContext` in a handler is a transport leak the same way `HttpContext` would be. The whole point of
  ADR-0043/0044's "adopted whole" clause is that the *adapter* is swappable.
- **A replicated connection directory / framework backplane** (invoke any client from any node). Rejected —
  that is a cluster membership feature; ADR-0048/0050 already provide the single-homed composition, and
  past it the answer is Orleans or Azure SignalR Service, per ADR-0025.
- **New streaming/channel handler contracts** (`ChannelReader<T>` parameters, duplex handler interfaces).
  Rejected in ADR-0044 and not reopened: inbound stays per-message dispatch; outbound stays client
  events/streams. The connection is a *carrier*, never a handler shape.
- **Widening `HandlerTransports` per protocol** (`SignalR`, `Tcp`, …). Rejected — the flag gates a semantic
  exposure surface; one `Connection` flag covers all adapters, and an app that must distinguish adapters is
  hosting two conversations, not two flags.
- **Foundation-level reliability** (acks, redelivery, per-connection durable queues). Rejected — ADR-0043's
  broker-per-browser-tab argument; reliability beyond the primitives' stated guarantees is a product tier
  (Ably/Pusher/SignalR Service) or adapter-internal (TCP already is one).

## Non-goals

No presence *store* (node-local registry + observer; durable presence is app-owned state — a table or a
connection-owning actor). No CRDT/OT collaboration primitives. No transport negotiation or fallback
ladder. No reconnection state machine in the foundation (adapter-owned; client events already self-heal via
`elarion.connected`, streams via resume). No generated per-adapter TS client in v1 — the SignalR JS client
or a raw WebSocket speaks the same wire names the schema already describes; generator support is a
follow-up once one adapter is real.

## Consequences

- Nothing ships with this ADR; it fixes the litmus test and the foundation contract so implementation can
  proceed as stacked PRs: contracts → `Elarion.Connections` kernel + client-events bridge →
  `HandlerTransports.Connection` gating → `Elarion.Connections.AspNetCore` (the device-shaped WebSocket
  adapter, doubling as the seam-validation exercise) → `Elarion.Connections.Tcp` → `Elarion.AspNetCore.SignalR`
  — in that order. The socket adapters moved ahead of SignalR when real IoT consumers (device gateways
  over WSS and raw TCP with proprietary payloads) showed they are the higher-demand transports; the TCP
  adapter also completes the seam validation with a second dissimilar transport over unchanged kernel
  contracts.
- SignalR becomes *available* to consuming apps without ever becoming the default: SSE + JSON-RPC remains
  the shipped happy path; the connection tier is opt-in by package reference, and the doctrine section is
  the review checklist for reaching for it.
- The client-events invariants survive verbatim: one topic catalog, one authorization path, one re-query
  contract, one Postgres fan-out — regardless of whether a subscriber arrived over SSE or a connection.
- Accepted asymmetries: server→client RPC is node-local (home-routed composition for cross-node);
  connection-carried handler calls are invisible to OpenAPI (they are the same handlers — the schema
  already describes them); a UDP adapter documents its delivery deltas rather than the foundation
  weakening its vocabulary.
- The seam-validation adapter is part of the definition of done, not an optional extra — it is what keeps
  "transport-neutral" a tested property instead of an intention.
