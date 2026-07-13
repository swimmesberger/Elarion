# Changelog

All notable changes to Elarion are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html). While Elarion is pre-1.0,
minor releases may include breaking changes.

## [Unreleased]

### Fixed
- **Framework-wide audit fix pass.** A deep concurrency/correctness audit across all packages, with
  regression tests throughout. Highlights: `StreamHub` no longer wedges permanently when a Wait-mode
  subscriber unsubscribes under a blocked publish; user-scoped handler-cache entries are additionally
  stamped with the global tag so a default `[CacheInvalidate]` actually evicts them; the in-memory
  staged-upload store no longer corrupts a blob after a mid-chunk client disconnect, and the PostgreSQL
  staged store stops re-reading the whole staged payload on every HEAD/append (O(n²) → O(n)); the
  in-memory scheduler keeps recurring chains single and intact across variable resyncs, occurrence
  cancels, one-time schedules, and shutdown/slot-grant races; the idempotency claim `lock_timeout` no
  longer bleeds into business statements (and is applied in nested units of work); PostgreSQL
  LISTEN/NOTIFY listeners survive half-open connections via a bounded-wait liveness probe and send the
  re-query hint on every establishment; client-event lifecycle callbacks are serialized per topic/scope;
  actor facade calls honor `CallTimeout` during bounded-mailbox enqueues, `StopAsync` can no longer leak
  a racing activation, and snapshot-conflict retries carry provenance (a nested actor's conflict no
  longer re-runs the outer turn); the role-holder proxy bounds connect/response-header waits (503 instead
  of hanging); handler discovery no longer silently skips consumer namespaces containing "Decorators";
  duplicate `[AppModule]` names are now reported as `ELMOD006` instead of crashing generation; keyset
  cursor decoding and `ElarionFile` base64 payloads report malformed client input as 400-class errors
  instead of 500s; the TypeScript client generator preserves nullable array items, parenthesizes enum
  array types, and rejects cyclic `$ref` schemas instead of overflowing the stack. Plus many smaller
  hardening, diagnostics, and documentation fixes from the same audit.
- **Audit records survive commit-phase failures.** `IAuditTrail.RecordAsync` now reports whether the
  success record is durable or enlisted in the ambient transaction (`AuditRecordDurability`); the EF sink
  promotes the audit scope to recorded only once the commit succeeds, so a command that fails at COMMIT
  gets its detached failure record instead of leaving no audit trace.
- **Actor activation replacement is serialized on the predecessor's drain.** A replacement activation
  for the same key no longer constructs/loads while the old activation's `OnDeactivateAsync` is still
  running (exclusive-resource actors can no longer observe a double-hold); a hung deactivation proceeds
  after the new `ActorOptions.DeactivationTimeout` (default 30 s) with a warning. PostgreSQL actor
  snapshots now mint lineage-unique starting versions, closing an ETag ABA where a clear+recreate could
  let a stale activation silently overwrite a different snapshot lineage.

### Changed
- **`IClientConnectionSink.InvokeAsync` is bounded by default.** New kernel option
  `ElarionConnectionsOptions.DefaultInvokeTimeout` (default 30 s, `null` = no default) applies when a
  call carries no per-call `ClientInvokeOptions.Timeout`; codecs are notified of connection teardown via
  the new `IClientConnectionProtocol.OnClosedAsync` seam member, so pending invokes fault instead of
  hanging. TCP listeners gain an optional `MaxConcurrentConnections` cap.
- **The default authorization denial message is now generic** ("Access denied.") instead of echoing the
  unmet permission/role/policy name; the unmet requirement is logged at debug level and
  `AuthorizationOptions.ForbiddenMessageFormat` restores the detailed message.
- **A disabled scheduler rejects runtime enqueues** (`InvalidOperationException`) instead of queueing
  jobs unboundedly; descriptor-declared jobs are unaffected.
- **New diagnostic `ELPIPE004`** warns when a retrying `[Resilient]` command handler lacks
  `[Idempotent]` (a timed-out attempt's uncancellable commit can complete while the retry re-executes
  the command). **Breaking:** `AddElarionIdentity` drops its unused `TKey` type parameter.

### Added
- **Data-rate shaping helpers (ADR-0055).** `Elarion` core gains the `Elarion.Buffering` namespace with
  the two primitives every telemetry gateway hand-rolls between "device produces samples" and
  "database/UI consume them" — BCL-only, no DI registration, `TimeProvider`-driven for deterministic
  tests, both flushing on `DisposeAsync` so shutdown writes the tail instead of dropping it.
  **`WriteBehindBuffer<T>`** accumulates loss-tolerant samples and flushes batches through an async
  delegate (the natural body: ADR-0051 `ExecuteInsertAsync`) when `MaxItems` or `FlushInterval` is
  reached, whichever first; bounded past `Capacity` by dropping the oldest (`DroppedCount` meters the
  pressure); flushes are single-flight (work arriving mid-flush coalesces into the next drain pass);
  a failed flush drops its batch — rethrown from the explicit `FlushAsync`, routed to the optional
  `onFlushError` callback on background/dispose flushes. **`KeyedConflater<TKey, TValue>`** is the
  live-visualization primitive: `Post(key, value)` is latest-wins per key with at most one emit per
  `MinInterval` (leading edge immediate on an idle key, trailing edge for the conflated latest — a
  quiet key always publishes its final value, never ending on a stale one); emissions for one key never
  overlap, idle keys retire automatically, and publish failures drop to the optional `onPublishError`
  callback (at-most-once hints, matching the client-event contract). Deliberately just these two shapes —
  needs beyond them (windows, joins, replay) are the trigger for a real reactive library, adopted whole.
- **Device identity and provisioning (ADR-0054).** Two new packages own the identity chain every device
  gateway otherwise hand-rolls — exactly where mistakes are security-relevant. **`Elarion.Devices`**
  (BCL crypto only, AOT-compatible): `IDevicePairingService` issues single-use, TTL-bounded pairing
  codes (CSPRNG over a Crockford-style human-typeable alphabet, validated normalization-stable and
  duplicate-free; the device id is pre-assigned at issue so the issuer can attach it to domain state)
  and redeems them atomically for a freshly minted per-device symmetric key — codes are stored
  SHA-256-hashed, unknown/expired/used redeems are indistinguishable, and redeeming a code issued for
  an already-provisioned device id **rotates** its key (issuing that code is the re-key
  authorization). `HmacChallengeVerifier` runs the connect-time handshake (per-connection CSPRNG
  nonce, constant-time HMAC-SHA256 verification, unknown ids pay the same MAC cost as known ones) and
  returns the device `ClaimsPrincipal` (`DevicePrincipal` — stable claim shape so
  `[RequirePermission]` and auditing work unchanged), shaped to drop into a
  `WebSocketConnectionHandler`/`TcpConnectionHandler` authenticator as ticket inputs. Stores sit
  behind `IDeviceKeyStore`/`IPairingCodeStore`; the in-memory pair is an explicit opt-in
  (`AddElarionInMemoryDeviceIdentityStores`) — durable identity never gets a silent volatile default.
  **`Elarion.Devices.EntityFrameworkCore`** ships the durable stores (`elarion_device_keys`,
  `elarion_device_pairing_codes`) with change-tracker-free writes on a fresh DI scope per operation;
  the pairing-code claim is one `DELETE … RETURNING`, so concurrent redeems are single-winner across
  nodes. Tables map via `modelBuilder.UseElarionDeviceIdentity()` or `[GenerateElarionDeviceIdentity]`
  (bundled generator; `ELDEV001` when `[GenerateDbSets]` is missing);
  `AddElarionDeviceIdentityEntityFrameworkCore<TDbContext>()` registers the whole chain.
- **Ordered streams (ADR-0052).** The ordered, completable tier next to client events, for when element
  identity matters and a single live producer owns the sequence. `Elarion` core gains
  `Elarion.Streams.StreamHub<T>` — a hot, ordered in-memory broadcast (BCL-only: Channels +
  `IAsyncEnumerable<T>`) with atomic replay-then-live (`ReplayCapacity` ring, default 1 =
  `BehaviorSubject`), `ResumeAfterSequence` resume (a gap beyond the ring is a visible sequence jump,
  never a silent hole), per-subscriber overflow strategy (`DropOldest`/`Wait`/`Cancel` →
  `StreamLaggedException`), and `Complete`/`Fail`. An `[Actor]` method returning `IAsyncEnumerable<T>`
  now becomes a **facade stream** (the Orleans 7+ grain-interface shape): the attach runs as a mailbox
  turn, enumeration off the mailbox, one subscription per enumeration; a live enumeration **retains the
  activation** against idle passivation (refCount lifetime — a streaming-only actor never loses its
  stream mid-flight; correctness passivations still complete hubs and start a new sequence epoch);
  ELACT012 rejects `CancellationToken` parameters and `[ConsumeEvent]` on stream methods. `Elarion.AspNetCore` adds
  `MapElarionStream<T>(route, subscribe)` — the SSE leg with `id:` = sequence, so the browser's automatic
  `Last-Event-ID` reconnect (or `?after=`) resumes from the producer's replay ring. Client events remain
  the default push tier; `samples/LiveQuotes` now ships both side by side
  (`curl -N /quotes/ELN/stream`).
- **PostgreSQL bulk insert (ADR-0051).** Two new packages bring an EF-native bulk path:
  **`Elarion.EntityFrameworkCore.BulkOperations`** (provider-neutral) adds `ExecuteInsertAsync` over
  `DbSet<T>` — set-based and non-tracking like EF's `ExecuteUpdate`/`ExecuteDelete` family, aligned with
  the dotnet/efcore #27333/#29897 design sketches: `IEnumerable<T>` + streaming `IAsyncEnumerable<T>`
  overloads, joins the ambient `Database.CurrentTransaction`, store-generated columns omitted and never
  fetched back, value converters honored, complex properties (value objects) flattened with null
  propagation, TPH discriminators written per set, and unsupported shapes (TPT, owned types, complex
  collections, shadow properties) failing loud before the database is touched.
  **`Elarion.BulkOperations.PostgreSql`** implements the `IBulkInsertProvider` seam over binary
  `COPY … FROM STDIN`, registered EF-natively on the options builder
  (`options.UseNpgsql(…).UseElarionPostgreSqlBulkOperations()`, no app-DI wiring) and running on the
  context's own connection (all-or-nothing, rolls back with the caller's transaction). Compiled
  per-column typed writers (converters inlined, cached per model) put it at raw-`NpgsqlBinaryImporter`
  parity in time *and* allocations — ~12× `SaveChanges` at 100k rows with ~0.5% of its allocations,
  benchmarked against EFCore.BulkExtensions (MIT fork), linq2db, and PhenX in `tests/Elarion.Benchmarks`
  (`--filter "*BulkInsert*"`, real PostgreSQL via Testcontainers). Opt-in upsert via the options bag:
  `OnConflict = DoNothing`/`Update` stages the COPY through a per-call temp table and merges with
  `ON CONFLICT` (target defaults to the primary key; `ConflictProperties` selects an alternate unique
  constraint, validated against the model) — the default direct path is untouched.
- **Producer-side subscription lifecycle for client events — initial values and interest.** A topic can
  now declare an `IClientEventSubscriptionObserver` (`[SubscriptionObserver<T>]` on the contract, or
  `ObserveSubscriptions<T>()` on the topic options) with deliberately Rx-shaped semantics:
  `OnSubscribedAsync` hands the producer a **per-subscriber sink** — greet the new subscriber with the
  current value (the `BehaviorSubject` pattern, producer-controlled) and the stream becomes
  self-converging: "last known value + everything since", on one connection, re-greeted on every
  reconnect; `OnInterestChangedAsync` is `RefCount` with a linger-debounced last-watcher departure
  (default 5 s — a browser reload never bounces an upstream connection). The pull sibling
  `IClientEventInterest.HasSubscribers(topic, scope)` lets producers (actors especially) skip work nobody
  observes — safe because the greeting covers late arrivals. Interest is node-local by design and
  authoritative for actor-fed topics via the ADR-0050 home routing. `samples/LiveQuotes` dogfoods it:
  unwatched symbols publish nothing, new subscribers converge from the greeting, and the dashboard drops
  its reconnect re-fetch.
- **`[AllowAnyResource]` — declare a client-event topic's resource segment a routing key.** Resource-scoped
  subscriptions stay fail-closed by default, but a topic whose resource merely selects which events to
  receive (a stock symbol, a public room id — not an entitlement) now declares that on the contract
  (`[AllowAnyResource]`, or `AllowAnyResource()` on the topic options): callers passing the topic's
  requirements subscribe to any resource without the `IClientEventSubscriptionAuthorizer` seam. Per-topic
  by design — the authorizer seam is global, so a blanket `return true` implementation would silently open
  every future entitlement-scoped topic; the seam now stays reserved for genuine per-resource entitlement
  checks. The generator flows the attribute into the topic registration, and `samples/LiveQuotes` drops its
  allow-all authorizer for the declaration.
- **The role-holder proxy (ADR-0050) — an in-app ingress rule for homogeneous fleets.**
  `app.UseElarionRoleHolderProxy("actors", "/quotes", …)` lets every instance serve the listed path
  prefixes by transparently forwarding them to the current role-lease holder when the instance is not
  it — so `--scale app=N` works out of the box for single-homed actors' endpoints, one hop slower
  off-home, until a load balancer takes over (the prefix list maps 1:1 onto that ingress rule). Routing
  happens **before** anything executes locally (no double-execution hazard), bodies and SSE stream
  through, auth headers replay verbatim (same app on both ends), and failure modes are bounded: one hop
  ever (`Elarion-Role-Proxied` loop guard), holder unknown/unreachable or mid-failover →
  `503 + Retry-After`, no retries or queueing. Zero cost when unused or when no role lease is
  registered (the call installs nothing — single-instance mode). Supporting pieces: the lease row now
  carries the holder's advertised address (`RoleLeaseOptions.AdvertisedAddress`, or the new
  `IInstanceAddressProvider` seam with `AddElarionInstanceAddress()` auto-detecting from the server's
  bound endpoints), exposed as `IRoleLease.CurrentHolderAddress`.
- **`samples/LiveQuotes` — the realtime middle-ground sample.** A simulated market-data feed (~100
  ticks/s) streamed through single-homed, per-symbol in-memory actors that conflate to human-readable
  rates and push resource-scoped client events to browsers over SSE — zero database, zero broker, one
  `dotnet run`. Demonstrates actor "shape 2" end-to-end (feed as a hosted service calling typed facades
  in-process — deliberately not integration events; sequence-guarded ordering; conflation;
  converge-then-stream on the client) with clock-deterministic, Docker-free tests and a README covering
  the worker/web multi-node topology.

### Fixed
- **Actor facades preserve nullable reference annotations.** A method returning `Task<T?>` (or taking a
  nullable reference parameter) lost the `?` in the generated facade and work item, failing CS8603 under
  warnings-as-errors; the registration generator now renders method results and parameters with the
  nullable modifier.
- **Actor state snapshotting (ADR-0047).** Declaring an `IActorState<TState>` constructor parameter gives
  an `[Actor]` durable, snapshot-persisted state: loaded before `OnActivateAsync`, persisted only by
  explicit `WriteStateAsync` (passivation never flushes), cleared by `ClearStateAsync`, refreshed by
  `ReadStateAsync` — the members deliberately mirror Orleans' `IPersistentState<T>` so state call sites
  survive a migration unchanged. Snapshots are canonical-JSON text behind the provider-neutral
  `IActorSnapshotStore` seam; the shipped backend is the new **`Elarion.Actors.PostgreSql`** package: an
  EF-mapped `elarion_actor_snapshots` table (`jsonb` payload — actor state stays SQL-inspectable; a
  `version` column as the ETag), change-tracker-free version-guarded writes,
  `[GenerateElarionActorSnapshots]`/`UseElarionActorSnapshots` model wiring (`ELASN001`), and the
  `IActorStateReader` query-side companion (read the latest snapshot on any instance without activating).
  Snapshot **concurrency conflicts self-heal**: a conflicted turn passivates its stale activation and
  transparently re-runs once on the winning snapshot; only a second consecutive conflict surfaces
  `ActorSnapshotConcurrencyException` (every conflict logs and counts `actor.snapshot.conflicts`). The
  docs state the mandatory state-design rules (the record is the query contract; interpretation and pure
  transitions live on `TState`; side effects after the write; shape-tolerant evolution).
- **Single-homed actors (ADR-0048).** `[Actor(SingleHomed = true)]` declares that an actor runs on exactly
  one instance app-wide; enforcement is the **actor home** — calls on any other instance fail immediately
  with `ActorNotHomedException` naming the holder (no call forwarding, deliberately: needing it is the
  Orleans trigger). Unenforced (with a warning) when no `IActorHomeLease` is registered, so local dev
  needs no wiring. Event delivery follows the home via the new dynamic
  **`OutboxOptions.DeliveryGate`** (per-cycle sibling of `RunDeliveryWorker`): a closed gate skips the
  cycle before anything is claimed, so a homogeneous fleet self-organizes — every instance publishes, the
  home delivers events and hosts the single-homed actors, failover moves both together.
- **Role leases — leader election on PostgreSQL (ADR-0049).** The new **`Elarion.Coordination.PostgreSql`**
  package elects exactly one instance per named role ("which instance *is* X right now") with one
  heartbeat-renewed conditional-upsert row per role (`elarion_role_leases` — the row is the whole
  membership protocol; application clock only; `IsHeld` undershoots expiry by a safety margin so the old
  holder stops before a new one can start; release-on-shutdown makes failover immediate; a database outage
  fails closed to "nobody holds"). Contract: the keyed `IRoleLease` in `Elarion.Abstractions.Coordination`;
  registration: `AddElarionPostgreSqlRoleLease<TDbContext>(o => o.RoleName = "…")` once per role;
  model wiring: `[GenerateElarionRoleLeases]`/`UseElarionRoleLeases` (`ELROLE001`). Deliberately **not** a
  distributed-lock API — coarse roles only. The actor home is its first consumer
  (`AddElarionPostgreSqlActorHome<TDbContext>()` = the `"actors"` role lease + the `IActorHomeLease`
  binding via `AddElarionActorHome`).
- **Billing sample: durable dunning.** `ClientDunningActor` dogfoods the full model — snapshot-backed
  escalation latch (survives passivation/restarts, e2e-tested), the rich-state reference shape
  (threshold/interpretation/pure transition/`SnapshotKey` on the record), and the `GetClientDunning`
  query handler reading via `IActorStateReader` without touching the actor system.
- **Outbox delivery worker is now opt-out per node (`OutboxOptions.RunDeliveryWorker`, default `true`).**
  `AddElarionOutbox<T>` previously always registered the hosted `OutboxDeliveryService`, so in a
  heterogeneous topology (e.g. web nodes with a feature module disabled plus a worker-role node hosting
  that module's consumers or actors) a publish-only node's delivery loop claimed messages whose consumers
  only exist elsewhere and parked them as unresolvable. Setting `RunDeliveryWorker = false` keeps the
  durable bus and outbox storage (publishing stays atomic with business data) but skips the worker
  registration entirely; run delivery only on the instance(s) registering **all** integration consumers.

### Fixed
- **Single-project hosts now wire their transports.** The generated bootstrapper
  (`[GenerateModuleBootstrapper]`) built its transport maps exclusively from referenced assemblies'
  Elarion manifests, so `[HttpEndpoint]`/`[Handler]`/`[ResourceFilter]` declarations living in the same
  project as the bootstrapper (Program + modules in one csproj) registered in DI but were silently absent
  from `MapElarionEndpoints`, `RegisterHandlers`, and the MCP metadata — every endpoint 404'd with no
  diagnostic. `AppModuleDiscoveryGenerator` now discovers transport handlers and resource-filter specs in
  the current compilation as well and merges them with the referenced manifests (current-compilation
  entries win deduplication), so the single-project and multi-project layouts wire identically.

## [0.2.4] - 2026-07-08

### Changed
- **Actor call path allocates ~70% less per call (ADR-0042 perf round).** Three profiling-driven
  optimizations on the actor hot path, allocation-neutral for the public API: the per-cell mailbox lock
  is replaced by a packed atomic state word (closed flag + pending count in one `long`, coordinated by
  interlocked ops — the `_gate` Monitor was the hottest contended frame); generated work items are now
  **pooled** — a rented, recycled instance backed by the new bounded `ActorWorkItemPool<T>`, so a
  completed call allocates no work-item object (the caller captures the completion `Task` before enqueue,
  so reuse is recycle-safe and — unlike an `IValueTaskSource` — keeps `AsTask()`/fan-out allocation-free);
  and actor telemetry no longer builds its span-name string when no trace listener is attached. Benchmarks:
  `Ask` 456→136 B, `Ask_Pipelined` 501→181 B, `PingPong` 488→168 B, with a ~14% faster single call.
- **Telemetry no longer allocates a span-name string when no listener is attached.** Every
  `ActivitySource.StartActivity` call across the framework interpolated its span name eagerly, before the
  listener check — allocating a string on every handler invocation, JSON-RPC/MCP dispatch, event
  publish/consume, scheduled run, and resilience-wrapped call even with tracing off. Each site now guards
  on `Source.HasListeners()` first; span names and tags are unchanged when a listener is present.

### Added
- **Opt-in cross-platform profiling for `tests/Elarion.Benchmarks`.** `--profiler EP` exports a speedscope
  flamegraph (EventPipe, no extra tooling); `ELARION_BENCH_DOTTRACE=1` emits a dotTrace snapshot. Both are
  off by default and macOS-friendly; usage is documented in `Program.cs`.

## [0.2.3] - 2026-07-07

### Added
- **Client events — near-realtime browser updates (ADR-0043).** After-commit facts projected to connected
  browsers as **at-most-once hints**: declare an `IClientEvent` wire contract in a module (topic registration
  is **generated** — `{module}.{name}`, `[ClientEvent("…")]` overrides, contract-level `[RequirePermission]`/
  `[RequireRole]` become subscribe-time requirements; diagnostics `ELCEV001`–`ELCEV003`), project the
  integration event onto it with a method-form `[ConsumeEvent]` (post-commit for free), and the browser
  converges by re-query — payloads are ids/refs, never state. `Elarion.ClientEvents` owns the opt-in topic
  catalog, canonical-JSON publisher, and the in-process registry behind the replaceable
  `IClientEventBroadcaster` seam; `Elarion.ClientEvents.AspNetCore` maps the SSE endpoint
  (`MapElarionClientEvents`, native `TypedResults.ServerSentEvents`, fail-closed subscribe-time authorization,
  `elarion.connected`/`elarion.keepAlive` control events); `Elarion.ClientEvents.PostgreSql` adds cross-node
  fan-out over `LISTEN/NOTIFY` (`AddElarionPostgreSqlClientEvents`) with an `elarion.connected` re-query
  signal after reconnect gaps. The schema exporter emits declared topics as the schema's `events` block, and
  the TypeScript client generator turns it into `events-client.ts` — a topic-typed subscription client with
  Zod-validated payloads over one `EventSource`. See the
  [client events doc](docs/capabilities/events/client-events.mdx) and
  [ADR-0043](docs/decisions/0043-client-events.md); streaming request/response support was evaluated and
  deferred in [ADR-0044](docs/decisions/0044-streaming-requests-and-responses.md).
- **In-memory actors — `Elarion.Actors` (ADR-0042).** Plain classes marked `[Actor]` become keyed,
  mailbox-protected state machines with **source-generated typed facades**: public async methods are the
  message surface, a generated `I{Name}` facade enqueues each call as a statically-typed work item (no
  reflection, no message envelopes, AOT-clean), and `IActorSystem.Get<IOrderFulfillment>(orderId)` addresses
  the virtual activation (created on first message, passivated after an idle timeout, per-activation DI
  scope; optional `IActorLifecycle` load/flush hooks). Execution is Orleans-style single-threaded:
  non-reentrant by default, with class-level `[Reentrant]` opting into turn-based interleaving (turns
  interleave at awaits, never run in parallel — encoded in a dedicated test suite); every call carries a
  timeout backstop (default 30 s) so actor→actor call cycles fail diagnosably instead of hanging. Exceptions
  cross the mailbox unwrapped with their actor-side stack traces; `Elarion.Actors` ActivitySource/Meter spans
  make a call look like an RPC hop. Actors are module-scoped like handlers (`AddActors` hook in
  `ConfigureDefaultServices`; `[assembly: GenerateActors]`/`[UseElarion]`; diagnostics `ELACT001`–`ELACT007`,
  including an analyzer flagging `ConfigureAwait(false)` inside `[Reentrant]` actors and a guard steering
  event consumption to relay consumers that call the facade)
  and deliberately **single-node** — clustering is a non-goal (swap to Orleans/Akka.NET/Proto.Actor per the
  ADR-0025 seam philosophy). The call path is benchmarked (`tests/Elarion.Benchmarks`, BenchmarkDotNet) and
  optimized step-by-step (pooled per-call cancellation, sync-enqueue fast path, pass-through facades:
  ~448 B / ~1.8M msg/s per mailbox pipelined). See [ADR-0042](docs/decisions/0042-in-memory-actors.md) and
  the [Actors concept doc](docs/concepts/actors.mdx).
- **File payloads — the in-memory `ElarionFile` tier and the staged-blob tier (ADR-0039).** A handler declares
  "I receive/return a file" once and every transport carries it the way that suits it best. **`ElarionFile`**
  (in `Elarion.Abstractions`, next to `Result<T>`) is the **small-file tier** — deliberately bytes-only
  (content + content type + optional file name, rule of thumb ≲ 4 MB). A `Result<ElarionFile>` response is
  served as a streamed file download over HTTP (`ElarionHttpResults.ToFileResult`; `application/octet-stream`
  with `type: string, format: binary` in the OpenAPI document via an inert endpoint marker), and rides a fixed
  base64 JSON envelope (`{ contentType, fileName?, data }`, `ElarionFileJsonConverter`, seeded into
  `ElarionFrameworkJsonContext` so it resolves under the AOT-strict serializer with no app registration) over
  JSON-RPC/MCP — **both directions**, so an `ElarionFile` request property is an upload. The exported schema
  marks the envelope `x-elarion-file`, and the TypeScript client generator maps it to a **native `File`**
  (params accept a `File` validated with `z.instanceof(File)`, results materialize one; base64 conversion at the
  call boundary, file-free schemas byte-identical). For **large files**, the staged-blob tier: uploads land in
  the pending area (`MapElarionResumableBlobUploads`/`MapElarionBlobUploads`) and the handler receives a blob reference to stream
  from; exports write a pending owner-scoped blob and return its `BlobRef`, streamed down from the new
  **`MapElarionBlobDownloads`** (`GET {prefix}/{blobId}`, exact-owner fail-closed → 404) — never-committed blobs
  expire via GC, giving temp-file semantics for free. `IFormFile` remains the HTTP-multipart escape hatch. See
  [ADR-0039](docs/decisions/0039-binary-file-responses.md).
- **Host-declared module endpoint hooks — `[ModuleEndpoints]` (ADR-0040).** A module whose assembly is
  deliberately web-free (no shared-framework reference) cannot declare the `MapEndpoints`/`ConfigureEndpointGroup`
  hooks itself, since both take `IEndpointRouteBuilder`. **`[ModuleEndpoints("Name")]`** (in `Elarion.AspNetCore`)
  marks a static class that declares those same hooks *on behalf of* a module — typically in the host, or a web
  companion assembly discovered via the per-assembly Elarion manifest. `AppModuleDiscoveryGenerator` calls them
  inside the named module's feature gate in `MapElarionEndpoints`, composed with the module's own hooks (module
  first, contributors in stable type-name order, group hooks chained), so a disabled module's contributed
  endpoints disappear with it — no hand-duplicated `IsModuleEnabled` check. An unknown module name warns
  `ELMOD004` (hooks skipped); a class with no recognized hook warns `ELMOD005`. See
  [ADR-0040](docs/decisions/0040-host-declared-module-endpoints.md).
- **Client-assigned entity identity (ADR-0038).** Elarion's stance is now enforced end to end: the
  application owns entity identity, mints ids in code with `Guid.CreateVersion7()` (UUIDv7 — time-ordered,
  index-friendly), and the model says so. The `[GenerateDbSets]` `ConfigureEntities` now ends with a
  generated **client-assigned Guid key pass** that declares the discovered domain entities'
  single-property `Guid` primary keys `ValueGeneratedNever` — scoped to the discovered entities'
  *assemblies*, so navigation-discovered children (where EF's insert-vs-update heuristic misreads a set id
  on a "generated" key and a replace-children update dies with `DbUpdateConcurrencyException` on a real
  database, dotnet/efcore#35090) are covered while Identity/framework entities keep their packaged
  generation; explicit or data-annotation configuration, custom value generators, and store defaults always
  win. Schema-neutral: adopting it produces one *empty* migration to re-sync the snapshot. Minting a Guid
  id with `Guid.CreateVersion7()` (UUIDv7) is the documented convention (framework code and docs updated
  to match); the model pass prevents the phantom UPDATE regardless of v4 vs v7.
  `ApplyElarionIdentity` now declares `ValueGeneratedOnAdd` explicitly for a `Guid` `TKey`, so no app-level
  key convention can reinterpret Identity's keys. A Testcontainers pin test proves the replace-children
  pattern both ways (the EF InMemory provider skips the affected-rows check and cannot catch it). See
  [ADR-0038](docs/decisions/0038-client-assigned-entity-identity.md) and
  [Entity identity](docs/capabilities/entity-framework.mdx).
- **Protocol-neutral staged (resumable) uploads + Azure Blob Storage backend (ADR-0035).** The tus staging seam
  is promoted into `Elarion.Blobs` as **`IStagedUploadStore`** — offset-guarded `AppendAsync`, an **explicit,
  idempotent `CompleteAsync`** that seals the staged bytes into a pending blob, nullable declared length
  (deferred-length uploads), and all expiry policy arriving as data (`StagedUploadCreation.ExpiresAt`,
  `StagedUploadCompletion.SessionExpiresAt`/`BlobExpiresAt`). This is the shape of the IETF *Resumable Uploads*
  draft ("tus 2.0"): when it stabilizes it becomes a second endpoint adapter over the same seam, and every
  staging backend lights up unchanged. `Elarion.Blobs.Tus` is now a **pure protocol adapter** (policy in
  `ResumableBlobUploadOptions`, including the new `CompletedSessionRetention`), completes uploads explicitly (no more
  zero-byte-append finalize hack), and **self-heals** the append-versus-completion crash window on the next
  `HEAD`. The provider-neutral collectors (`StagedUploadGarbageCollector`, and `BlobGarbageCollector`, moved up
  from `Elarion.Blobs.PostgreSql`) live beside the seam, so the in-memory staging default is now swept too.
  New **`Elarion.Blobs.Azure`** exercises the seam on the bare Azure SDK: `AzureBlobStore` implements
  `IBlobStore` + `IBlobLifecycle` over blob metadata (ETag-guarded commit/GC races), and
  `AzureStagedUploadStore` stages each session as an **append blob** whose offset guard is the server-side
  `If-Append-Position-Equal` precondition, completing via a **server-side copy** into the final pending blob —
  no relational staging table, zero protocol knowledge (`AddElarionAzureBlobStore` /
  `AddElarionAzureBlobLifecycle` / `AddElarionAzureStagedUploads`; Azurite-backed integration tests). See
  [ADR-0035](docs/decisions/0035-protocol-neutral-staged-upload-seam.md).
- **Blob listing — prefix + delimiter virtual hierarchy (ADR-0036).** `IBlobStore` gains
  **`ListAsync(BlobListRequest)`** and **`ListContainersAsync`**: flat prefix listing, optionally rolled up
  into delimiter-inclusive virtual-directory `Prefixes` (the S3/Azure/GCS model — deliberately *not* real
  directories; apps that need folder semantics model folders as entities). Entries page in ordinal name
  order behind an opaque continuation token; `BlobMetadata` now carries the lifecycle **`State`** and the
  request filters on it, so browse surfaces can hide pending uploads. `BlobStoreExtensions.ListAllAsync`
  adds the auto-paging `IAsyncEnumerable` enumeration migration/backup tooling wants. Azure maps natively
  onto `GetBlobsByHierarchy` (state filtering is client-side per page — Azure cannot filter metadata
  server-side); PostgreSQL computes the roll-up in one grouped `COLLATE "C"` keyset query over the
  existing `(container, name)` index. Listing is a browse/ops surface — handlers keep answering
  "which blobs belong to X" from their own tables. See
  [ADR-0036](docs/decisions/0036-blob-listing-virtual-hierarchy.md).

### Changed
- **Breaking (telemetry): all duration metrics are now seconds (OTel semantic conventions).** Every Elarion
  duration histogram (`handler.execution.duration`, `actor.message.duration`/`actor.message.queue_wait`,
  `scheduler.job.run.duration`/`scheduler.job.run.lag`, `scheduler.operation.duration`,
  `messaging.consumer.invocation.duration`, `messaging.delivery.duration`, `handler.cache.operation.duration`,
  `resilience.policy.execution.duration`, and the RPC request duration) now records **seconds** (unit `s`)
  instead of milliseconds, and supplies the semconv-recommended bucket boundaries as instrument advice so
  exporters don't fall back to millisecond-scaled default buckets. The JSON-RPC/MCP duration metric is renamed
  `rpc.server.duration` → **`rpc.server.call.duration`** per the current OTel RPC conventions (the retired
  experimental name was defined in milliseconds). The public `Record*` helpers on the telemetry classes now
  take a `TimeSpan` instead of `double elapsedMilliseconds`. Span attributes with an explicit `_ms` suffix
  (e.g. `scheduler.job.duration_ms`) are unchanged — they are self-describing. Update dashboards and alerts
  accordingly.
- **Breaking (blob storage wiring):** `PostgreSqlBlobStore<T>` no longer depends on an injected
  `NpgsqlDataSource`. Its dedicated streaming-read connection is now **cloned from the blob `DbContext`'s own
  connection** (`ICloneable.Clone()` → the connection's owning data source), so it shares the context's pool,
  type mapping, auth callbacks, and target database by construction — under every EF connection shape
  (`UseNpgsql(connectionString)`, `UseNpgsql(dataSource)`, `AddNpgsqlDataSource` + `UseNpgsql()`). The
  `connectionString` overloads of `AddElarionPostgreSqlBlobStore`, `AddElarionPostgreSqlBlobLifecycle`, and
  `AddElarionPostgreSqlStagedUploads` are **removed**; the context registration is the only connection wiring.
  **Migration:** drop the connection-string argument from those calls; an `NpgsqlDataSource` registered solely
  for the blob store can be deleted (one kept for EF binding is unaffected). See
  [ADR-0041](docs/decisions/0041-blob-streaming-connections-clone-the-context-connection.md).
- **Behavior:** the generated `ConfigureEntities` declares domain `Guid` primary keys `ValueGeneratedNever`
  (ADR-0038, see *Added*). An app that left `Id` unset and relied on EF's client-side Guid generator now
  inserts `Guid.Empty` (failing loudly on the second row): set ids in code (`Guid.CreateVersion7()`,
  recommended) or configure `ValueGeneratedOnAdd()` explicitly on that entity — explicit configuration
  always wins. Add one (empty) migration to re-sync the model snapshot.
- Framework-minted ids are UUIDv7 now (`Guid.CreateVersion7()`): outbox message and correlation ids,
  delivery lease tokens, in-memory event/message ids, scheduler job/run ids, PostgreSQL blob ids,
  staged-upload session ids (PostgreSQL/in-memory/Azure), and the upload endpoints' storage-name segments.
  Ids remain opaque Guids on every contract; only their version bits changed (b-tree-friendly ordering,
  and storage names now list in upload order within an owner prefix).
- **Breaking:** `ITusUploadStore`/`TusUpload`/`TusUploadCreation`/`TusOffsetConflictException` are replaced by
  the `IStagedUploadStore` family in `Elarion.Blobs`; the `Elarion.Blobs.Tus.PostgreSql` package dissolves into
  `Elarion.Blobs.PostgreSql`. **Migration:** `AddElarionTusPostgreSql<T>` → `AddElarionPostgreSqlStagedUploads<T>`,
  `UseElarionTusStorage()` → `UseElarionStagedUploads()`, `[GenerateElarionTusStorage]` →
  `[GenerateElarionStagedUploads]` (diagnostic `ELTUS001` → `ELBLB002`), staging table default `tus_uploads` →
  `staged_uploads` (declared length column is now nullable), and `TusGcOptions` → the provider-neutral
  `StagedUploadGcOptions` with completed-session retention moving to `ResumableBlobUploadOptions.CompletedSessionRetention`.
  The tus wire behavior is unchanged. `Elarion.Blobs` now references the `Microsoft.Extensions.*` abstractions
  packages (DI/Hosting/Logging) to host the seam's default and collectors. The tus adapter's host-facing API
  also drops the protocol name so the resumable-upload protocol is swappable without touching wiring:
  `AddElarionTus` → `AddElarionResumableBlobUploads`, `MapElarionTus` → `MapElarionResumableBlobUploads`,
  `TusOptions` → `ResumableBlobUploadOptions` (the package `Elarion.Blobs.Tus`, its namespace, the internal
  tus protocol helpers, and the wire behavior are unchanged — the tus identity lives in the package, not the
  host-facing verbs).
- **`@swimmesberger/elarion-contributions` — adoption-feedback revisions (ADR-0032 addendum).** Two production
  adoptions ([#71](https://github.com/swimmesberger/Elarion/issues/71) — a Vite SPA with no auth;
  [#72](https://github.com/swimmesberger/Elarion/issues/72) — a TanStack Start SSR app) surfaced framework
  gaps, not user errors. **`when` clauses are now strictly typed** against the vocabulary — a typo'd or
  out-of-vocabulary name is a compile error (the `Name | (string & {})` escape hatch had silently voided that
  guarantee on every axis, and the fail-closed evaluator turned typos into invisibly hidden UI), and
  `Vocabulary` axes are optional so a no-auth app binds `{ module }` only and a stray permission/flag/role
  clause fails to compile. New **`ItemOf`/`ContextOf`** extractors plus a typed `<ExtensionSlot context=…>`
  deliver the point's declared slot context to the render prop (previously phantom, forcing hosts to smuggle
  it through a separate React context). **`createContributionRegistry`** is generic over the vocabulary and
  **throws on two co-visible contributions to one point sharing an id** (ids double as render keys). New
  **`createStaticCapabilities`** ships the no-snapshot `CapabilityReader` (modules/permissions/roles default
  `"all"`, flags none) for self-hosted/no-auth apps. Docs flip the recommended composition to *manifests by
  glob, routes registered statically* (a glob-composed tree degrades `Link` **and**
  `useLoaderData`/`useParams`), and gain a TanStack Start (SSR) shim, a no-auth recipe, and a Vite React-dedupe
  callout. See the [ADR-0032](docs/decisions/0032-frontend-contribution-model.md) addendum and
  [the frontend-modules concept doc](docs/concepts/frontend-modules.mdx).

### Added
- **User-context trace & log enrichment — on by default (ADR-0033).** Every handler span is now enriched with
  the calling user, and a matching `ILogger` scope carries that context onto every log line the handler produces,
  so traces and logs can be filtered by user. The Elarion source generator attaches a new
  **`HandlerContextEnrichmentDecorator`** (`Elarion.Pipeline`) immediately inside the `TracingDecorator`, so it
  tags `Activity.Current` (the handler span) and its `BeginScope` wraps the authorization/validation/handler
  chain — a denied or invalid request is still attributed to its caller. Because it runs in the handler pipeline
  rather than an ASP.NET middleware, it works identically across **every** transport (JSON-RPC, `[HttpEndpoint]`,
  MCP, scheduler jobs, event consumers), reading the dispatch-scope-seeded `ICurrentUser`; anonymous executions
  add nothing. The decorator itself is user-agnostic — it drains whatever the registered **`IHandlerContextEnricher`**
  instances (new seam in `Elarion.Abstractions.Diagnostics`) contribute. The framework ships one,
  **`UserContextEnricher`**, registered by default when current-user support is added (`AddElarionClaimsCurrentUser`),
  emitting `user.id` + `user.roles` + `user.permissions` (OpenTelemetry semantic-convention `user.*` keys, not the
  deprecated `enduser.*`); **email is PII and opt-in**, and user identity is deliberately kept **off metrics**
  (unbounded cardinality) — it rides only the span and the log scope (which surfaces only when the host enables
  `IncludeScopes`). Configure or disable the built-in enricher with **`AddElarionUserContextEnrichment(o => …)`**;
  contribute your own trace tags / log-scope items — a tenant id, a request source, a correlation value — by
  implementing `IHandlerContextEnricher` and registering it with **`AddElarionHandlerContextEnricher<T>()`**
  (composes with, rather than replaces, the built-in one). Zero new package dependencies. See
  [ADR-0033](docs/decisions/0033-user-context-trace-and-log-enrichment.md) and
  [Telemetry & observability › User & request context](docs/capabilities/telemetry.mdx).
- **Inbox for integration-event consumers — on by default (ADR-0022).** Handler-form consumers of an
  `IIntegrationEvent` are now deduplicated automatically: the handler generator attaches the ADR-0021
  `IdempotencyDecorator` with a synthesized `Consumer`-scoped policy — owner = the consuming handler's identity,
  key = the delivered message id — claimed **inside the consumer's own transaction** (it takes over unit-of-work
  ownership from `TransactionDecorator` there), so a redelivered message replays the committed claim instead of
  re-running the consumer's effect. Lease races use `WaitThenReplay` (only a *committed* claim is ever
  acknowledged); a failed `Result` rolls the claim back and the message retries. Opt out with the new
  **`[AllowDuplicates]`** attribute — the consumer-side mirror of `[AllowAnonymous]`, a positive declaration
  that redelivery is harmless (naturally idempotent effect, or dedup delegated to a message-id-keyed
  downstream), restoring the plain transaction decorator (`ELINBX001` when declared off-plane). Claims expire
  after a fixed 24 h, well above the outbox retry window. Attachment is **soft**: with no
  `IIdempotencyStore` registered the consumer runs un-deduped as before; `AddElarionOutbox<T>` and the in-memory
  integration bus now wire `AddElarionIdempotency()` so the store is present by default (pair the outbox with
  `AddElarionIdempotencyEntityFrameworkCore` for claims that survive restarts — inbox rows share the idempotency
  table under a `"consumer"` scope). New **`IEventContext.MessageId`** (`Guid?`, `null` on the domain plane)
  exposes the durable, redelivery-stable message identity — use it (not `CorrelationId`, a tracing id) for
  hand-rolled dedup and as the idempotency key for downstream systems; both delivery tiers also seed it into the
  scope's `IIdempotencyKeyAccessor`. Domain-event and method-form consumers are never inboxed. See
  [ADR-0022](docs/decisions/0022-inbox-idempotent-event-consumers.md) and
  [Handling duplicates](docs/capabilities/events/consuming-events.mdx).
- **Client capability bootstrap (`Elarion.Session`, ADR-0030).** A framework-shipped handler returns one snapshot
  of what the backend offers the current user and deployment — enabled modules, the exposed flags/variants, and
  the user's roles/permissions — so a frontend can hide/adapt UI from a single call (a **read-only UX projection,
  not enforcement**; the handler's `[RequirePermission]`/`[FeatureGate]` is still the real gate). Modules opt in
  by enumeration with **`[ClientFeatures("a","b")]`** (`Elarion.Abstractions.Modules`) on their `[AppModule]`
  type — leak-safe, module-gated — which `AppModuleDiscoveryGenerator` round-trips cross-assembly through the
  Elarion manifest into a gated `configuration.GetClientCapabilityManifest()` (emitted only when a module opts
  in). `SessionHandler` composes the manifest with `IFeatureFlagService`/`IFeatureVariantService`/`ICurrentUser`
  into `SessionResponse { user, modules, flags, variants }` (flag/variant services optional). Wired with
  `AddElarionSession(manifest)` (DI), the bus `MapElarionSession()`, and the concrete REST `MapElarionSession(route)`
  in `Elarion.AspNetCore`; the TypeScript generator emits a self-contained `session-client.ts` with an
  **OpenFeature web-SDK provider** hydrated from one snapshot when the schema exposes `elarion.session`. See
  [ADR-0030](docs/decisions/0030-client-capability-bootstrap.md) and
  [the client-capabilities concept doc](docs/concepts/client-capabilities.mdx).
- **Typed capability vocabulary in the exported schema (ADR-0032).** `JsonRpcSchemaExporter.Generate` accepts an
  optional `JsonRpcSchemaExportOptions` and emits a `capabilities` block — enabled modules with their
  `[ClientFeatures]` names, the structured permission catalog (`{permission,resource,verb}`), and role names —
  auto-resolved by the schema-generation tool from the app's own DI registrations (`ClientCapabilityManifest`,
  now in `Elarion.Abstractions.Modules` beside `[ClientFeatures]`, and `IPermissionCatalog`; both optional, the
  block is omitted when absent so existing schemas stay byte-identical). The TypeScript generator turns the block
  into typed constants and literal unions in `session-client.ts` (`Modules`/`Flags`/`Permissions`/`Roles` with
  `ModuleName`/`FlagName`/`PermissionName`/`RoleName`; accessors take `Name | (string & {})`, falling back to
  `string` on older schemas) — the frontend analog of the generated `ElarionPermissions`, so capability checks
  are compile-checked against the same vocabulary the backend enforces. ADR-0032 also records the frontend
  contribution model (declarative manifests, extension-point tokens, `when` clauses) ahead of its sample-first
  implementation. See [ADR-0032](docs/decisions/0032-frontend-contribution-model.md).
- **Frontend contribution model — `@swimmesberger/elarion-contributions` (ADR-0032, sample-first).** The
  backend's review-isolation rule ("a module only touches its own code") extended to the TypeScript frontend:
  typed extension-point tokens (`defineExtensionPoint` — the frontend `[ModuleContract]`), declarative module
  manifests (`defineModule` + `contribute`; a manifest-level `when` ANDs into every contribution), the
  fail-closed AND `when` evaluator over the capability snapshot (a **read-only UX projection, never security**),
  and the deterministic `createContributionRegistry`. Framework-free, dependency-free core with a `/react`
  sub-export (`ContributionProvider`/`useContributions`/`<ExtensionSlot>`) and a `/tanstack-router` sub-export
  (one `redirectUnless` route guard sharing the `when` semantics) — React and the router are optional peers.
  Point payload shapes, the app shell, module discovery, and route composition stay app-owned (no UI kit, no
  router machinery — non-goals). The Billing web sample is restructured into `platform/` + `modules/*` with
  `import.meta.glob` discovery (a new module is a new folder), proving an app-owned sidebar point and a
  module-owned cross-module row-action point. See
  [the frontend-modules concept doc](docs/concepts/frontend-modules.mdx).
- **Imperative handler transport mapping (ADR-0031).** `HandlerDispatcher.Map<TRequest, TResponse>(name, transports)`
  is documented and reused as the host-facing seam for exposing a handler whose class the host does not own
  (framework-shipped, third-party, or startup-decided) — kept under its existing name. REST stays a concrete,
  hand-authored `MapElarionX(route)` per handler (RDG/AOT-safe; no generic HTTP map, by design). The session
  bootstrap is the first consumer. See [ADR-0031](docs/decisions/0031-imperative-handler-transport-mapping.md).
- **Declarative request validation (`Elarion.Validation`, ADR-0027).** Two-tier validation replaces the
  FluentValidation integration: **tier 1** is standard `System.ComponentModel.DataAnnotations` attributes on the
  request DTO (`[Range]`, `[MinLength]`/`[MaxLength]`/`[Length]`/`[StringLength]`, `[RegularExpression]`,
  `[EmailAddress]`, `[Url]`, `[Base64String]`; requiredness from NRT + `required`, no `[Required]` needed) —
  enforced at runtime **and** exported to every contract surface — while **tier 2** (cross-field, conditional,
  async/DB rules) lives in the handler, returning `AppError.Validation`/`Conflict` inside the transaction. The
  handler generator auto-attaches the framework `ValidationDecorator` (`Elarion.Abstractions.Pipeline`, just
  inside the feature gate, pre-transaction) for any handler whose request graph carries validation attributes —
  zero cost when unannotated — over the new `IRequestValidator` seam (`Elarion.Abstractions.Validation`,
  field-path-keyed `RequestValidationErrors`). The opt-in **`Elarion.Validation`** package (ADR-0017 shape)
  adapts `Microsoft.Extensions.Validation`'s runtime base (never its generator) via `AddElarionValidation()`;
  a new `ValidationResolverGenerator` emits per-module resolvers with constant-constructed attribute arrays
  (no runtime attribute reflection), gated by module enablement. Custom constraints subclass a mapped attribute
  (`[Slug] : RegularExpressionAttribute`) and get enforcement plus every schema for free. New diagnostics
  `ELVAL001` (response cannot represent failure) and `ELVAL002` (attributes present but `Elarion.Validation`
  not referenced — documented but unenforced). See [ADR-0027](docs/decisions/0027-declarative-request-validation.md)
  and [the validation concept doc](docs/concepts/validation.mdx).
- **Constraint export across every contract surface.** The same DataAnnotations flow into all four surfaces:
  `JsonRpcSchemaExporter` injects the JSON Schema keywords (`[Range]` → `minimum`/`maximum` + exclusive
  variants; length attributes → `minLength`/`maxLength`, `minItems`/`maxItems` on arrays;
  `[RegularExpression]` → `pattern`; `[Url]`/`[Base64String]`/`[EmailAddress]` → `format`); MCP tool input
  schemas share the same builder; OpenAPI gets the set natively from Microsoft (verified reflection-off) plus a
  new `[EmailAddress]` → `format: "email"` parity transformer in `Elarion.AspNetCore.OpenApi`; and the
  TypeScript client generator's Zod emitter maps the keywords (`.min`/`.max`, `.regex`, `.gte`/`.lte`,
  `.gt`/`.lt`, array `.min`/`.max`, `.int()`, `.uuid()`/`.email()`/`.url()`). The generated client emits
  **params schemas** (`rpcParamsSchemas`) beside result schemas and **pre-validates request params by default**
  (`validateParams: false` opts out; a local failure throws `RpcParamsValidationError`, batch items included;
  Zod v3 and v4 compatible) — a tier-1 violation never costs a round trip, and what the client pre-validates is
  by construction what the server enforces.
- **OpenAPI for the HTTP transport (`Elarion.AspNetCore.OpenApi`).** An opt-in sibling over
  `Microsoft.AspNetCore.OpenApi` that brings the `[HttpEndpoint]` REST transport to schema/contract parity with
  JSON-RPC (ADR-0026). `AddElarionOpenApi()` + `app.MapOpenApi()` serve a standard OpenAPI document; Elarion adds
  the wiring Microsoft can't: canonical-JSON schema generation (reflection-off), the module tags the generator
  now emits (`.WithTags`), normalized operation ids, and the idempotency contract for `[Idempotent]` handlers
  (an `Idempotency-Key` header parameter + an `x-elarion-idempotent` vendor extension, the OpenAPI analog of the
  JSON-RPC `idempotent` flag). Client generation is off-the-shelf (`openapi-typescript` + `openapi-fetch`, or
  Kiota) and build-time export reuses `Microsoft.Extensions.ApiDescription.Server` — no bespoke generator and no
  Elarion OpenAPI MSBuild package. Pins `Microsoft.OpenApi` ≥ 2.7.5 (advisory GHSA-v5pm-xwqc-g5wc). See
  [`docs/capabilities/transports/openapi`](docs/capabilities/transports/openapi.mdx).
- **`AddElarionHttpJson()`.** Base HTTP-transport wiring in `Elarion.AspNetCore` that aligns ASP.NET's
  `Microsoft.AspNetCore.Http.Json` options with the canonical `IElarionJsonSerialization` configuration, so the
  `[HttpEndpoint]` transport (de)serializes request bodies and responses through the source-generated contexts
  with reflection off — closing a latent gap where a `[HttpEndpoint]` JSON body could not deserialize under the
  AOT-strict default. Idempotent, a deliberate global alignment, and overridable by a later
  `ConfigureHttpJsonOptions`; `AddElarionOpenApi()` calls it.
- **Host-priority JSON resolver overrides.** `ElarionJsonOptions.OverrideTypeInfoResolvers` composes ahead of
  every `TypeInfoResolvers` entry, so a host can override how any type serializes — including envelope types a
  transport context registers first-match (previously unbeatable). The full chain is overrides → contributed
  resolvers → the framework context → the optional reflection fallback.
- **Blob upload lifecycle + resumable (tus) transports.** A "pre-upload then reference, reclaim if
  abandoned" model for attaching files to an entity over a JSON transport that cannot carry binary.
  `Elarion.Blobs` gains the provider-neutral, S3-free lifecycle: `BlobLifecycleState` (`Pending`/`Committed`),
  the optional `IBlobLifecycle` capability (`CommitAsync` promotes a pending blob **atomically with the caller's
  entity insert**; `DeleteExpiredPendingAsync` is the garbage-collection entry point), and additive
  `BlobUploadRequest.InitialState`/`ExpiresAt` (defaults keep a plain `SaveAsync` permanent).
  `Elarion.Blobs.PostgreSql` implements it with `state`/`expires_at` columns behind a partial index over pending
  rows, a set-based delete that never races a concurrent commit, and the lease-free `BlobGarbageCollector` sweeper
  (`AddPostgreSqlBlobLifecycle`). Two open HTTP transports produce pending blobs over the neutral `IBlobStore`:
  `Elarion.Blobs.Tus` implements **tus 1.0** (Creation/Core/Expiration/Termination) — the open, resumable,
  large-file, browser-close-resilient protocol supported natively by Uppy (`@uppy/tus`) and `tus-js-client`,
  returning the reference in the `Elarion-Blob-Ref` header — with durable PostgreSQL staging in
  `Elarion.Blobs.Tus.PostgreSql` so in-progress uploads survive restarts; and `Elarion.Blobs.AspNetCore` adds a
  minimal direct-upload endpoint (`MapElarionBlobUploads`) for FilePond and plain `fetch`/`<form>` clients. The S3
  wire protocol (SigV4/`aws-chunked`/XML) is a deliberate non-goal. See
  [`docs/capabilities/blob-uploads`](docs/capabilities/blob-uploads.mdx).
- **Idempotency (`[Idempotent]`).** A transport-neutral, declarative way to make a command handler safe to
  retry: a generated decorator owns a unit-of-work transaction, writes the idempotency key **atomically with
  the handler's business writes**, lets a database unique constraint reject duplicates, and replays the stored
  `Result<T>`. Single-transaction and success-only by default (a failure rolls back → the key stays retryable;
  opt-in `StoreFailures = Definitive` stores definitive failures via a savepoint); a concurrent in-flight
  duplicate fast-fails with `409` (Postgres `lock_timeout`, configurable `WaitThenReplay`); reuse with a
  different body is `422`; missing key is `400`. Concurrent duplicates serialize across nodes on the database
  unique constraint — **no external distributed lock**. The `[Idempotent]` attribute, the
  `IIdempotencyStore`/`IIdempotencyKeyAccessor`/`IUnitOfWork` seams, the `IdempotencyDecorator`, and a
  framework-owned `TransactionDecorator` live in `Elarion.Abstractions`; the in-memory default store and
  `AddElarionIdempotency` in `Elarion`; the durable EF Core store, `[GenerateElarionIdempotencyKeys]`
  generator, and retention purge in the new **`Elarion.Idempotency.EntityFrameworkCore`**; the EF unit of work
  in the new **`Elarion.EntityFrameworkCore.UnitOfWork`**; and the `Idempotency-Key` HTTP header capture
  (`UseElarionIdempotencyKey`) in `Elarion.AspNetCore`. New diagnostics `ELIDEM001`–`ELIDEM004` and
  `ELIDEMEF001`. See [ADR-0021](docs/decisions/0021-idempotency.md) and
  [the idempotency concept doc](docs/concepts/idempotency.mdx).
- **Idempotency across the wire.** The JSON-RPC schema export now marks each `[Idempotent]` operation with
  `"idempotent": true`, the server reads a per-call key at `params._meta` (batch-correct, JSON-RPC and MCP), and
  the generated TypeScript client **attaches an idempotency key by default** (a `crypto.randomUUID()` at
  `params._meta`) to those operations — configurable via `idempotency` on the client and a per-call
  `idempotencyKey` override. Retry stays a higher-layer concern (e.g. TanStack Query); the client only attaches
  the key.
- **Canonical JSON serialization (`IElarionJsonSerialization`).** One framework-owned `JsonSerializerOptions`
  that every subsystem reads — JSON-RPC, MCP, idempotency, caching, the outbox, and settings — so a host
  configures the JSON type context once instead of threading options into each subsystem. `ElarionJsonOptions`
  (naming knobs, an ordered resolver list, `EnableReflectionFallback`, `PostConfigure`) and the
  `IElarionJsonSerialization` accessor live in `Elarion.Abstractions.Serialization`; `AddElarionJson` /
  `ConfigureElarionJson` register and compose it over a plain contributor seam (no `Microsoft.Extensions.Options`
  dependency). The generated `AddElarion(configuration)` contributes every enabled module's source-generated
  context automatically. **AOT-strict by default:** a type missing from every source-gen context throws instead
  of silently reflecting; reflection is opt-in via `EnableReflectionFallback`. No bare `JsonSerializerOptions`
  is registered in DI, so Elarion never collides with a host's own registration. See
  [ADR-0023](docs/decisions/0023-canonical-json-serialization.md).
- **Cross-instance scheduler coordination (recurring jobs run once per cluster).** The in-memory scheduler was
  the last stateful subsystem without a durable rung: on N nodes every recurring job fired N times. A node now
  claims each recurring occurrence in a shared database row right before executing it, and exactly one node wins
  — the others record a `claimed-elsewhere` skip and continue. Cron occurrences claim their exact wall-clock slot
  (`INSERT … ON CONFLICT DO NOTHING`; the composite PK is the fence); fixed-rate/fixed-delay occurrences — whose
  due times are node-anchored — dedupe by a one-interval window serialized with a per-job `pg_advisory_xact_lock`;
  one-time startup schedules stay per-node. The transport-neutral `IScheduledOccurrenceCoordinator` seam and the
  always-claims `LocalScheduledOccurrenceCoordinator` default live in `Elarion.Abstractions`/`Elarion` (single-node
  behavior is unchanged, no I/O); the new **`Elarion.Scheduling.EntityFrameworkCore`** ships the claims table
  (`UseElarionSchedulerClaims` / `[GenerateElarionSchedulerClaims]` with a bundled generator, `ELSCH001`), the
  `EfCoreScheduledOccurrenceCoordinator`, and a retention purge worker
  (`AddElarionSchedulerEntityFrameworkCore<TDbContext>`). Coordination fails **closed** (an unreachable claims
  database skips the occurrence rather than duplicating it); recurring occurrences are at-most-once. Durable
  runtime one-shot jobs remain the planned phase 2. `[ScheduledJob(Placement = JobPlacement.EveryNode)]` opts a
  recurring job out of coordination for jobs that maintain process-local in-memory state (which coordination would
  leave stale on the losing nodes). See [ADR-0025](docs/decisions/0025-distributed-scheduler-coordination.md) and
  [`docs/capabilities/scheduling/multi-node`](docs/capabilities/scheduling/multi-node.mdx).
- **Cross-instance settings change notification over PostgreSQL `LISTEN/NOTIFY`.** The EF Core settings store
  composed with the in-process change source was single-instance: a settings write on one node never reached
  another node's `IChangeToken` watchers (or the scheduler's `${…}` live rescheduling) until it restarted. The
  new opt-in **`Elarion.Settings.PostgreSql`** (`AddElarionPostgreSqlSettingsChanges(connectionString | NpgsqlDataSource)`)
  implements the existing `ISettingsChangeSource`/`ISettingsChangePublisher` seams over `LISTEN/NOTIFY`: a hosted
  listener per node fires watch tokens on every node in commit order (and fires all watches after a reconnect,
  since PostgreSQL does not queue for absent listeners). A new `IEfCoreSettingsChangeNotifier` seam lets the store
  announce writes on its own connection, so a write inside a caller-owned transaction is delivered only on commit
  and never on rollback. See [ADR-0024](docs/decisions/0024-postgres-listen-notify-settings-changes.md).
- **Streaming PostgreSQL blob reads.** `PostgreSqlBlobStore.OpenReadAsync` no longer buffers the whole blob into
  memory: it opens a dedicated connection from an injected `NpgsqlDataSource` and reads via
  `CommandBehavior.SequentialAccess` + `NpgsqlDataReader.GetStream`, with the reader/command/connection owned by
  the returned `BlobDownload` (released on disposal, double-dispose safe). A read inside a caller-owned ambient
  transaction stays buffered so it sees the caller's own uncommitted writes. See
  [`docs/capabilities/blob-storage`](docs/capabilities/blob-storage.mdx).
- **EF wiring parity across every EF-backed subsystem.** Each now offers the same three affordances: a model-wiring
  method with `tableName`/`schema` overrides and a `snakeCase` toggle, a `[GenerateElarionX]` bundled-generator
  trigger, and case-aware defaults. New bundled generators for the outbox (`[GenerateElarionOutbox]`, `ELOBX001`),
  settings (`[GenerateElarionSettings]`, `ELSET001`), PostgreSQL blob storage (`[GenerateElarionBlobStorage]`,
  `ELBLB001`), and tus staging (`[GenerateElarionTusStorage]`, `ELTUS001`); the existing idempotency-keys,
  resource-grants, and Identity generators gain `TableName`/`Schema` (and Identity `Schema`/`TablePrefix`) knobs.
  The PostgreSQL blob and tus stores now build their raw SQL from the EF model, so the overrides apply to the
  Npgsql content path too.

### Changed
- **Abstractions holds contracts, not implementations — pipeline decorators moved to core (ADR-0034).** The nine
  framework pipeline decorators (`TracingDecorator`, `AuthorizationDecorator`, `ValidationDecorator`,
  `FeatureGateDecorator`, `IdempotencyDecorator`, `ResilienceDecorator`, `CacheDecorator`,
  `CacheInvalidationDecorator`, `TransactionDecorator`) and `HandlerTelemetry` moved out of `Elarion.Abstractions`
  into `Elarion` core — the decorators under the new **`Elarion.Pipeline`** namespace, `HandlerTelemetry` under
  **`Elarion.Diagnostics`**. `Elarion.Abstractions` now holds only contracts (interfaces, attributes, records) and
  grants `InternalsVisibleTo("Elarion")` as the reference implementation. The source generator emits the new
  fully-qualified names; the emitted pipeline order is unchanged and no package gains a new dependency.
  **Breaking (namespace):** code referencing these types directly updates its `using`s — most notably a host's
  OpenTelemetry registration of `HandlerTelemetry.ActivitySourceName`/`MeterName` now imports `Elarion.Diagnostics`.
  See [ADR-0034](docs/decisions/0034-abstractions-holds-contracts-not-implementations.md).
- **Validation errors are field-keyed end to end.** `ValidationErrorData` gains an optional
  `FieldErrors: IReadOnlyDictionary<string, string[]>?` beside the existing flat `Errors` list (additive), and
  `AppError.Validation` gains a field-keyed factory overload. Keys are **wire-named field paths** per the
  canonical JSON naming policy (`"address.street"`, indexers preserved: `"deliveries[1].street"`; the empty key
  is not field-specific). HTTP 400s now surface `FieldErrors` as the standard RFC 7807 `errors` extension (the
  `HttpValidationProblemDetails` shape); JSON-RPC carries the payload in `error.data` — so a client-side Zod
  pre-flight failure and a server 400 address the same field the same way. The TypeScript generator now emits
  params schemas alongside result schemas and the generated client validates params by default (set
  `validateParams: false` to keep the previous behavior).
- **`[HttpEndpoint]` responses serialize through the aligned HTTP JSON options.** `ElarionHttpResults.ToResult`
  now returns `TypedResults.Ok` instead of a custom result type, so request binding, responses, and OpenAPI
  schemas all flow through one `Microsoft.AspNetCore.Http.Json` options object (a host override applies
  consistently to both directions). Consequence: a `[HttpEndpoint]` host must call `AddElarionHttpJson()` (or
  `AddElarionOpenApi()`) so responses serialize under the reflection-off default; by default REST output still
  matches the JSON-RPC/MCP transports for the same DTO.
- **DI registration verbs unified to `AddElarionX`, and blob/tus storage wiring renamed (breaking).**
  `AddInMemoryScheduler` → `AddElarionScheduler`, `AddInMemoryDomainEventBus` → `AddElarionDomainEventBus`,
  `AddInMemoryEventBus` → `AddElarionInMemoryEventBus`, `AddInMemoryIntegrationEventBus` →
  `AddElarionInMemoryIntegrationEventBus`, `AddPostgreSqlBlobStore`/`AddPostgreSqlBlobLifecycle` →
  `AddElarionPostgreSqlBlobStore`/`AddElarionPostgreSqlBlobLifecycle`, `AddMicrosoftResilienceRuntime` →
  `AddElarionResilience`, and the blob model-wiring `UsePostgreSqlBlobStorage` → `UseElarionBlobStorage`.
  **Migration:** rename the calls. The PostgreSQL blob/tus registration methods now require connection info
  (a connection string overload, or an `NpgsqlDataSource` in DI) for streaming reads. `EfCoreSettingsStore`
  takes an `IEfCoreSettingsChangeNotifier` instead of an `ISettingsChangePublisher` + logger;
  `PostgreSqlBlobStore` takes an `NpgsqlDataSource`; `ApplyElarionIdentity`'s signature is now
  `(schema, tablePrefix, snakeCase)`; the PascalCase default resource-grants table is `ElarionResourceGrants`
  (was `ResourceGrants`); and the idempotency/grants index names derive from the table name.
- **Soundness-hardening pass: breaking contract and schema changes (breaking).** A full adversarial audit of the
  framework's concurrency, transaction, authorization, and AOT paths was fixed in one pass; the correctness fixes
  below ride on these breaks. **Migrations:** the idempotency key table gains a leading `operation` PK column
  (`(operation, scope, owner, key)`); `stored_blobs` gains an `owner_id` column and the `tus_uploads` staging
  index changes shape — regenerate migrations for all three. `IOutboxStore.MarkProcessedAsync`/`MarkFailedAsync`
  now take the claimant's `lockId` and return `bool`, and `MarkPermanentlyFailedAsync` parks poison messages.
  `StoredResult<T>` is replaced by a non-generic `StoredResult` (value carried as pre-serialized JSON) so
  `[Idempotent]` round-trips AOT-strict. Resource grants are keyed by `Type.FullName` (was `Type.Name`) — re-key
  existing grant rows or set an explicit `ResourceTypeName` on all three paths. `[CacheInvalidate]` defaults to
  `Scope = Global` (was `CurrentUser` — the old default let an admin's mutation strand another user's cached
  read). Keyset cursors carry a keyset-identity tag (format v2): previously-issued cursors are rejected loudly,
  and a malformed cursor now throws `MalformedCursorException` instead of silently returning page 1.
  `ITusUploadStore.AppendAsync` drops its unused chunk-length parameter. An unseeded `ICurrentUser` is now
  anonymous (`IsAuthenticated == false`) instead of throwing, so deny-by-default fails closed off-transport.
  Nested `IUnitOfWork.BeginAsync` joins the ambient transaction via a savepoint (command-calling-command via
  `IHandlerSender` no longer throws), and `CommitAsync` flushes pending `DbContext` changes before committing.
- **JSON serialization is configured centrally, not per subsystem (breaking).** `AddElarionJsonRpc` and
  `AddElarionMcp` no longer take a `JsonSerializerOptions` parameter, and `JsonRpcOptions.SerializerOptions` is
  removed — both read the canonical `IElarionJsonSerialization`. `ISettingsManager` typed access is now
  ergonomic-only (`GetAsync<T>(key, fallback)` / `SetAsync<T>(key, value)`, resolving type info from the
  accessor) instead of taking an explicit `JsonTypeInfo<T>`. `OutboxOptions.SerializerOptions` is now nullable
  and defaults to the canonical options (its reflection-based default is removed). **Migration:** delete any
  hand-built `JsonSerializerOptions` and its `AddSingleton`; call `AddElarion(configuration)` (which contributes
  module contexts) and, if needed, `ConfigureElarionJson(o => …)` for custom naming/resolvers; drop the options
  argument from `AddElarionJsonRpc`/`AddElarionMcp`.
- **The module bootstrapper is auto-generated as the fixed-name `ElarionBootstrapper` (breaking).**
  `[GenerateModuleBootstrapper]` becomes an **assembly** attribute (`[assembly: GenerateModuleBootstrapper]`)
  and `AppModuleDiscoveryGenerator` emits the host wiring (`AddElarion`, `MapElarionEndpoints`,
  `RegisterHandlers`, `GetMcpMetadata`, …) as a framework-named `ElarionBootstrapper` static in the host's root
  namespace — you no longer declare a `partial class`. Framework-owned names give every Elarion host the same
  composition root (see [ADR-0018](docs/decisions/0018-generated-infrastructure-is-framework-named.md)).
  **Migration:** delete your `[GenerateModuleBootstrapper]` partial class, add
  `[assembly: GenerateModuleBootstrapper]`, and reference `ElarionBootstrapper.RegisterHandlers` (etc.) instead of
  your old type name.
- **Split the web-free Identity model into `Elarion.EntityFrameworkCore.Identity` (breaking).** The
  `[GenerateElarionIdentity]` marker, its bundled source generator, and the `ApplyElarionIdentity` model
  helper moved out of `Elarion.AspNetCore.Identity` into a new `Elarion.EntityFrameworkCore.Identity` package
  that depends only on EF Core + `Microsoft.AspNetCore.Identity.EntityFrameworkCore` (no
  `Microsoft.AspNetCore.App` `FrameworkReference`). A persistence/application layer that owns the `DbContext`
  can now compose the snake_case Identity model **without** pulling in the ASP.NET shared framework — the same
  EF-only ↔ web split as `Elarion.EntityFrameworkCore` ↔ `Elarion.AspNetCore`. The host wiring
  (`AddElarionIdentity`, the `ICurrentUser` mapping, the authorizer) stays in `Elarion.AspNetCore.Identity`.
  **Migration:** reference `Elarion.EntityFrameworkCore.Identity` from the project that declares
  `[GenerateElarionIdentity]` / calls `ApplyElarionIdentity`, and change its `using Elarion.AspNetCore.Identity;`
  to `using Elarion.EntityFrameworkCore.Identity;`. See [`docs/capabilities/identity`](docs/capabilities/identity.mdx).

### Fixed
- **`Result<Unit>` idempotency payloads no longer touch `GetTypeInfo(typeof(Unit))`.** The generated
  `[Idempotent]`/inbox policy for a `Result<Unit>` response stores only the success flag and reconstructs
  `Unit.Value` on replay — previously the emitted serializer would have thrown on an AOT-strict host, because
  `Unit` is registered in no JSON context.
- **Soundness-hardening pass: ~50 adversarially-verified findings across every subsystem.** Highlights by area —
  *outbox:* finalize is lease-guarded (a stalled worker can no longer wipe a legitimate re-claimant's lease and
  trigger cascading redelivery), failed messages retry with exponential backoff instead of head-of-line blocking,
  and an unresolvable event type is parked loudly instead of silently marked delivered. *Idempotency/transactions:*
  `[Idempotent]` on `Result<T>` handlers no longer throws `NotSupportedException` under the AOT-strict default;
  savepoint rollbacks truncate buffered integration events (consumers never see rolled-back state); the
  `WaitThenReplay` path is bounded by `lock_timeout`; the in-memory store/no-op unit of work warn once that they
  are single-process. *Security:* `[Require*]`/`[FeatureGate]` on a base handler class are honored by the
  generator (a derived handler no longer ships unguarded); grants insert via `ON CONFLICT DO NOTHING` (a duplicate
  grant no longer poisons the ambient transaction); same-named entities in different modules no longer share
  authorization; deny-by-default no longer crashes event consumers. *Generators:* cache keys reject non-scalar
  properties and misspelled `KeyProperties` (new `ELCACHE005–007`, `ELPIPE003`, `ELEFC002`, `ELMOD003`
  diagnostics); `[Resilient]`/`[Cacheable]` are rejected on the event-consumer plane; duplicate `DbSet` names and
  unsupported manifest schema versions are diagnosed; the Identity generator no longer squats the host's
  `OnEntitiesConfigured` seam; `HandlerRegistrationGenerator` and `AppModuleDiscoveryGenerator` discover per node
  (no full-compilation re-bind per keystroke) with strict cache-reuse tests. *Transports:* client disconnects are
  no longer logged as errors or answered into aborted requests; a JSON-RPC batch with an `Idempotency-Key` header
  is rejected instead of replaying item 1's response for item 2; REST responses serialize through the canonical
  JSON options and fail loudly when they're missing; MCP tool exceptions are logged and sanitized. *Runtime:*
  unconditional settings writes no longer drop updates under concurrency; the settings refresher loads before
  other hosted services start; a live variable reschedule no longer injects a concurrent extra run; `[Resilient]`
  jobs without the resilience runtime fail fast at startup; variant resolution is race-free on concurrent first
  calls; the in-memory event pump survives dispatch faults, warns on never-committed events, and domain-event
  re-entrancy is depth-bounded. *Blobs/tus:* `AddElarionTusPostgreSql` wires the pending-blob GC (no more
  leaked pending blobs), finalization is atomic, completed sessions are reaped, ownership checks are exact and
  fail-closed. Every fix carries a regression test (798 tests, Postgres-backed races included).
- **`ICurrentUser` now resolves inside JSON-RPC and MCP handlers (and so does authorization).** The
  dispatchers run each call in a fresh DI child scope, which does not inherit the request scope's scoped
  `CurrentUserSnapshot`, so a handler injecting `ICurrentUser` — or the `AuthorizationDecorator`, which
  reads it — threw `"Current user has not been initialized"`. Now every dispatcher-based transport seeds the
  per-call snapshot the same way: it captures the authenticated principal at its boundary (`HttpContext.User`
  for JSON-RPC, `RequestContext.User` for MCP) into a `DispatchScopeContext`, and one initializer applies it —
  no `IHttpContextAccessor`, no `AsyncLocal`. Plain HTTP endpoints were unaffected. See
  [`docs/capabilities/current-user`](docs/capabilities/current-user.mdx).

### Added
- **Source-generated permission catalog (`ElarionPermissions` + `IPermissionCatalog`), Kubernetes-RBAC style.**
  `[RequirePermission]` now takes a **`(resource, verb)`** pair (`[RequirePermission("properties", Verbs.Read)]`,
  enforced as the composed claim `properties.read`; verb vocabulary open via the new `Verbs` constants or any
  string). A new `PermissionCatalogGenerator` discovers every `[RequirePermission(resource, verb)]`/`[RequireRole]`
  and emits two surfaces, so seeding and role→permission policy enumerate the full set instead of a hand-kept
  `Permissions.All`/`ReadOnly` list — zero central edits per guarded handler. The **compile-time**
  `ElarionPermissions` static (in the assembly's root namespace) exposes `All`/`Roles`/`ByModule`/`ByResource`/
  `ByVerb` and typed accessors (`ElarionPermissions.Properties.Read`), aggregated cross-assembly from the Elarion
  manifest — so static role policy reads like K8s rules (`ByResource["properties"]`, `ByVerb["read"]`). The
  **runtime** `IPermissionCatalog` (`Elarion.Abstractions.Authorization`, registered by `AddElarionAuthorization`)
  exposes the same data for dynamic enumeration, aggregating one `PermissionCatalogModule` per module via the
  module's gated `ConfigureDefaultServices` (cross-assembly; a disabled module contributes nothing). Generation is
  on under `[assembly: UseElarion]` or `[assembly: GeneratePermissionCatalog]`; diagnostics `ELPERM001` (handler
  under no module) and `ELPERM002` (colliding typed accessor). See
  [`docs/concepts/authorization`](docs/concepts/authorization.mdx#permission-catalog).
- **Per-call dispatch-scope seeding (`Elarion.JsonRpc`).** `IDispatchScopeInitializer` +
  `DispatchScopeContext` + the `IServiceProvider.CreateDispatchScope(context)` / `SeedScope(context)` helpers
  carry request-boundary state into the fresh per-call child scope dispatcher-based transports (JSON-RPC, MCP)
  create. Current-user is one registered consumer; hosts add their own (tenant, correlation, …) via
  `TryAddEnumerable`.
- **Off-HTTP `ICurrentUser` (`Elarion` core).** `AddElarionClaimsCurrentUser` + `ClaimsPrincipalCurrentUser` +
  `ClaimsCurrentUserOptions` provide the claims-based `ICurrentUser` with **no ASP.NET dependency**, so gRPC,
  console, or any custom transport gets identity + `[Require*]` authorization by referencing only `Elarion`.
- **`HandlerInvoker.InvokeAsync<TRequest,TResponse>` (`Elarion` core).** The typed-direct transport entry
  point: creates a seeded dispatch scope, resolves the decorated handler, invokes it, and disposes the scope —
  the sibling of the JSON-RPC/MCP name-based dispatch path for transports that know the static handler type.
- **`IAppErrorTranslator<TError>` (`Elarion.Abstractions`).** A seam for mapping `AppError` to a transport's
  wire error type. The JSON-RPC bridge resolves `IAppErrorTranslator<RpcError>` (defaulting to the
  `AppErrorMapper` codes), so a host can override JSON-RPC error codes by registering its own.
- **Runtime-changeable, database-backed settings (`Elarion.Settings` + `Elarion.Settings.EntityFrameworkCore` + `Elarion.Settings.Configuration`).**
  Key/value settings with **swappable abstractions on both sides**. The sink side is `ISettingsStore` plus the
  listen seam `ISettingsChangeSource`/`ISettingsChangePublisher`, keyed by an extensible `(Kind, Owner)`
  `SettingsScope` (`Global` and `User(ownerId)` ship) and hierarchical, virtual `:`-separated keys, with
  optimistic-concurrency `SettingWriteResult`. The shipped in-process backend (single-instance notify) and an
  EF Core backend (`EfCoreSettingsStore<TDbContext>`, change-tracker-free immediate writes, `UseElarionSettings`)
  both implement it. The consuming side is the AOT-clean scoped `ISettingsManager` (typed access via source-gen
  `JsonTypeInfo<T>`, per-user scope resolved from `ICurrentUser`, failing closed when unauthenticated), plus an
  `IConfiguration`/`IOptionsMonitor` adapter (`AddElarionSettingsConfiguration`) with `IChangeToken` reload.
  See [`docs/concepts/settings`](docs/concepts/settings.mdx) and [ADR-0011](docs/decisions/0011-runtime-settings-subsystem.md).
- **Reusable variable substitution (`Elarion.Abstractions.Substitution`).** Spring-style `${key:-default}`
  placeholders resolved from a pluggable `IVariableSource` (and change-observable `IObservableVariableSource`):
  `VariableSubstitution` supports both whole-value and embedded substitution, `ConfigurationVariableSource`
  bridges to `IConfiguration` (and its reload token), and `AddElarionVariableSubstitution` registers a default
  source. A general building block, not tied to any one subsystem. See
  [`docs/concepts/variable-substitution`](docs/concepts/variable-substitution.mdx).
- **Scheduler live reschedule on variable change.** The in-memory scheduler resolves `${...}` schedule
  variables through `IVariableSource`; when the source is observable, a watched-variable change **reschedules
  affected recurring jobs immediately** (signature-based change detection; supersede + re-enqueue; mid-run
  fixed-delay chains reschedule themselves), beyond per-occurrence next-fire pickup.
- **Resource-based & data-level authorization (`Elarion.Abstractions` + `Elarion.Paging` + new `Elarion.Authorization.EntityFrameworkCore`).**
  Per-resource read/write checks **and** efficient database-level filtering, as two opt-in legs driven from one
  declarative source. *List filtering:* `[ResourceFilter<TEntity>]` generates a reflection-free `IQueryAuthorizer<T>`
  predicate composed into `IQueryable` via `.WhereAuthorized(spec, user)` **before** paging — the database filters,
  with correct counts/pagination (never in-memory `@PostFilter`); rules are `OwnerProperty`/`TenantProperty` plus a
  `Shared` rule that becomes a correlated `EXISTS` over the grants table for the caller's user **or any of their
  roles**. *Point check:* `[RequireResource(typeof(T), Operation = "read", Id = nameof(Req.Id))]` extends the
  `AuthorizationDecorator` with an `IResourceAuthorizer` seam — the resource id is a compile-checked request path, not
  an `#id` string ([ADR-0012](docs/decisions/0012-dynamic-variable-references.md)) — and `IResourceAuthorizer` doubles
  as the escape hatch for handler-owned pre-write validation. *Sharing:* the auth-provider-neutral
  `Elarion.Authorization.EntityFrameworkCore` package ships a DB-native `ResourceGrant` table (user/role shares),
  `IResourceGrantStore`, the grants-backed authorizer, and the Identity-consistent `[GenerateElarionResourceGrants]`
  DbSet generator — composes with, but does not depend on, ASP.NET Identity. Generated filter specs are
  auto-registered and module-feature-gated by the host bootstrapper via the assembly manifest. See
  [`docs/concepts/resource-authorization`](docs/concepts/resource-authorization.mdx) and
  [ADR-0013](docs/decisions/0013-resource-and-data-level-authorization.md).

### Changed
- **The event bus is now pub/sub-only; request/reply is unified under typed dispatch (breaking).** See
  [ADR-0010](docs/decisions/0010-event-bus-is-pub-sub-only.md). `IDomainEventBus.RequestAsync` and the single-
  **responder** role of `[ConsumeEvent]` are removed — `[ConsumeEvent]` now means exactly one thing, a fan-out
  subscriber (handler form returns `Result<Unit>`/`IHandler<TEvent>`; method form returns `void`/`Task`/`ValueTask`
  or the non-generic `Result`/`Task<Result>`/`ValueTask<Result>` for a failure channel), and a `Result<T>` *with a
  value* is rejected (`ELEVT005` handler-form, `ELEVT002` method-form; the
  duplicate-responder `ELEVT004` is retired). For an in-process, in-transaction typed request/reply, inject the new
  **`IHandlerSender`** and call `SendAsync<TRequest, TResponse>(request, ct)` (auto-registered by the generated
  bootstrapper, or `AddElarionHandlerSender()` manually), or inject `IHandler<TRequest, Result<TResponse>>` directly. To upgrade a former
  responder: drop `[ConsumeEvent]` and the `IDomainEvent` marker on the request, and replace
  `bus.RequestAsync<R, T>(r, ct)` with `sender.SendAsync<R, T>(r, ct)`.
- **Named handler dispatch is now transport-agnostic (breaking — source + generated host wiring).** A handler
  is mapped onto a single transport-neutral request/reply bus, `HandlerDispatcher`
  (`Elarion.Abstractions.Dispatch`), which owns no serialization or wire format; **JSON-RPC and MCP are now
  thin adapters over that one shared bus** (each serving only the operations flagged for its surface) rather
  than two separate dispatcher instances. `[RpcMethod]` is renamed to **`[Handler]`** and its name is now
  **optional** — when omitted the operation name is inferred by convention as `{module}.{operation}` (the
  handler type name minus a `Handler`/`Command`/`Query`/`Request` suffix, camelCased; an explicit name is
  recommended for stable public/wire contracts). `RpcTransports` is renamed to **`HandlerTransports`**
  (`JsonRpc`/`Mcp`/`All` unchanged) and `[McpMethod]` to **`[McpHandler]`**. The generated host wiring's two
  methods `RegisterRpcMethods`/`RegisterMcpMethods` (and the per-module `Add{Module}JsonRpc`/`Add{Module}Mcp`)
  are replaced by a single **`RegisterHandlers`** (per-module `Add{Module}Handlers`) that both adapters
  resolve, and the JSON-RPC `MapHandler` bridge is removed in favor of mapping onto the `HandlerDispatcher`
  (`dispatcher.Map<Req,Resp>("x")` / `dispatcher.MapDelegate<Req,Resp>("x", fn)`). To upgrade: rename the
  attributes/enum, and change host calls to pass `ModuleBootstrapper.RegisterHandlers` to
  `AddElarionJsonRpc(serializerOptions, …)` and `AddElarionMcp(metadata, serializerOptions, …, configure)`. Pass
  the **same** `RegisterHandlers` delegate to both transports — the shared bus is built once (first registration
  wins). MCP tool calls now dispatch directly through the bus and no longer emit the JSON-RPC transport-level OTel
  span/metric (handler-level tracing is unchanged); operation names must be unique across the bus, and a collision
  is reported at compile time (`ELRPC003`) and rejected at registration.
- **`Elarion` core is now transport-agnostic — it no longer references `Elarion.JsonRpc`.** The
  transport-neutral dispatch-scope rail (`DispatchScopeContext` / `IDispatchScopeInitializer` /
  `CreateDispatchScope` / `SeedScope`) moved to `Elarion.Abstractions` (namespace
  `Elarion.Abstractions.Dispatch`), and the JSON-RPC handler bridge (`MapHandler` / `AppErrorMapper` /
  `JsonRpcAppErrorTranslator`) moved from core into `Elarion.JsonRpc` (which now references
  `Elarion.Abstractions`). JSON-RPC is genuinely package-optional: reference `Elarion.JsonRpc` only when using
  the dispatcher. **Breaking namespaces (pre-1.0):** the rail types are now under `Elarion.Abstractions.Dispatch`;
  `MapHandler` / `AppErrorMapper` are now under `Elarion.JsonRpc`.
- **`ClaimsPrincipalCurrentUser` materializes claims lazily** (on first access, cached) instead of eagerly on
  `Initialize`, so seeding a fresh snapshot per dispatch call costs nothing until a claim is read and no
  claims are parsed twice — making per-call seeding uniform across transports without copying.
- **Identity re-layered into core (breaking for direct users of the ASP.NET types).** The claims snapshot,
  options, and current-user initializer moved from `Elarion.AspNetCore` to `Elarion` core
  (`ClaimsPrincipalCurrentUser` / `ClaimsCurrentUserOptions`); `CurrentUserSnapshot` and
  `AspNetCoreCurrentUserOptions` are removed. `AddElarionCurrentUser` (ASP.NET) now delegates to
  `AddElarionClaimsCurrentUser` and takes `ClaimsCurrentUserOptions`; the middleware seeds the request scope
  via `SeedScope`. Behavior is unchanged for HTTP hosts.
- **Breaking (custom batch strategies):** `IBatchExecutionStrategy.ExecuteAsync` takes a
  `DispatchScopeContext context`; strategies create each per-item scope via
  `CreateDispatchScope(context)` so scoped state (current user, …) is seeded per item.
- The in-memory scheduler now resolves `${...}` schedule variables through the general `IVariableSource` seam
  (config-backed by default via `AddElarionVariableSubstitution`) instead of taking `IConfiguration` directly,
  so the variable source is swappable. `ScheduledJobSchedule.Resolve(IConfiguration)` is unchanged and gains a
  `Resolve(IVariableSource)` overload.

### Removed
- **Breaking:** the FluentValidation integration is removed (pre-1.0, no compatibility shim, ADR-0027):
  `[GenerateModuleValidators]`, `ModuleValidatorRegistrationGenerator` (the per-module `IValidator<T>`
  registration), and every FluentValidation package reference are gone. Imperative rule lambdas were invisible
  to every contract surface — exportable rules hid in unexportable places. **Migration:** move shape constraints
  onto the request DTOs as DataAnnotations (one declaration now feeds runtime enforcement, `rpc-schema.json`,
  OpenAPI, MCP tool schemas, and the Zod client), move business rules into the handler (inside the transaction),
  reference `Elarion.Validation`, and call `services.AddElarionValidation()`. The Billing sample is migrated
  accordingly and drops its hand-written `ValidationDecorator` for the framework one. Teams that want
  FluentValidation can still wire it as an app-owned decorator — which is all it ever was.
- **Breaking:** `ConfigPlaceholder` (`Elarion.Abstractions.Scheduling`) is removed in favor of the general
  `VariableSubstitution`. The common surface — `ScheduledJobSchedule.Resolve(IConfiguration)` — is unchanged;
  only direct callers of the low-level helper need to switch (same `${key:-default}` semantics).

### Documentation
- The validators concept doc is replaced by [`docs/concepts/validation`](docs/concepts/validation.mdx) — the
  two-tier model, the export story, field-keyed errors, and the honest server-only boundary; the tutorial,
  reference tables, and transport docs follow (ADR-0027).
- New [`docs/concepts/transports`](docs/concepts/transports.mdx): authoring a new transport (gRPC, console, …)
  — the seams, the scope rail, off-HTTP `ICurrentUser`, the two invoke paths, and `ErrorKind` mapping.

## [0.2.2] - 2026-06-27

### Added
- **Authorization building blocks (`Elarion.Abstractions.Authorization` + `Elarion` core).** Declarative,
  transport-neutral handler authorization: the `[RequireClaim]`, `[RequirePermission]` (sugar over the
  configured permission claim), `[RequireRole]`, `[RequirePolicy]`, and `[AllowAnonymous]` attributes; an
  async `IAuthorizer` seam with the default `ClaimsAuthorizer` (over `ICurrentUser`, **no ASP.NET dependency**);
  a transport-neutral named-policy seam (`IAuthorizationPolicy`) where `[AuthorizationPolicy("name")]`
  auto-registers a policy per module like `[Service]` (or register manually via `AddElarionAuthorizationPolicy`);
  and an `AuthorizationDecorator` the source generator **auto-attaches** as the outermost functional gate when
  a handler carries a `Require*` attribute. An assembly-/`[AppModule]`-scoped `[ElarionAuthorizationDefaults]`
  flips to secure-by-default (deny unless `[AllowAnonymous]`). `AppError.Unauthorized` (HTTP 401) joins
  `Forbidden` (403), and `ICurrentUser` gains claim access (`HasClaim`/`GetClaimValues`). The same `[Require*]`
  handlers are enforced identically under JSON-RPC, MCP, and HTTP, regardless of the authentication provider.
  See [`docs/concepts/authorization`](docs/concepts/authorization.mdx) and
  [ADR-0009](docs/decisions/0009-authorization-building-blocks.md).
- **`Elarion.AspNetCore.Identity` — optional ASP.NET Core Identity integration.** `AddElarionIdentity<TUser, TRole, TKey, TDbContext>`
  wires Identity + EF stores against a **plain** `DbContext` (no `IdentityDbContext` inheritance). A bundled
  generator, triggered by `[GenerateElarionIdentity<TUser, TRole, TKey>(SnakeCase = true)]` on a `[GenerateDbSets]`
  context, emits the Identity `DbSet`s and a self-contained snake_case-ready model (via the EF generator's new
  neutral `OnEntitiesConfigured` seam) — so the context composes Identity instead of inheriting it. The same
  authorization works with Entra ID / any OIDC-JWT by configuring `AddElarionCurrentUser` claim mapping, no
  Identity package required. See [`docs/capabilities/identity`](docs/capabilities/identity.mdx).
- **`samples/Billing` — a compiled, full-stack, current-pattern sample.** The runnable counterpart of
  the [tutorial](docs/tutorial), rebuilt to the recommended solution structure and full feature set:
  a `Billing.Application` (shared-kernel `Domain` entities under no `[AppModule]`, plus `Core`/`Clients`/
  `Invoicing` modules owning their handlers, validators, services, scheduled jobs, integration events,
  resilience policy, **and** each entity's `IEntityTypeConfiguration<T>`), a `Billing.Infrastructure`
  (PostgreSQL `BillingDbContext` with the EF Core outbox, migrations, and SMTP sender), a `Billing.Api`
  host (JSON-RPC + MCP + scheduler/resilience/cache + OpenTelemetry), a `Billing.AppHost` (**.NET Aspire**
  provisioning PostgreSQL and running the API), and a **Vite + React + Tailwind v4 + shadcn/ui + TanStack
  Query** `web` frontend that calls the API through the **generated** JSON-RPC client. It demonstrates the
  full chain — one C# handler becoming a JSON-RPC method, an exported `rpc-schema.json`, a generated typed
  client, and a React call — and builds as part of the solution.
- **Solution-structure guidance ([`docs/concepts/solution-structure`](docs/concepts/solution-structure.mdx)).**
  Documents the recommended layout for consuming apps: keep entities in a shared-kernel
  namespace (under no `[AppModule]`, so cross-aggregate references never trip the `ELMOD002` boundary
  analyzer), let each module own its `[EntityConfiguration]` `IEntityTypeConfiguration<T>` beside its handlers, keep
  infrastructure to platform capabilities, and graduate to a separate assembly only when multiple hosts
  share the code. The tutorial and the Billing sample follow it.
- **`HandlerMetadata` decorator seam (`Elarion.Abstractions.Pipeline`).** A decorator can declare a
  `HandlerMetadata` constructor parameter; the source generator supplies it with the **concrete handler
  type**, so attribute-driven decorators (e.g. authorization reading a `[RequirePermission]`-style
  attribute) read the handler's attributes correctly **regardless of their position** in the chain.
  This replaces the position-dependent `inner.GetType().GetCustomAttribute(...)` pattern, which fails
  **open** when the decorator is not innermost. See
  [decorator pipelines](docs/concepts/decorator-pipelines.mdx).

### Changed
- **The decorator attachment predicate takes `HandlerMetadata` (breaking — source).** A decorator's optional
  `AppliesTo` predicate is now `public static bool AppliesTo(HandlerMetadata handler)` (use `handler.RequestType`
  for request-based checks), giving custom decorators the same handler-attribute-driven attachment the framework's
  built-in decorators use. `HandlerMetadata` carries `HandlerType`/`RequestType`/`ResponseType`. The older
  `AppliesTo(System.Type request)` is reported as `ELPIPE002` rather than silently ignored.
- **`IAuthorizationPolicy` no longer carries `Name`.** A policy's name lives on `[AuthorizationPolicy("name")]`
  or the registration call (one source of truth, and the compile-time metadata a future analyzer uses).
- **EF Core entity participation is now configuration-driven (breaking — source).** The `[DbEntity]`
  marker is removed. An entity opts into a generated context through `[EntityConfiguration]` placed on its
  `IEntityTypeConfiguration<T>` implementation — the single source of truth that drives **both** the
  entity's generated `DbSet<T>` and its `Configure(...)` application (emitted as a reflection-free
  `modelBuilder.ApplyConfiguration<T>(...)` call). There is no separate entity marker, so a configured
  entity is a discovered entity, and only a configuration carrying `[EntityConfiguration]` participates (a
  bare `IEntityTypeConfiguration<T>` is ignored — the previous "schema-only configuration applied without a
  DbSet" path is gone). A single `[EntityConfiguration]` class may implement `IEntityTypeConfiguration<T>`
  more than once, contributing one `DbSet` and one `Configure(...)` call per entity. Scopes move from
  `[DbEntity(scopes)]` to `[EntityConfiguration(scopes)]` with unchanged semantics. The generated
  `IAppDbContext` **interface is removed**: the database is application logic accessed directly (no
  repository *and* no context interface), so `[GenerateDbSets]` now goes on the **concrete partial
  `DbContext`** — the generator emits the `DbSet<T>` properties and `ConfigureEntities` onto the context
  itself — and handlers inject the concrete `DbContext`, not an interface. A new `ELEFC001` warning reports
  an `[EntityConfiguration]` that implements no `IEntityTypeConfiguration<T>`. To upgrade: delete
  `[DbEntity]` from entities and add `[EntityConfiguration]` to each entity's configuration class (writing
  one where an entity had none); move `[GenerateDbSets]` from the `IAppDbContext` interface onto the
  concrete `DbContext`, delete the interface, and change handler/service dependencies from `IAppDbContext`
  to the concrete context. See [Entity Framework Core](docs/capabilities/entity-framework.mdx).
- **Decorator generic-constraint filtering now honors self-referential constraints.** A decorator
  constrained `where TResponse : IResultFailureFactory<TResponse>` (the canonical no-reflection way to
  build a failure result, e.g. in a validation decorator) is now attached only to `Result`-returning
  handlers and cleanly elided from handlers whose response doesn't implement the interface — previously
  the self-reference was treated permissively, which could emit a constraint-violating registration.
- **`Unit` moved to the `Elarion.Abstractions.Results` namespace (breaking — source).** The framework's
  no-value `Unit` type was in the root `Elarion.Abstractions` namespace that every handler imports for
  `IHandler`/`Result`/`AppError`, so a consuming app with its own `Unit` domain type hit `CS0104`
  ambiguity. `Unit` now lives in `Elarion.Abstractions.Results`; code that names it directly (e.g.
  returning `Result<Unit>`) adds `using Elarion.Abstractions.Results;`. The `IHandler<T>` no-content
  sugar and `Result.Success()` are unaffected, so most handlers need no change.
- **Source generators now ship inside their runtime packages (breaking — packaging).** The
  standalone `Elarion.Generators` and `Elarion.EntityFrameworkCore.Generators` analyzer packages are
  no longer published. The Elarion generator's analyzer is bundled into the **`Elarion`** package and
  the EF Core generator's analyzer into the **`Elarion.EntityFrameworkCore`** package, so referencing
  the runtime/marker package is now sufficient — no separate analyzer `PackageReference` and no
  `PrivateAssets="all"` wiring. Because NuGet analyzer assets are not transitive, each analyzer lives
  in exactly one package: application libraries and hosts that need the Elarion generator reference
  `Elarion` directly, and any assembly declaring `[EntityConfiguration]`/`[GenerateDbSets]` types references
  `Elarion.EntityFrameworkCore` directly. This fixes the silent failure where a separate entity
  assembly referencing only the EF Core marker package emitted no manifest and produced zero `DbSet`s.
  To upgrade: remove the `Elarion.Generators` / `Elarion.EntityFrameworkCore.Generators`
  `PackageReference`s; add a direct `Elarion` reference to host projects that previously relied on the
  standalone generator package.
- **`ELMOD002` is now a uniform location-based rule (breaking — analyzer).** The boundary analyzer
  previously flagged only a fixed set of module-internal *kinds* (`[Service]`, handler,
  `[EntityConfiguration]`) and never entities. It now flags **any** cross-module dependency (constructor
  parameter, field, or property) on a type declared *inside* another `[AppModule]` — entity, DTO,
  `[Service]`, handler, or `[EntityConfiguration]` alike — unless that type is a published `[ModuleContract]`.
  Everything *outside* every module (the shared kernel and platform-capability ports) stays shareable, and
  foundation (`Core`) modules get no exemption. A module can therefore *own* its data by placing entities in
  its own namespace (the on-ramp to a bounded context), while shared infrastructure belongs on ports outside
  the modules. To upgrade: route a flagged cross-module dependency through a `[ModuleContract]`, a
  platform-capability port outside the modules (the port/adapter pattern), or the shared kernel — see
  [Cross-module communication](docs/concepts/cross-module-communication.mdx).

### Fixed
- **Generated TypeScript JSON-RPC client now type-checks under `erasableSyntaxOnly`.** The client emitted
  by `@swimmesberger/elarion-jsonrpc-client-generator` declared its error classes (`RpcError`,
  `RpcTransportError`) with TypeScript **constructor parameter properties**, which are rejected when a
  consumer sets `"erasableSyntaxOnly": true` — now the default in `npm create vite@latest -- --template
  react-ts` (TypeScript 6 emits `TS1294`). The classes now declare their fields and assign them in the
  constructor body, so a freshly scaffolded Vite/React app type-checks the generated client without
  editing its `tsconfig`. The public API (constructor signatures, `.code`/`.data`/`.status` members) is
  unchanged.

## [0.2.0] - 2026-06-19

First stable release.

### Added
- **Handlers as event consumers (preferred form).** A class-level `[ConsumeEvent]` on an
  `IHandler<TEvent, Result<T>>` (or the new `IHandler<TEvent>` sugar) whose request *is* the event
  makes a handler a first-class event consumer, dispatched through its full decorator pipeline
  (tracing, resilience, validation, cache-invalidation) — the `[Service]`-method form remains a
  lightweight alternative. The role is inferred from the response type: `Result<Unit>` (the
  `IHandler<TEvent>` sugar) is a fan-out subscriber whose failed `Result` surfaces as an
  `EventConsumerFailedException`; a domain handler returning `Result<T>` (`T ≠ Unit`) is the single
  `RequestAsync` responder. Integration handlers are fan-out only (`ELEVT005`).
- `Elarion.Abstractions`: a `Unit` no-value type, a non-generic `Result`, and the `IHandler<T>`
  convenience interface (sugar for `IHandler<T, Result<Unit>>` via a default interface method that
  bridges the non-generic `Result`), so no-content handlers stay ergonomic without touching the
  decorator pipeline or handler generator.
- `Elarion.Messaging.InMemory`: the simple, best-effort **in-memory integration-event
  (Plane B) bus**, commit-gated by the EF Core DbContext transaction (`AddInMemoryIntegrationEventBus`;
  `AddInMemoryEventBus` also wires the `Elarion` domain tier). Events are buffered per scope and
  delivered after commit by a hosted pump, with the package's EF Core interceptors flushing after
  commit and discarding on rollback automatically — no hand-written transaction decorator and no
  public dispatch-scope seam. A non-durable sibling of the EF Core outbox.
- `Elarion.Messaging.Outbox`: a durable EF Core transactional outbox `IIntegrationEventBus`
  (Plane B). Each integration event is recorded as an `OutboxMessage` row in the caller's `DbContext`
  (committed atomically with the business data, discarded on rollback) and delivered after commit by a
  hosted worker that polls, claims via a provider-neutral conditional-update lease (safe across
  instances, reclaimed after a crash), dispatches to integration consumers on isolated scopes
  (at-least-once — consumers must be idempotent), and finalizes/purges. `UseElarionOutbox(ModelBuilder)`
  adds the table (partial pending index); `AddElarionOutbox<TDbContext>()` wires the tier.
- Per-module `ConfigureDefaultServices` aggregation (`ModuleDefaultServicesGenerator`) that auto-wires
  and feature-gates each module's handlers, services, validators, scheduled jobs, and event consumers,
  so a disabled module registers none of them and authors no longer hand-wire `Add{Module}…` calls.
  Scheduled jobs and event consumers are now module-feature-gated and module-scoped only — the previous
  flat assembly-wide registration methods were removed; a job/consumer that falls under no module is
  reported (`ELSG010`/`ELEVT003`).
- Composite, multi-column offset sorts: `SortMapBuilder.ThenBy` chains fixed-direction tiebreakers and
  a new `SortDirection` enum sets per-entry directions, so a non-unique sort column gets a stable
  trailing key. A client `-`/`+` prefix now flips only the entry's primary column.
- Restructured documentation into a navigable, multi-page guide under [`docs/`](docs/), covering
  getting started, core concepts, JSON-RPC, scheduling, resilience, EF Core, telemetry, and
  reference material.

### Changed
- **Breaking:** blob storage is now streaming-first. `IBlobStore.SaveAsync` takes a
  `BlobUploadRequest` plus a `Stream`, and `GetAsync` is replaced by `OpenReadAsync` returning a
  disposable `BlobDownload` (metadata + open content stream). The `byte[]`/file save styles and the
  buffered/copy-to read styles move to `BlobStoreExtensions` (`SaveAsync(byte[])`, `SaveFromFileAsync`,
  `DownloadContentAsync`, `ReadAllBytesAsync`, `DownloadToAsync`), so `SaveFromFileAsync` no longer
  leaks a local-filesystem assumption onto the interface and a new backend implements only the
  primitives. `BlobUploadRequest.ContentLength` is an optional hint while the recorded `Size` is
  always the actual bytes written. The PostgreSQL store streams seekable writes straight into `bytea`,
  and streams a non-seekable source without buffering when `ContentLength` is supplied (verifying it
  against the actual bytes so `Size` stays truthful), buffering only when the hint is absent; it
  documents a `GetStream`
  read-streaming upgrade path. The shape follows the major blob SDKs (S3/Azure/GCS) so an alternative
  backend drops in cleanly.
- **Breaking:** keyset pagination is declared off the entity. The `[Keyset]` attribute is now generic
  (`[Keyset<TEntity>]`) and goes on a dedicated partial class that the generator fills with the
  `IKeysetDefinition<TEntity>` implementation and a static `Definition`. An entity can have any number
  of orderings, each in its own keyset class. Handlers pass the definition explicitly
  (`source.ToKeysetPageAsync(request, MyKeyset.Definition, selector)`), making keyset symmetric with
  offset paging; the per-entity zero-argument convenience overload is removed. A non-partial or nested
  keyset class reports `ELKEY005`.
- Dedicated documentation for handler caching (`[Cacheable]`, `[CacheInvalidate]`) and current-user
  access (`ICurrentUser`).
- Provider-neutral blob storage contracts and a PostgreSQL-backed blob storage package with EF Core
  model configuration.
- Project health files: `CONTRIBUTING.md`, `SECURITY.md`, `CODE_OF_CONDUCT.md`, this changelog, and
  GitHub issue/PR templates.
- Project logo and icon.

## [0.1.0] - Unreleased

Initial preview line.

### Added
- Module-based application framework with `[AppModule]`, compile-time handler/service/validator
  registration, and a module bootstrapper generator.
- `IHandler<TRequest, TResponse>` use-case model with `Result<T>` and transport-agnostic `AppError`.
- Generated decorator pipelines with assembly/module/handler scoping.
- Declarative handler caching backed by `HybridCache`.
- Transport-neutral JSON-RPC: dispatcher, ASP.NET Core endpoint mapping, batch execution, build-time
  schema export, and a TypeScript/Zod client generator.
- In-memory scheduler with source-generated jobs, fixed-rate/fixed-delay/cron schedules, overlap and
  misfire policies, and runtime one-off jobs.
- Resilience policies (`[ResiliencePolicy]`, `[Resilient]`) with handler and scheduler integration,
  backed by a pluggable runtime (default: Microsoft.Extensions.Resilience / Polly).
- Optional Entity Framework Core source generation for `DbSet`s and entity configuration.
- OpenTelemetry-compatible tracing and metrics for JSON-RPC, scheduling, caching, and resilience.

[Unreleased]: https://github.com/swimmesberger/Elarion/compare/v0.2.4...HEAD
[0.2.4]: https://github.com/swimmesberger/Elarion/releases/tag/v0.2.4
[0.2.3]: https://github.com/swimmesberger/Elarion/releases/tag/v0.2.3
[0.2.2]: https://github.com/swimmesberger/Elarion/releases/tag/v0.2.2
[0.2.0]: https://github.com/swimmesberger/Elarion/releases/tag/v0.2.0
[0.1.0]: https://github.com/swimmesberger/Elarion/releases/tag/v0.1.0
