# ADR-0043: Client events — after-commit facts projected to the browser over SSE

- Status: Accepted
- Date: 2026-07-06
- Related: [ADR-0001](0001-event-transaction-phase.md) (the two event planes this deliberately does **not**
  extend), [ADR-0002](0002-cross-module-communication.md) (published contracts at boundaries — the client is
  the least-trusted boundary), [ADR-0010](0010-event-bus-is-pub-sub-only.md) (pub/sub-only bus),
  [ADR-0022](0022-inbox-idempotent-event-consumers.md) (the delivery guarantees a browser cannot honor),
  [ADR-0024](0024-postgres-listen-notify-settings-changes.md) (the LISTEN/NOTIFY pattern the cross-node
  fan-out copies), [ADR-0025](0025-distributed-scheduler-coordination.md) (replace the seam, never grow the
  default), [ADR-0026](0026-openapi-http-transport.md) (own only what Microsoft can't),
  [ADR-0030](0030-client-capability-bootstrap.md)/[ADR-0032](0032-frontend-contribution-model.md) (opt-in by
  enumeration; UX projection is never security), [ADR-0031](0031-imperative-handler-transport-mapping.md)
  (concrete `MapElarionX(route)` per REST surface), the
  [events](../capabilities/events/index.mdx) capability docs.

## Context

Applications need near-realtime UI: an invoice list that updates when another user pays an invoice, progress
for a long-running import, a dashboard that reflects changes made on another node. Elarion has every piece of
the server-side story — commit-gated integration events with at-least-once delivery and an inbox
(ADR-0001/0022), cross-node signaling over LISTEN/NOTIFY (ADR-0024), and a typed schema-to-TypeScript chain —
but no way to push a fact to a connected browser. Apps hand-roll SSE endpoints per feature or fall back to
polling.

Three constraints shape the design:

1. **The trust boundary.** Integration events cross *module* boundaries inside the trusted system: every
   consumer is compiled-in code with full trust and no `ICurrentUser`. A browser crosses the *trust*
   boundary: a dynamic population of principals, each entitled to a different subset of events. Plane B has
   no vocabulary for "who may observe this" because inside the trust boundary the question does not exist.
2. **The guarantee gap.** `IIntegrationEventBus` states its contract in the interface: recorded in the unit
   of work, delivered after commit, retried until the consumer succeeds, deduped by the inbox. A browser is
   ephemeral — a closed laptop misses events, period. Delivery to a client is **at-most-once**, and no
   framing may silently break the plane's stated guarantee for one subscriber class.
3. **Scale positioning (ADR-0025).** The default must cover ~1–10 nodes on the one PostgreSQL the app
   already runs. Durable per-client queues with acknowledged cursors — what honoring at-least-once to a
   browser would actually take — is a message broker per browser tab; that tier is a product
   (Ably/Pusher/SignalR Service), not an Elarion default.

## Decision

### Model

**Client events are not a third plane.** The type level keeps the one distinction a publisher cannot
delegate — in-transaction (`IDomainEvent`) vs after-commit (`IIntegrationEvent`) — because whether a consumer
failure rolls back the command is publisher-side semantics, visible at the publish call site. Downstream of
commit, audience routing is a *topic* concern: a client event is a **named topic whose schema is a deliberate
wire contract** — the client-side analogue of `[ModuleContract]`.

**Push is a hint, not a source of truth.** Client events are light (ids/refs, not state); the client
converges by re-running normal query handlers, which carry the real `[Require*]` gates. Delivery is
at-most-once by definition; the transport signals "you may have missed things" (`elarion.connected`) after any
gap and the client re-queries. This single rule eliminates durable replay, per-client cursors, and ordering
machinery from the design.

### Contracts (`Elarion.Abstractions`)

- `IClientEvent` — marker interface on the contract DTO (immutable record). It marks what the type *is*: a
  topic schema at the trust boundary. The topic name follows the `{module}.{name}` shape (camelCase, like
  handler names; a trailing `Event` suffix stripped). Under `[UseElarion]`/`[GenerateClientEventTopics]` the
  registration is **generated per module** (`Add{Module}ClientEvents`, wired into `ConfigureDefaultServices`
  so topics disappear with the module's feature gate): the optional `[ClientEvent("name")]` attribute
  overrides the full topic name, and the contract's `[RequirePermission]`/`[RequireRole]` attributes become
  subscribe-time requirements. Diagnostics: `ELCEV001` (client event under no module), `ELCEV002` (colliding
  topic names), `ELCEV003` (contracts declared without referencing `Elarion.ClientEvents`). Explicit
  `AddElarionClientEvents(e => e.AddTopic<T>("module.event"))` remains the manual path (the
  `AddElarionVariantCatalog` pattern) — either way an unregistered publish fails loud and an unknown topic
  does not exist on the wire; the catalog rejects duplicate names/types at composition.
- `IClientEventPublisher.PublishAsync(evt, scope, ct)` — interface publishing, symmetric with the two buses.
- `ClientEventScope` — data, not behavior: `Global`, `User(id)`, `Resource(key)`.
- Subscribe-time authorization is declared **on the contract type** with the existing attributes
  (`[RequirePermission]`/`[RequireRole]`/`[RequireClaim]`); the transport evaluates them when a client
  subscribes to the topic. Nothing reaches the wire without a declared type — opt-in by enumeration, the
  `[ClientFeatures]` principle.

### Publishing

The recommended path is a **hand-written projection consumer** — a method-form `[ConsumeEvent]` on a
`[Service]` that maps the internal integration event to the client contract:

```csharp
[Service]
internal sealed class InvoicingClientProjections(IClientEventPublisher clientEvents) {
    [ConsumeEvent] // post-commit, lightweight side effect — commit-gating inherited
    public ValueTask On(InvoicePaid evt, CancellationToken ct) =>
        clientEvents.PublishAsync(
            new InvoiceChanged { InvoiceId = evt.InvoiceId },
            ClientEventScope.Resource($"customer:{evt.CustomerId}"), ct);
}
```

This keeps the command handler audience-blind (one `IIntegrationEventBus` injection, one publish — audience
routing is a *subscription* concern, not a publish argument), inherits commit-gating from Plane B delivery,
and makes the trust-boundary crossing one visible, reviewable line where fields that must not leak are
dropped. There is **no generated forwarder** — the mapping is the module's hand-written concern, the ADR-0002
price paid for the same reason.

Calling the publisher directly from a handler is legal and *is* the **ephemeral tier** (progress reporting):
immediate, at-most-once, not commit-gated — the semantics are stated by where the call sits, exactly how the
two planes explain themselves.

### Delivery

- **Transport: Server-Sent Events**, via a concrete `MapElarionClientEvents(route)` in
  `Elarion.ClientEvents.AspNetCore` (its own host sibling, the `Elarion.Blobs.AspNetCore` precedent — the
  base host package stays free of the client-events dependency; ADR-0031 style — no generic map,
  RDG/AOT-safe), built on the framework-native `TypedResults.ServerSentEvents` (own only what Microsoft
  can't). Plain HTTP — works through every proxy/auth setup the app already has, browser-native reconnect,
  no new wire protocol. Payloads are canonical JSON written as-is (`SseItem<string>` — serialized once at
  publish); the SSE event name is the topic name. Control signals are named `elarion.*` events:
  `elarion.connected` when the stream opens — every occurrence means "you may have missed events" — re-query;
  a cross-node backend re-delivers the same event after a delivery gap, so there is one re-query contract,
  not a separate resync event — and `elarion.keepAlive` on idle so proxies keep the connection open. The
  names are the public `ClientEventControlEvents` constants.
- **Subscription** = (topic, scope), passed as query parameters on the SSE `GET` (EventSource cannot POST);
  changing the subscription set reconnects. Server-side filtered: `User` scope is always derived from
  `ICurrentUser` — a client can never request another user's scope. `Resource` scope goes through an
  `IClientEventSubscriptionAuthorizer` seam and is **fail-closed**: no authorizer registered → resource
  subscriptions denied. *Addendum (2026-07-11):* a topic whose resource segment is a **routing key, not an
  entitlement** (a stock symbol, a public room id) declares `[AllowAnyResource]` on the contract (or
  `AllowAnyResource()` on the topic options) and skips the seam once the topic's requirements pass —
  per-topic by design, because the authorizer seam is global and a blanket `return true` implementation
  would silently open every future entitlement-scoped topic. Module-gated: a disabled module's topics do
  not exist.
- **Cross-node fan-out** (`Elarion.ClientEvents.PostgreSql`): LISTEN/NOTIFY, copying the ADR-0024 listener —
  one dedicated connection per node, `pg_notify` on a pooled connection per publish (no transaction gating
  needed: the recommended producer already runs after commit, and the direct-publish tier is pre-commit by
  design), exponential-backoff reconnect. The publishing node receives its own events through the same
  loop-back, keeping one delivery path. After a reconnect the node delivers `elarion.connected` to every
  local subscriber (`IClientEventLocalDelivery.DeliverToAll`) — the client-facing analogue of the settings
  listener's `FireAll()` — because PostgreSQL does not queue notifications for absent listeners.
  Light events keep payloads under the ~8 KB NOTIFY cap by construction; an oversized publish is dropped
  loudly on the publishing node instead of delivering on some nodes and not others. Past ~10 nodes: replace
  the broadcaster seam (Redis/broker), never grow the default.
- **In-process default** (`Elarion.ClientEvents`): subscription registry + broadcaster, single-node correct
  with zero configuration.

### Schema and TypeScript chain

The declared topics flow into `rpc-schema.json` as an `events` block — each topic with its payload schema,
supplied to the exporter via the DI-resolved `ClientEventTopicManifest` (the `ClientCapabilityManifest`
pattern; omitted when empty → byte-identical schemas, the ADR-0032 precedent). The TS generator emits
`events-client.ts`: topic-typed accessors, Zod-validated payloads (schemas in `rpc-schemas.ts`), and an
`EventSource` wrapper multiplexing the subscriptions over one connection:

```ts
const events = createElarionEvents({ url: "/events" });
events.invoicing.invoiceChanged.subscribe(
  { resource: `customer:${customerId}` },
  (evt) => queryClient.invalidateQueries({ queryKey: ["invoices", evt.invoiceId] }),
);
// elarion.connected fires on every (re)connect — the "you may have missed events" re-query hint.
events.$client.onConnected(() => queryClient.invalidateQueries());
```

### Package layout

| Package | Owns |
| --- | --- |
| `Elarion.Abstractions` | `IClientEvent`, `IClientEventPublisher`, `ClientEventScope`, `IClientEventSubscriptionAuthorizer`, `ClientEventSubscription` |
| `Elarion.ClientEvents` | explicit topic catalog, canonical-JSON publisher, in-process subscription registry + broadcaster default (single-node correct) |
| `Elarion.ClientEvents.PostgreSql` | LISTEN/NOTIFY fan-out, resync-on-reconnect (not `IsAotCompatible`-constrained beyond its Npgsql dep) |
| `Elarion.ClientEvents.AspNetCore` | `MapElarionClientEvents(route)` SSE endpoint |
| TS client generator | `events` schema block → typed subscription client |

## Alternatives considered

- **`[ClientEvent]` attribute on integration events** (an attribute makes the event flow to clients).
  Rejected twice over: attributes in Elarion are declarative metadata generators read, never a mechanism
  that silently causes delivery; an attribute cannot compute per-instance channel keys
  (`customer:{id}`); and it welds the internal event type to the public wire — every internal rename
  becomes a breaking client change (the ADR-0002 coupling).
- **Clients as remote Plane-B subscribers** ("a browser is just another integration-event consumer").
  Right about the plumbing — the bridge *is* a consumer — wrong about the contract: one subscriber class
  would silently get the opposite delivery semantics from what `IIntegrationEventBus` promises, and Plane B
  has no per-principal authorization vocabulary. The client subscribes to a *published projection*, not to
  the integration event.
- **Topics on `IIntegrationEventBus`** (`PublishAsync(evt, [Internal, Client(scope)])`). Rejected — it puts
  audience knowledge back into every publish call site (handlers computing client channel keys), the
  interface can no longer state one guarantee ("depends which topic"), and every broker backend
  implementing the seam would have to know browsers exist. Audience routing is a subscription concern;
  the projection consumer *is* "adding the client topic", expressed as a subscriber.
- **A third plane / `IClientEvent` as a plane marker.** Rejected — planes encode the relationship to the
  transaction, the one distinction upstream of commit. Client visibility is downstream-of-commit audience
  routing; conflating them blurs both.
- **SignalR.** Rejected as the default — a hub protocol plus sticky-session/backplane concerns (the
  recommended backplane is Redis, colliding with the one-Postgres positioning), for a system that only
  needs the server→client half; client→server is already covered by JSON-RPC/HTTP/MCP. WebSockets remain a
  future *replaced transport seam*, never grown default complexity.
- **Durable replay / `Last-Event-ID` resumption.** Rejected for v1 — honoring it means per-client durable
  cursors over retained events. The hint-plus-resync model makes reconnect correctness a re-query, which
  the client must implement anyway.
- **A generated 1:1 projection forwarder.** Rejected for now — same reasoning as ADR-0002's rejection of
  generated cross-module forwarders; the hand-written line is where boundary review happens. Generator
  sugar over the consumer side can be revisited if real modules show the ceremony is felt.

## Non-goals

No third event plane. No bidirectional transport (SignalR/WebSockets). No delivery guarantees to the
browser. No durable replay or ordering guarantees. No presence, collaboration, or CRDT primitives. No
generated integration-event forwarder. The S3-style rule applies: these are where realtime frameworks go to
bloat, and each is either app-owned or a future seam replacement.

## Composing the pattern by hand (without the packages)

Every semantic piece also exists in independently shipped seams; a single-node app can compose the pattern by hand: a
method-form `[ConsumeEvent]` on a `[Service]` (post-commit for free) pushing into per-subscriber
`System.Threading.Channels`, drained by a hand-written SSE endpoint mapped through the module's endpoint
hooks or `[ModuleEndpoints]` (ADR-0040). Multi-node today: sticky sessions, or the app copies the ADR-0024
LISTEN/NOTIFY pattern itself. See the
[client events recipe](../capabilities/events/client-events.mdx). For genuinely "near"-realtime dashboards,
polling a keyset query remains the humble alternative at this tier.

## Consequences

- The two-plane story stays intact and gains a crisp second axis: **types encode the transactional
  relationship; topics/scopes route audiences after commit.** Per-audience contract types are topic
  schemas, not guarantee markers.
- A module's client-visible surface is enumerable in one place (its projection service + `IClientEvent`
  records), the same way `[ModuleContract]` documents its cross-module surface. Opt-in by enumeration
  holds: an internal event can never leak to the wire by accident.
- The at-most-once downgrade happens at one visible line (the projection publish), not silently inside a
  plane that promises otherwise; `IIntegrationEventBus` remains implementable by any backend that delivers
  events, unmodified.
- Costs accepted: one small projection class per module (the ADR-0002 price), a reconnect when the
  subscription set changes, and three new diagnostics (`ELCEV001`–`ELCEV003`).
- Rollout is bottom-up stacked PRs, each independently useful: Abstractions contracts → `Elarion.ClientEvents`
  core → SSE endpoint → schema/TS chain → PostgreSQL fan-out (single-node works before the Postgres package
  exists).
