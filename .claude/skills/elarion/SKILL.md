---
name: elarion
description: >-
  Build application features on the Elarion .NET framework the intended way: business logic in
  [Handler] classes inside modules, REST/JSON-RPC/MCP exposed via attributes, wiring emitted by
  source generators. Use this skill whenever the solution references Elarion packages (Elarion,
  Elarion.Abstractions, Elarion.AspNetCore, Elarion.EntityFrameworkCore, @swimmesberger/elarion-*)
  and you are adding or changing features, endpoints, handlers, entities, events, scheduled jobs,
  validation, authorization, or host wiring — even when the user doesn't say "Elarion". Elarion is
  newer than your training data and its API moves fast: read this first and verify against the
  current docs instead of writing Elarion code from memory.
---

# Building on Elarion

Elarion is a modular .NET application framework: vertical feature modules, handler-based CQRS,
`Result<T>` error flow, source-generated wiring, and three parallel transports (JSON-RPC, REST, MCP)
projected from the same handler. Two things follow for you:

1. **Don't write Elarion code from memory.** The API is newer than your training data and changes
   between versions. Verify names and signatures against the docs/package the app actually
   references (see [Research the current API](#research-the-current-api--dont-guess)).
2. **The framework already does the plumbing.** If you are hand-writing registration, endpoint
   mapping, serializer config, or transaction management, you are usually duplicating (and
   fighting) generated code.

## Where code goes

Business logic lives in **handlers inside modules** (typically an `Application` project). The
host/Api project owns only infrastructure: `Program.cs` composition, DB provider, auth middleware.

The most common agent mistake looks like this — don't do it:

```csharp
// MyApp.Api/Program.cs (or an Endpoints/ folder in the host) — WRONG
app.MapPost("/clients", async (AppDbContext db, CreateClientDto dto) => {
    // ...validation + business logic inline...
});
```

Instead, write a handler in the module and declare the transport with an attribute. If a route
genuinely can't be expressed by `[HttpEndpoint]`, use the module's `MapEndpoints` escape hatch
(below) — and even there the lambda stays thin and calls a handler.

## A feature, end to end

A module is a class marked `[AppModule]`; everything under its namespace prefix belongs to it:

```csharp
namespace MyApp.Application.Modules.Clients;

[AppModule("Clients")]
public static partial class ClientsModule { }
```

A handler is the unit of work. One class serves JSON-RPC, MCP, and (opt-in) REST:

```csharp
namespace MyApp.Application.Modules.Clients.Handlers;

[Handler("clients.get")]            // JSON-RPC method + MCP tool (name optional: {module}.{operation})
[HttpEndpoint("clients/{id}")]      // REST opt-in: GET inferred from IQuery, ICommand → POST
public sealed class GetClient(AppDbContext db)
    : IHandler<GetClient.Query, Result<GetClient.Response>> {

    public sealed record Query : IQuery {
        public required Guid Id { get; init; }
    }

    public sealed record Response(Guid Id, string Name);

    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        var client = await db.Clients.FirstOrDefaultAsync(c => c.Id == query.Id, ct);
        return client is null
            ? AppError.NotFound($"Client {query.Id} not found.")   // implicit → Result<Response>
            : new Response(client.Id, client.Name);
    }
}
```

**Register nothing.** Source generators discover handlers, services (`[Service]`), validators,
jobs, and event consumers, and gate all of it per module (`Modules:{Name}:Enabled`). If a handler
"isn't found" at runtime, the cause is its namespace not being under a module, the module being
disabled, or a build diagnostic you skipped — never a missing `AddScoped`.

Host wiring is a few generated calls (copy the current form from the quickstart doc — these names
have evolved before). Project layout is your choice: the bootstrapper discovers handlers from
referenced module assemblies **and from the host compilation itself**, so a tiny app can be one
project (`Program.cs` + modules + the trigger — `samples/LiveQuotes` is that shape); prefer the
host + application split (`samples/Billing`) as the app grows:

```csharp
[assembly: GenerateModuleBootstrapper]                              // once, in the host

builder.Services.AddElarion(builder.Configuration);                 // modules + canonical JSON
builder.Services.AddElarionJsonRpc(ElarionBootstrapper.RegisterHandlers);
app.MapElarionEndpoints(app.Configuration);                         // all [HttpEndpoint] routes, gated
app.MapElarionJsonRpc();
```

## Custom endpoints — the escape hatch

For routes `[HttpEndpoint]` can't express (streaming, SSE, file download, third-party webhooks),
the **module** declares hooks — never the host, so the routes stay module-owned and feature-gated:

```csharp
[AppModule("Clients")]
public static partial class ClientsModule {
    // Optional: one group (prefix/policy/conventions) for ALL module routes, generated + hand-written.
    public static IEndpointRouteBuilder ConfigureEndpointGroup(IEndpointRouteBuilder endpoints) =>
        endpoints.MapGroup("").RequireAuthorization();

    // Hand-written minimal-API routes. Keep the lambda thin: resolve the handler, call it,
    // translate the Result — the logic itself still lives in the handler.
    public static void MapEndpoints(IEndpointRouteBuilder endpoints) {
        endpoints.MapPost("clients/{id}/archive", async (
            Guid id,
            IHandler<ArchiveClient.Command, Result<ArchiveClient.Response>> handler,
            CancellationToken ct) =>
            ElarionHttpResults.ToResult(await handler.HandleAsync(new(id), ct)));
    }
}
```

`ElarionHttpResults.ToResult` / `.ToNoContentResult` / `.ToProblem` produce the same RFC 7807
responses as generated routes; `.ProducesElarionErrors()` adds the OpenAPI failure metadata.

## Stateful in-memory work — actors

Handlers are stateless. **Default to the database for concurrency** — optimistic concurrency (version
column), constraints, `[Idempotent]`/inbox, and scheduled jobs solve classical web-app races; most apps
need zero actors, and an actor is never the fix for "two requests raced on a row". An actor is justified
only when the unit of consistency is a **live in-memory thing**, in exactly three shapes: (1) it owns a
stateful external resource that must be accessed single-flight/ordered (a TCP device session, a
per-tenant API client, an OAuth token refresh); (2) hot, ephemeral, loss-tolerant state where DB
round-trips are pure overhead (live telemetry/device data, presence, live progress, write-behind
buffers — no snapshot needed); (3) a long-lived event-driven coordinator that must act exactly once
(the snapshot + single-homing shape). Rule of thumb: if the solution sketches as a table with a version
column, use the table. When one of those shapes fits, don't hand-roll a `Channel` + loop or a
lock-guarded singleton — that's `Elarion.Actors`:

```csharp
[Actor]                              // requires [assembly: GenerateActors] (or [UseElarion])
public sealed class OrderFulfillmentActor(IActorContext<Guid> context, IEmailSender email) {
    private FulfillmentState _state;                        // plain fields, never locked

    public async Task<Result<Unit>> Ship(ShipmentInfo info, CancellationToken ct) { ... }
}

// callers — usually a handler; the handler stays the transport-facing/authorization gate:
var order = actors.Get<IOrderFulfillment>(orderId);         // IActorSystem; facade is generated
var result = await order.Ship(info, ct);                    // mailbox-serialized, no locks
```

- The `IActorContext<TKey>` constructor parameter makes the actor **keyed** (one activation per key,
  activated on first message, passivated after ~5 min idle — in-memory state drops). No context
  parameter → process singleton.
- **Durable state**: declare an `IActorState<TState>` constructor parameter — the snapshot loads before
  `OnActivateAsync`, `state.State` is the in-memory copy (`null` until assigned when no snapshot exists),
  and only explicit `state.WriteStateAsync(ct)` persists (passivation never flushes; `ClearStateAsync`
  deletes, `RecordExists` reports — the members mirror Orleans' `IPersistentState<T>`).
  **Design `TState` as the query contract (mandatory)**: constants, derived flags, and pure transition
  methods live ON the record (it's shared — the actor, `IActorStateReader` queries on other instances,
  and SQL all deserialize the same type); actor methods only apply a transition, `WriteStateAsync`, then
  side effects (after the write — the write is the commit point). Evolve the shape with
  optional/defaulted properties on the record, never by migrating in `OnActivateAsync`.
  **Write cadence decides query meaning**: write-through → the reader is DB-fresh (the only cadence where
  it's a first-class query path); periodic checkpointing → the reader is bounded-stale (warm-restart
  mechanism, never a "live" view). Real-time views of hot in-memory state are **push** — the actor
  publishes client events from its home and the Postgres fan-out reaches every instance's browsers —
  never `IActorStateReader` polling. On a concurrent
  snapshot change the stale activation passivates and the turn transparently re-runs once on the
  reloaded snapshot (so write turns as reapplyable mutations; side effects before the write are
  at-least-once); only sustained conflicts surface as `ActorSnapshotConcurrencyException`. Backend: reference
  `Elarion.Actors.PostgreSql`, put `[GenerateElarionActorSnapshots]` on the `[GenerateDbSets]` context,
  and call `services.AddElarionPostgreSqlActorSnapshots<AppDbContext>()`; register `TState` in the
  module's `JsonSerializerContext`. Manual load/flush via `IActorLifecycle.OnActivateAsync/OnDeactivateAsync`
  remains the escape hatch for storage the seam doesn't fit.
- The generated facade is `I{ClassName-minus-Actor}`; methods must return
  `Task`/`Task<T>`/`ValueTask`/`ValueTask<T>`/`IAsyncEnumerable<T>` (ELACT002 otherwise). Actors are
  module-scoped like handlers — outside a module they warn (ELACT003) and are not registered.
- **Ordered streams (ADR-0052)** — when a consumer needs *every element in order with completion/resume*
  (not latest-wins): the actor owns an `Elarion.Streams.StreamHub<T>` field, publishes inside turns, and
  exposes `IAsyncEnumerable<StreamItem<T>> Watch(long? resumeAfter) => _hub.SubscribeSequenced(new() {
  ResumeAfterSequence = resumeAfter })` — a facade stream (attach = mailbox turn, enumeration
  off-mailbox). No `CancellationToken` param, no `[ConsumeEvent]` on stream methods (ELACT012). A live
  enumeration retains the activation against idle passivation (refCount lifetime); snapshot-conflict
  and shutdown passivations ignore retention — so still complete the hub in `OnDeactivateAsync`
  (re-activation = new hub = new sequence epoch). Serve it with `app.MapElarionStream<T>(route, (ctx, after) => …)` —
  SSE with `id:` = sequence, `Last-Event-ID`/`?after=` resume. Client events stay the default push tier;
  a stream needs a single live producer per key (route it to the actor home).
- **Data-rate shaping (ADR-0055)** — don't hand-roll batching/throttling between "samples arrive" and
  "database/UI consume": `Elarion.Buffering` (in `Elarion` core, BCL-only, no DI) ships
  `WriteBehindBuffer<T>` (`Add(item)`; flushes batches via your async delegate — naturally
  `ExecuteInsertAsync` — on `MaxItems` or `FlushInterval`, whichever first; bounded drop-oldest,
  single-flight, `FlushAsync` + flush-on-dispose; pass `onFlushError` to observe dropped batches) and
  `KeyedConflater<TKey,TValue>` (`Post(key, value)` latest-wins; at most one emit per key per
  `MinInterval` via your delegate — naturally `IClientEventPublisher.PublishAsync`; leading emit
  immediate, trailing emit so a quiet key never ends stale; idle keys retire). Typical owner: the actor
  holds both as activation state and disposes them in `OnDeactivateAsync`. Loss-tolerant by contract —
  transactional data goes through handlers + outbox, not these.
- Default is **non-reentrant** (one message start-to-finish; an actor→actor call cycle fails with a
  `TimeoutException` after ~30 s — that's the deadlock backstop, treat it as a design smell).
  `[Reentrant]` opts into Orleans-style interleaving at await points (never parallel); don't use
  `ConfigureAwait(false)` inside a reentrant actor.
- **Single-node by design**: in-memory activations on N nodes are N independent states (with
  `IActorState` they share one ETag-guarded snapshot row — safe, but optimistic). For one
  authoritative activation app-wide, mark it `[Actor(SingleHomed = true)]` and register the home
  lease (`AddElarionPostgreSqlActorHome<AppDbContext>()` + `[GenerateElarionRoleLeases]` on the
  context — the home is the `"actors"` role of the generic `IRoleLease` leader-election primitive in
  `Elarion.Coordination.PostgreSql`): one instance is elected home, calls elsewhere fail with
  `ActorNotHomedException` (for HTTP endpoints, bridge with the role-holder proxy —
  `app.UseElarionRoleHolderProxy("actors", "/live-prefixes…")` before routing + `AddElarionInstanceAddress()`
  on every instance; installs nothing without a lease, and the prefix list is the future ingress rule), and
  event delivery follows the lease via
  `AddElarionOutbox<T>(o => o.DeliveryGate = (sp, _) => ValueTask.FromResult(sp.GetRequiredService<IActorHomeLease>().IsHeld))`.
  Reads from any instance use `IActorStateReader.ReadAsync<TState>(key)` (snapshot, no activation).
  For true placement/forwarding move to Orleans/Akka.NET/Proto.Actor instead of bending this.
  Stateless parallelism never belongs in actors — that's handlers + `Task.WhenAll`.

## Long-lived connections — device gateways and interactive clients

Server→client facts are **client events** (SSE; at-most-once hints, client re-queries), ordered outputs
are **streams**, client→server calls are handlers. Reach for `Elarion.Connections` only when the
conversation itself is stateful or latency-interactive: a physical device holding a socket, live
collaborative input, or server→client RPC. The kernel (`AddElarionConnections()`) gives you the
node-local `IClientConnectionRegistry`, lifecycle observers, and a bridge that serves client-event
subscriptions over the connection with the exact same topic catalog + fail-closed auth as SSE.

The WebSocket endpoint means you write two things — an authenticator and a codec:

```csharp
public sealed class GatewayHandler(...) : WebSocketConnectionHandler {      // factory: one session per link
    public override ValueTask<WebSocketConnectionSession?> CreateSessionAsync(
        HttpContext context, CancellationToken ct) => ...new GatewaySession(...);  // null = reject (403)
}
public sealed class GatewaySession(...) : WebSocketConnectionSession {      // per-connection state lives here
    public override async ValueTask<ClientConnectionTicket?> AuthenticateAsync(
        WebSocketHandshakeContext handshake, CancellationToken ct) { ... }   // null = reject
    public override IClientConnectionProtocol CreateProtocol(WebSocketClientConnection c) =>
        new GatewayCodec(c, ...);                                            // parses frames, routes to the twin actor
}
// host: services.AddSingleton<GatewayHandler>(); app.UseWebSockets();
//       app.MapElarionConnectionSocket<GatewayHandler>("/gateway/ws");
```

Set `PrincipalId` to the device id — a device's parallel channels all register under it
(`registry.GetForPrincipal(deviceId)`), and a digital-twin **actor keyed by device id** serializes the
shared state across channels and user-triggered commands. Facts still travel as client events, even
over a connection; the sink (`SendAsync`/`InvokeAsync`) is for conversation traffic only.

Raw TCP devices use `Elarion.Connections.Tcp` — same authenticator/codec seams over
`AddElarionTcpConnectionListener/Dialer<THandler>` (or `TcpConnectionEndpoints.Apply*` for
bindings-as-data), plus a required `Framer` because TCP has no message boundaries. What the adapter owns
so you don't: optional **TLS before framing** (`o.Tls = new TcpServerTlsOptions/TcpClientTlsOptions { … }`
— fresh BCL options per connection, fail-closed validation, no plaintext fallback), **late
authentication** (register the ticket anonymous, then `registry.PromoteAsync(connectionId, identity)` —
one-way, exactly once, authenticated tickets always need a `PrincipalId`; client-event subscriptions drop
so the peer resubscribes), **bounded backpressure** (`MaxPendingSends` admission throws
`TcpSendQueueFullException` at capacity; a completed send means the frame was physically written; FIFO by
admission order, so "reply, then follow-ups" is just admitting them in order from one codec/actor turn),
and **deterministic shutdown** (`ShutdownGracePeriod`, then abort — no leaked connection tasks). For
request/reply into the device, correlate with
`ConnectionPendingRequests.SendAndWaitAsync(key, sendCt => connection.SendBinaryAsync(frame, sendCt))` —
registration before send, withdrawal when the send fails. Dispatch decoded commands through a
per-connection `new ConnectionHandlerInvoker(services, connection)` —
`invoker.InvokeAsync(decoded, ct)` infers both generic arguments when the request carries a self-typed
marker (`ICommand<TSelf, TResponse>`/`IQuery<TSelf, TResponse>`); marker-free requests use
`invoker.InvokeAsync<TRequest, TResponse>`, named traffic `invoker.InvokeNamedAsync(dispatcher, name,
request, ct)` (full pipeline per message; named routes need `HandlerTransports.Connection`). Test codecs
socket-free with `InMemoryTcpLink`. High-rate connections (game-server tier) opt into the
**low-allocation profile** (ADR-0066), each piece independent: construct the invoker with
`new ConnectionHandlerInvokerOptions { ScopeMode = ConnectionDispatchScopeMode.PerConnection }` (one
reused dispatch scope + cached chain; sequential dispatch; `await invoker.DisposeAsync()` on close;
transaction/idempotency pipelines warn — they assume per-message scoping), declare hot handlers
`[Handler(Scope = ServiceScope.Singleton)]` (compile-time verified, ELSG011–013: all ctor deps provably
singleton, no scope-dependent pipeline features) and `[HandlerTelemetry(HandlerTelemetryMode.None)]`
(also on the module class or assembly, nearest wins), and serialize outbound payloads straight into the
framed buffer with `connection.SendBinaryAsync(state, static (s, output) => …, ct)` (identical
backpressure; custom `TcpMessageFramer`s implement `BeginMessage`/`CompleteMessage`). The full profile
dispatches at 0 B/op; inbound `OnBinaryAsync` memory is pooled and call-scoped on every adapter — copy
it if the codec defers work. Hot value-type requests use the explicit-generic `InvokeAsync` overload
(the marker overload boxes a struct request).

Don't hand-roll device provisioning — `Elarion.Devices` owns the identity chain (ADR-0054):
`AddElarionDeviceIdentityEntityFrameworkCore<TDbContext>()` + `[GenerateElarionDeviceIdentity]` on the
context gives you `IDevicePairingService` (issue a single-use CSPRNG pairing code — device id
pre-assigned at issue; redeem atomically mints the per-device key; codes stored hashed) and
`HmacChallengeVerifier` for the in-socket handshake: `CreateNonce()` → send → device answers
`HMAC-SHA256(key, nonce)` → `VerifyAsync(deviceId, nonce, mac)` returns the device `ClaimsPrincipal`
(constant-time; null = reject) → build the ticket with `PrincipalId = deviceId`. The redeem endpoint is
app-owned and must be rate-limited; sweep expired codes with a `[ScheduledJob]` calling
`IPairingCodeStore.DeleteExpiredAsync`.

## Rules that don't change

- **Errors are values.** Return `Result<T>`; fail with `AppError.Validation / NotFound / Conflict /
  Forbidden / Unauthorized / BusinessRule / Internal`. Implicit conversions accept both the response
  and the error. Exceptions are for bugs; every transport translates `AppError` itself.
- **Validation is two-tier.** Standard `System.ComponentModel.DataAnnotations` on the request DTO
  (enforced at runtime *and* exported to OpenAPI/JSON-RPC/Zod schemas). Cross-field, async, or DB
  checks belong in the handler, inside its transaction. No FluentValidation, no pre-handler async
  validator.
- **Authorization sits on the handler.** `[RequirePermission("resource", "verb")]`,
  `[RequireRole]`, `[RequirePolicy]`, `[FeatureGate]` — class-level, enforced identically on every
  transport. ASP.NET `[Authorize]`/policies are host-level extras (middleware or
  `ConfigureEndpointGroup`), never the business gate.
- **Inject the concrete `DbContext`.** No repository layer, no `IAppDbContext`. Entities pair an
  `IEntityTypeConfiguration<T>` marked `[EntityConfiguration]` with `[GenerateDbSets]` on the
  DbContext. Commands already run in a framework transaction — don't open your own.
- **In-process calls are typed.** Inject `IHandler<TReq, Result<TRes>>` (or `IHandlerSender`) so a
  rename is a compile error; never dispatch by string name. A request declared with a self-typed marker
  (`Query : IQuery<Query, Response>`) gets fully inferred dispatch — `sender.SendAsync(new Query(id), ct)`
  with no generic arguments. Cross-module calls go through a
  `[ModuleContract]` — the analyzer (ELMOD002) flags reaching into another module's internals.
- **Two event planes.** Same-transaction reaction → domain event (inline, a failure rolls the
  command back). After-commit side effect → integration event (outbox, retried, deduped). Pub/sub
  only — no request/reply over the bus.
- **One canonical serializer.** JSON comes from source-generated contexts composed via
  `ConfigureElarionJson`; never register a bare `JsonSerializerOptions` in DI. Reflection
  serialization is off by default — a "type not supported" JSON error means a missing
  `[JsonSerializable]` context entry, not a reason to enable reflection.
- **Frontend calls the generated client.** `rpc-schema.json` (exported at build) →
  `@swimmesberger/elarion-jsonrpc-client-generator` → typed client with Zod validation. Don't
  hand-write fetch wrappers for RPC methods.

## Research the current API — don't guess

Before writing or reviewing Elarion code:

1. **Pin the version.** Check `Directory.Packages.props` / the `.csproj` for the referenced
   Elarion package versions.
2. **Read the docs — they are LLM-friendly.**
   - Index of all pages: <https://elarion.wimmesberger.dev/llms.txt>
   - Whole docs in one file: <https://elarion.wimmesberger.dev/llms-full.txt>
   - Single page as raw markdown: `https://elarion.wimmesberger.dev/llms.mdx/docs/<path>/content.md`,
     e.g. `/llms.mdx/docs/concepts/handlers/content.md`
   - Source of truth is `docs/` in <https://github.com/swimmesberger/Elarion> (ADRs under
     `docs/decisions/` explain the *why*); a local checkout may exist — worth a quick look.
3. **Read the shipped XML docs for the exact referenced version** when the site might be ahead:
   `ls ~/.nuget/packages | grep -i elarion`, then the `.xml` beside the dll.
4. **Build early and trust the diagnostics.** `dotnet build` surfaces `EL*` diagnostics (ELHTTP*,
   ELRPC*, ELVAL*, ELAUTH*, ELMOD002, …) — the generators enforcing these conventions. Look the id
   up in `reference/diagnostics` and fix the cause; never suppress.

High-value pages (paths under `/docs/`, same layout in the repo's `docs/`):

| Topic | Page |
| --- | --- |
| Host wiring, first feature | `getting-started/quickstart`, `getting-started/project-structure` |
| Handlers, pipeline, services | `concepts/handlers`, `concepts/decorator-pipelines`, `concepts/services` |
| Modules, hooks, gating | `concepts/modules`, `capabilities/hosting` |
| REST / JSON-RPC / MCP / OpenAPI | `capabilities/transports/` |
| Validation / authorization | `concepts/validation`, `concepts/authorization`, `concepts/resource-authorization` |
| EF, transactions, pagination | `capabilities/entity-framework`, `concepts/persistence-and-transactions`, `capabilities/pagination` |
| Bulk insert (imports, backfills) | `capabilities/bulk-operations` |
| Events, outbox, idempotency | `capabilities/events/`, `concepts/idempotency` |
| Client events (browser push / SSE) | `capabilities/events/client-events` |
| Errors | `concepts/results-and-errors` |
| Actors (stateful in-memory) | `concepts/actors` |
| Attribute / diagnostic / package tables | `reference/attributes`, `reference/diagnostics`, `reference/packages` |
| TS client, frontend modules | `capabilities/transports/typescript-client`, `concepts/frontend-modules` |

## You're off the rails if…

- an `Endpoints/`/`Controllers/` folder or a fat `MapPost` lambda is growing in the host project;
- you're writing `services.AddScoped<SomeHandler>()`, MediatR/`IRequestHandler`, or any manual
  handler registration;
- a repository interface, `IAppDbContext`, or hand-rolled unit-of-work is wrapping EF;
- a singleton service is growing locks/`SemaphoreSlim`/`Channel` plumbing around mutable state —
  that's an `[Actor]`;
- FluentValidation just appeared in a `.csproj`;
- a handler carries `[Authorize]` instead of `[RequirePermission]`;
- a `try/catch` returns an error DTO instead of a failed `Result<T>`.

Each of these has a framework-intended counterpart described above — use it, and check the docs
page from the table when the exact signature matters.
