# AGENTS.md — Elarion

Canonical agent and contributor guidance. Tool entry points point here and must not duplicate content:
`CLAUDE.md` imports this file (`@AGENTS.md`); `.github/copilot-instructions.md` points here; and
`.github/instructions/csharp.instructions.md` scopes the **C# coding standards** section to `**/*.cs` for
Copilot. Add or change guidance **here**, not in the pointer files.

Elarion is a reusable .NET application framework. Keep it independent of every downstream application: do
not mention, depend on, or optimize for any consuming app by name. Application domain code, database
conventions, UI frameworks, and deployment quirks belong in consuming repositories.

This file is agent orientation + conventions. The full per-package purpose/dependency table lives in
[`docs/reference/packages.mdx`](docs/reference/packages.mdx); the "why" behind every decision lives in the
linked ADRs (`docs/decisions/`) and concept docs (`docs/concepts/`). Prefer editing those for depth; keep
this file to the rules an agent must follow.

**Scale positioning.** Elarion targets small-to-mid apps — roughly **1–10 nodes, vertical-first, on the one
PostgreSQL the app already runs**. Shipped defaults (scheduler claims, outbox leases, settings
`LISTEN/NOTIFY`, the `UNLOGGED` L2 cache) must work at that tier and need not scale beyond it. A concern
that only appears past ~10 nodes is resolved by **replacing the seam** (`IScheduledOccurrenceCoordinator`,
`ISettingsChangeSource`, `IIntegrationEventBus`, `IBlobStore`, `IHandlerCache`, …) with a dedicated job
engine/broker — never by growing a default's complexity or config surface. Design test for any new default:
"does it cover 10 nodes on one Postgres?" (ADR-0025).

## Package layout

One line each; see [`packages.mdx`](docs/reference/packages.mdx) for entry points + dependency shapes and the
ADRs for mechanics. Trigger diagnostics are in parentheses.

- `Elarion.Abstractions` — implementation-neutral contracts: handler interfaces (`IHandler<TRequest,TResponse>` + the `IHandler<T>` no-content sugar), `Result<T>`/`Result`/`Unit` (`Unit` in `Elarion.Abstractions.Results`, not the root namespace), `ElarionFile` (the **in-memory small-file payload** for downloads *and* uploads, deliberately bytes-only — content + content type + file name; fixed base64 JSON envelope via `ElarionFileJsonConverter`, seeded in `ElarionFrameworkJsonContext`; large files use the staged-blob tier instead, ADR-0039), CQRS markers (`IRequest`/`ICommand`/`IQuery`), pagination (`Page<T>`, keyset/offset requests), scheduling, variable substitution (`IVariableSource`/`IObservableVariableSource`, Spring `${key:-default}`), messaging markers/buses, the authorization + feature-flag + variant + validation building blocks (attributes + seams — no provider deps; the pipeline **decorators** moved to `Elarion` core, ADR-0034), the named handler bus (`HandlerDispatcher`) + per-call dispatch-scope rail (`Elarion.Abstractions.Dispatch`), canonical JSON (`ElarionJsonOptions`/`IElarionJsonSerialization`/`AddElarionJson`, no `M.E.Options` dep), and source-gen triggers. No runtime-integration deps.
- `Elarion` — runtime primitives: the pipeline **decorators** (`Elarion.Pipeline` — tracing, authorization, feature-gate, validation, idempotency, resilience, caching, context enrichment + `HandlerTelemetry`; moved out of Abstractions, ADR-0034), modules, current-user, default `ClaimsAuthorizer`, in-memory scheduler + domain event bus, resilience policy catalog, session bootstrap (`Elarion.Session`, ADR-0030), default-on user-context trace/log enrichment (`AddElarionUserContextEnrichment` + `IHandlerContextEnricher` seam, ADR-0033). **Bundles the `Elarion.Generators` analyzer** — the single home; reference `Elarion` directly in any assembly needing the generator (analyzer assets aren't transitive). Transport-agnostic + dependency-light (ADR-0017): pulls only `Elarion.Abstractions` + `Microsoft.Extensions.*` *Abstractions*; heavy provider defaults are opt-in siblings.
- `Elarion.Caching` — HybridCache-backed `IHandlerCache` default (`AddElarionHandlerCaching`); pulls `M.E.Caching.Hybrid`. The `[Cacheable]`/`[CacheInvalidate]` seam stays in Abstractions (ADR-0004/0017).
- `Elarion.Caching.PostgreSql` — recommended **L2**: a Postgres `UNLOGGED` table behind HybridCache (`AddElarionPostgreSqlHandlerCaching`, `UseWAL=false`), reusing your Postgres instead of Redis (ADR-0020). Not `IsAotCompatible`.
- `Elarion.Resilience` — Polly-backed `IResiliencePipelineRunner` default (`AddElarionResilience`); pulls `M.E.Resilience`. Catalog/metadata stay in Abstractions; `ResilienceDecorator` lives in `Elarion` core (ADR-0017/0034).
- `Elarion.Actors` — in-memory actor runtime (ADR-0042): plain `[Actor]` classes become keyed, mailbox-protected state machines with **source-generated typed facades** (`IActorSystem.Get<IOrderFulfillment>(orderId)` — facade = class name minus `Actor`, prefixed `I`). Keyed via an `IActorContext<TKey>` ctor param (else singleton); virtual activation + idle passivation (default 5 min) with `IActorLifecycle` load/flush hooks; per-activation DI scope; non-reentrant default + `[Reentrant]` Orleans-style turn interleaving (no `ReenterAfter` — rejected); per-call timeout (default 30 s) is the deadlock backstop; backpressure = Wait/Fail only (no drops — calls are request/reply); no decorator pipeline (handlers stay the gate; actors are module-internal, ELMOD002). `ActorRegistrationGenerator` in `Elarion.Generators` (`[GenerateActors]`/`[UseElarion]`; per-module `Add{Module}Actors` via the `AddActors` hook; ELACT001–005). Telemetry source/meter `Elarion.Actors`; exceptions cross the mailbox unwrapped with actor-side stack traces. **Single-node by design** — need a cluster: swap to Orleans/Akka.NET/Proto.Actor, never grow this default (ADR-0025).
- `Elarion.Validation` — `M.E.Validation`-backed `IRequestValidator` default (`AddElarionValidation`, also calls `AddElarionJson`); runtime base only (`ExcludeAssets="analyzers"` — Elarion's `ValidationResolverGenerator` supplies metadata). App references it to enforce validation + silence `ELVAL002`. `IsAotCompatible` (ADR-0027).
- `Elarion.Scheduling.EntityFrameworkCore` — cross-instance scheduler coordination over EF/Postgres: per-occurrence claim rows → exactly one node runs each recurring occurrence (`AddElarionSchedulerEntityFrameworkCore<T>`; `UseElarionSchedulerClaims`/`[GenerateElarionSchedulerClaims]`, ELSCH001). `[ScheduledJob(Placement=JobPlacement.EveryNode)]` opts out. Fail-closed (ADR-0025).
- `Elarion.FeatureFlags.OpenFeature` — OpenFeature-backed `IFeatureFlagService` default (`AddElarionOpenFeature`, bring your provider) + `ICurrentUser`→`EvaluationContext` mapping, no `HttpContext`.
- `Elarion.FeatureFlags.FeatureManagement` — batteries-included config-driven flags via the M.FeatureManagement OpenFeature provider (`AddElarionFeatureManagement(config)`), ADR-0016.
- `Elarion.Blobs` — provider-neutral, streaming-first blob contracts: `IBlobStore` (`SaveAsync`/`OpenReadAsync`→disposable `BlobDownload`/metadata/delete/exists), ergonomic `byte[]`/file helpers as extensions, the **upload lifecycle** (`BlobLifecycleState` Pending/Committed, `IBlobLifecycle` — `CommitAsync` inside the caller's tx, `DeleteExpiredPendingAsync` GC), and the **protocol-neutral staged (resumable) upload seam** (`IStagedUploadStore`: offset-guarded `AppendAsync`, explicit idempotent `CompleteAsync`→Pending blob, nullable `Length` for deferred-length/RUFH; expiry policy arrives as data, ADR-0035) with the in-memory default + the provider-neutral `StagedUploadGarbageCollector`/`BlobGarbageCollector`, and **prefix+delimiter listing** (`ListAsync`/`ListContainersAsync` → virtual-directory roll-up, opaque continuation tokens, `BlobMetadata.State`; a browse/ops surface, never real directories or an app-query path, ADR-0036). Stays S3-free.
- `Elarion.Blobs.PostgreSql` — Postgres blob storage via EF model config + Npgsql streaming I/O (`AddElarionPostgreSqlBlobStore<T>`; `UseElarionBlobStorage`/`[GenerateElarionBlobStorage]`, ELBLB001; streaming reads **clone the context's connection** — no `NpgsqlDataSource` registration or connection string, ADR-0041). Implements `IBlobLifecycle` (partial index over pending rows) and the durable `IStagedUploadStore` (bytea staging, conditional-append offset guard; `AddElarionPostgreSqlStagedUploads<T>`; `UseElarionStagedUploads`/`[GenerateElarionStagedUploads]`, ELBLB002).
- `Elarion.Blobs.AspNetCore` — direct blob-transfer endpoints: upload (`MapElarionBlobUploads`: POST bytes→Pending `BlobRef`, DELETE→owner cancel; content-type allow-list + size cap, fits FilePond/`fetch`) and the owner-scoped streaming download (`MapElarionBlobDownloads`: GET /{blobId}, exact-owner fail-closed → 404; the staged-blob export leg, ADR-0039).
- `Elarion.Blobs.Tus` — **tus 1.0** resumable upload transport (`MapElarionResumableBlobUploads`); the recommended transport (Uppy/`tus-js-client`). A pure protocol adapter over `IStagedUploadStore` (all policy in `ResumableBlobUploadOptions`, handed to the store as instants); completed upload → Pending blob, ref in `Elarion-Blob-Ref`.
- `Elarion.Blobs.Azure` — Azure Blob Storage backend: `AzureBlobStore` (`IBlobStore`+`IBlobLifecycle` via blob metadata, ETag-guarded commit/GC) and the native `AzureStagedUploadStore` (one append blob per session, server-side `If-Append-Position-Equal` offset guard, completion = server-side copy; `AddElarionAzureBlobStore`/`AddElarionAzureStagedUploads`). Zero protocol knowledge — the ADR-0035 seam-validation exercise. Not `IsAotCompatible`-flagged.
- `Elarion.ClientEvents` — client events (ADR-0043): after-commit facts projected to browsers as **at-most-once hints** (light payloads — ids/refs; client converges by re-query). Contracts in Abstractions (`IClientEvent` marker, `IClientEventPublisher.PublishAsync(evt, scope)`, `ClientEventScope` Global/User/Resource, fail-closed `IClientEventSubscriptionAuthorizer`); this package owns the topic catalog (`AddElarionClientEvents(e => e.AddTopic<T>("module.event", t => t.RequirePermission(…)))` — opt-in by enumeration, unregistered publish fails loud; under `[UseElarion]`/`[GenerateClientEventTopics]` the registration is **generated per module**: topic inferred `{module}.{name}` (trailing `Event` stripped, `[ClientEvent("…")]` overrides), contract-level `[RequirePermission]`/`[RequireRole]` become subscribe-time requirements, ELCEV001–ELCEV003), the canonical-JSON publisher, and the in-process registry behind the replaceable `IClientEventBroadcaster`/`IClientEventLocalDelivery` seams. Recommended producer: method-form `[ConsumeEvent]` projection (post-commit for free); direct in-handler publish = the ephemeral progress tier. `IsAotCompatible`.
- `Elarion.ClientEvents.PostgreSql` — cross-node client-event fan-out over `LISTEN/NOTIFY` (`AddElarionPostgreSqlClientEvents`, the ADR-0024 listener pattern): every publish = `pg_notify`, every node (publisher included — one delivery path) delivers to its own browsers via a dedicated LISTEN connection; after a reconnect it pushes `elarion.connected` (`ClientEventControlEvents`) to all local subscribers via `IClientEventLocalDelivery.DeliverToAll` so clients re-query; >8 KB NOTIFY payloads fail loud at publish. ~1–10 nodes; past that replace `IClientEventBroadcaster` (ADR-0025). Not `IsAotCompatible`.
- `Elarion.ClientEvents.AspNetCore` — the SSE transport over the native `TypedResults.ServerSentEvents`: `MapElarionClientEvents(route)` (GET, `subscriptions` query param = JSON array of `{topic, resource?}`), fail-closed subscribe-time auth (401 unauthenticated; unknown topic / failed topic requirement / resource scope without passing authorizer → 404, never leaking existence), user scope always the caller's own; control signals are named events (`elarion.connected` on open = re-query hint, `elarion.keepAlive` on 15 s idle).
- `Elarion.JsonRpc` — JSON-RPC + MCP **protocol adapters** over the shared `HandlerDispatcher` (`JsonRpcDispatcher`/`McpDispatcher`, filtered by `HandlerTransports`): envelopes, telemetry, schema export, `Result`→`RpcError` (`AppErrorMapper`). ASP.NET-free. The bus + dispatch-scope rail live in Abstractions.
- `Elarion.AspNetCore` — ASP.NET JSON-RPC endpoint mapping, batch, current-user middleware, `[HttpEndpoint]` minimal-API mapping (`HttpAppErrorMapper`, `ElarionHttpResults` — incl. `ToFileResult` for `Result<ElarionFile>` downloads, ADR-0039), `[ModuleEndpoints("Name")]` (endpoint hooks for a module declared outside its assembly — host or web companion, called inside the module's feature gate; ELMOD004/ELMOD005, ADR-0040), and `AddElarionHttpJson()` (mirrors canonical JSON onto `Http.Json.JsonOptions` — needed under reflection-off).
- `Elarion.AspNetCore.Identity` — optional ASP.NET Identity **host wiring** (`AddElarionIdentity<TUser,TRole,TKey,TDbContext>` over the app's plain `DbContext`); maps `ICurrentUser` to Identity claims. Model lives in the EF-only sibling below.
- `Elarion.EntityFrameworkCore.Identity` — **web-free Identity model** (`[GenerateElarionIdentity<…>]`/`ApplyElarionIdentity`, ELIDN001); emits the seven Identity DbSets + snake_case model with no `FrameworkReference` — a data layer composes Identity without the ASP.NET shared framework.
- `Elarion.AspNetCore.Mcp` — MCP server over Streamable HTTP (`AddElarionMcp`/`MapElarionMcp`), the `McpDispatcher` filtered to `HandlerTransports.Mcp`. Only package referencing the `ModelContextProtocol` SDK.
- `Elarion.AspNetCore.OpenApi` — opt-in OpenAPI for `[HttpEndpoint]` over `Microsoft.AspNetCore.OpenApi` (`AddElarionOpenApi`/`app.MapOpenApi()`). Owns only what Microsoft can't: canonical-JSON wiring, clean operationIds, and the `Idempotency-Key`/`x-elarion-idempotent` transformer. Only package referencing `Microsoft.AspNetCore.OpenApi` (ADR-0026).
- `Elarion.AspNetCore.SchemaGeneration` — MSBuild package + host-launching tool that exports `rpc-schema.json` at build.
- `Elarion.EntityFrameworkCore` — EF marker attributes: `[EntityConfiguration]` (on an `IEntityTypeConfiguration<T>` — drives both the `DbSet<T>` and its `Configure`, no separate entity marker), `[GenerateDbSets]` (on the concrete partial `DbContext`), assembly `[UseElarionEntityFrameworkCore(Provider=…)]`. Runtime-dep-free; **bundles the EF generator** (ELEFC001).
- `Elarion.Paging` — keyset + offset pagination: `[Keyset<TEntity>]` (dedicated partial class), `IKeysetDefinition<T>`, cursor codec, `SortMap`/`SortMapBuilder`, `IQueryable` → `Page<T>`. Also the **data-level auth list filter** (Leg B, ADR-0013): `[ResourceFilter<TEntity>]` + `IQueryable.WhereAuthorized(spec, user)` → generated predicate applied *before* paging (ELRES001–005).
- `Elarion.Authorization.EntityFrameworkCore` — **grants backend** (resource sharing, ADR-0013): a generic `ResourceGrant` table keyed by `(resourceType, resourceId, principalKind, principalId, operation)`, `IResourceGrantStore`, grants-backed `IResourceAuthorizer` (`[RequireResource]` default), `IResourceGrantSource` (`AddElarionResourceAuthorization<T>`; `ApplyElarionResourceGrants`/`[GenerateElarionResourceGrants]`, ELRG001). Auth-provider-neutral (keys off `ICurrentUser` strings).
- `Elarion.Settings` — runtime key/value settings, swappable both sides: sink `ISettingsStore` (`SettingsScope` = open `(Kind,Owner)`, hierarchical `:` key, optimistic concurrency) + listen seam `ISettingsChangeSource`/`…Publisher` (`IChangeToken`); consumer `ISettingsManager` (typed via source-gen `JsonTypeInfo<T>`, per-user scope from `ICurrentUser`, fail-closed). Default in-process (`AddElarionSettings`), ADR-0011.
- `Elarion.Settings.EntityFrameworkCore` — EF settings store (`AddElarionSettingsEntityFrameworkCore<T>`; `UseElarionSettings`/`[GenerateElarionSettings]`, ELSET001). Change-tracker-free writes (`ExecuteUpdate`/raw `INSERT`); announces via `IEfCoreSettingsChangeNotifier` (default in-process, skips ambient-tx writes).
- `Elarion.Settings.Configuration` — consuming-side `IConfiguration` adapter over the `Global` scope with `IChangeToken` reload (`AddElarionSettingsConfiguration`); a `BackgroundService` loads once DI exists. Global scope only.
- `Elarion.Settings.PostgreSql` — **cross-instance settings changes** over `LISTEN/NOTIFY` (`AddElarionPostgreSqlSettingsChanges`, ADR-0024): one dedicated LISTEN connection/node; the EF store swaps in a `pg_notify` notifier so writes are commit-gated. Not `IsAotCompatible`.
- `Elarion.Messaging.InMemory` — best-effort in-memory integration (Plane B) bus, commit-gated by the DbContext tx (`AddElarionInMemoryIntegrationEventBus<T>` / `AddElarionInMemoryEventBus<T>`). Non-durable outbox sibling; the `<T>` overload auto-attaches its interceptors.
- `Elarion.Messaging.Outbox` — EF transactional outbox: durable `IIntegrationEventBus` recording each event as an `OutboxMessage` (atomic with business data) + hosted delivery loop (conditional `ExecuteUpdate` lease, at-least-once) (`AddElarionOutbox<T>`; `UseElarionOutbox`/`[GenerateElarionOutbox]`, ELOBX001; partial index over pending rows). `IOutboxStore` seam. Seeds each delivery scope with `OutboxMessage.Id` (`IEventContext.MessageId`, the inbox key) and wires `AddElarionIdempotency` for the default-on inbox (ADR-0022).
- `Elarion.EntityFrameworkCore.UnitOfWork` — framework EF transaction boundary: `EfUnitOfWork<T>` over the EF-free `IUnitOfWork` seam (PostgreSQL `SET LOCAL lock_timeout` + savepoints) (`AddElarionUnitOfWork<T>`). The framework `TransactionDecorator` (`AppliesTo` = `ICommand`/`IIntegrationEvent`, not `[Idempotent]`/inboxed consumers) composes it (ADR-0021).
- `Elarion.Idempotency.EntityFrameworkCore` — durable idempotency store: `INSERT … ON CONFLICT DO NOTHING` **inside the caller's tx** (composite-PK constraint is the cross-node fence, no external lock; `55P03` lock_timeout → 409) (`AddElarionIdempotencyEntityFrameworkCore<T>`; `ApplyElarionIdempotencyKeys`/`[GenerateElarionIdempotencyKeys]`, ELIDEMEF001). `[Idempotent]` + seams + in-memory default live in Abstractions/`Elarion` (ADR-0021). Also backs the **inbox** rows (`IdempotencyScope.Consumer`, ADR-0022).
- `Elarion.Generators` — Roslyn generators (handlers, services, modules, RPC/HTTP/MCP maps via `AppModuleDiscoveryGenerator`, validation resolvers, resilience policies, scheduled jobs, event consumers, client-event topics, permission catalog, per-module `ConfigureDefaultServices`). `HandlerRegistrationGenerator` auto-attaches the authorization → feature-gate → validation decorators (see the model sections). Hosts `ModuleApiGenerator` + `ModuleBoundaryAnalyzer` (ELMOD002). **`IsPackable=false`** — bundled into `Elarion`.
- `Elarion.EntityFrameworkCore.Generators` — EF generators: `DbContextGenerator` (DbSets + `ConfigureEntities` + the `OnEntitiesConfigured` seam other feature generators implement; cross-assembly via `EntityConfigurationManifest`; `ConfigureEntities` ends with the **client-assigned Guid key pass** — single-property Guid PKs of the discovered entities' assemblies → `ValueGeneratedNever`, navigation children covered, explicit config/store defaults/value generators win, ADR-0038), the `[Keyset]` emitter (provider-aware row-value seek under Npgsql), and `[ResourceFilter]` (ELKEY005). **`IsPackable=false`** — bundled into `Elarion.EntityFrameworkCore`.
- `@swimmesberger/elarion-jsonrpc-client-generator` — TS CLI/library: schema → method contracts, constraint-aware Zod params/result schemas, a params-pre-validating fetch client (`validateParams:false` opts out), native `File` mapping for `x-elarion-file` payloads (params take a `File`, results materialize one; base64 conversion at the call boundary, file-free schemas byte-identical), and the typed client-event subscription client from the schema's `events` block (ADR-0043: `events-client.ts` — topic-typed `createElarionEvents` over one `EventSource`, Zod-validated payloads, `$client.onConnected` = re-query hint; event-free schemas byte-identical). Zod v3+v4. `src/elarion-jsonrpc-client-generator`.
- `@swimmesberger/elarion-contributions` — the **frontend contribution model** (ADR-0032 + its 2026-07 addendum): typed extension-point tokens (`defineExtensionPoint<TItem,TContext>` — the frontend `[ModuleContract]`; `ItemOf`/`ContextOf` extract the declared types), declarative module manifests (`defineModule` + `contribute(point, items)`; manifest-level `when` ANDs into every item), the `when` evaluator (`{module?,permission?,flag?,role?}`, AND, fail-closed — **UX projection, never security**; axes typed **strictly** against the app vocabulary: typo = compile error, omitted/`never` axis rejects every use), the deterministic `createContributionRegistry` (generic over the vocabulary; **throws on duplicate co-visible ids per point** — ids double as render keys, prefix `{module}.{item}`), `createContributionKit<Vocabulary>()` (axes optional — a no-auth app binds `{module}` only), and `createStaticCapabilities` (the no-snapshot `CapabilityReader`: modules/permissions/roles default `"all"`, flags none). Zero-dep core; `/react` bindings (`ContributionProvider`/`useContributions`/`<ExtensionSlot>` — its `context` prop is checked against the point's `TContext` and handed to the render prop; React optional peer); `/angular` bindings (`provideContributions`/`injectContributions`→`Signal`, `@angular/core` optional peer — decorator/template-free so it ships in the one package with no ng-packagr; app renders with `@for` or a self-owned `*extensionSlot` directive over `injectContributions`); `/tanstack-router` ships one helper — `redirectUnless(when, to)`/`createRouteGuards<V>()` — and no other routing machinery. Point payload shapes are app-owned (no framework `NavItem` — rejected as not framework-worthy; the modular-sidebar/shadcn recipe is in [frontend-modules](docs/concepts/frontend-modules.mdx)). Recommended composition (post-#71/#72): **manifests discovered** via `import.meta.glob("./modules/*/index.ts", {eager:true, import:"default"})`, **routes registered statically** (`routes: readonly AnyRoute[]` per module, one typed `addChildren` line — a glob-composed tree degrades `Link to` *and* `useLoaderData`/`useParams`; glob-routes + `AnyRouter` is the documented alternative). Vite adopters need `resolve.dedupe: ["react","react-dom"]` + `optimizeDeps.include` (documented; no package-side lever). TanStack Start (SSR) shim + no-auth recipe live in the concept doc. `src/elarion-contributions`.

## Architecture boundaries

- Core packages stay reusable and domain-neutral — no consuming-app names, domain logic, or app deps.
- `Elarion.Abstractions` must not depend on runtime-integration packages, and holds **contracts only** — interfaces, attributes, markers, and data records. Concrete behavior (the pipeline decorators, `HandlerTelemetry`, default impls) lives in `Elarion` core or an opt-in sibling; core is granted `InternalsVisibleTo` as the reference implementation (ADR-0034).
- `Elarion` core is **dependency-light** (only Abstractions + `M.E.*` *Abstractions*) and **transport-agnostic** (no protocol/host/ASP.NET/EF package, not even `Elarion.JsonRpc`). A heavy provider default lives in its own opt-in sibling; the seam stays in Abstractions, the pipeline **decorator lives in `Elarion` core** (ADR-0017/0034). Do not reintroduce a concrete third-party runtime dep into core/Abstractions.
- Two layers sit above core: **protocol** packages (`Elarion.JsonRpc` — format + dispatcher, no wire) and **host** packages (`Elarion.AspNetCore*` — bind a protocol to a wire). Hosts depend on protocols, not vice versa.
- Event bus split stays: Abstractions owns the messaging contracts + plane split; `Elarion` owns only the in-memory impl. An alternative backend implements `IIntegrationEventBus` (the only broker-portable plane).
- `Elarion.Blobs` stays provider-neutral and S3-free (lifecycle + staging seam in core; upload protocols are HTTP-layer adapters over `IStagedUploadStore` — tus today, RUFH/tus-2.0 as a future adapter over the same seam). A provider ships blob store + staging store as a matched pair. The S3 wire protocol is a deliberate non-goal.
- Authorization, feature-flag gating, and request validation each follow the same **seam/impl split**: attribute + seam in Abstractions, the auto-attached **decorator in `Elarion` core** (no provider dep, ADR-0034); the heavy default in an opt-in package. Swapping a provider must never touch a `[FeatureGate]`/`[Require*]`/DTO. Validation uses standard `System.ComponentModel.DataAnnotations` — **no Elarion validation attribute, no FluentValidation dep anywhere** (app-owned decorator only). Business rules live in handlers, never a pre-handler validator (ADR-0027).
- `Elarion.AspNetCore.OpenApi` is the only package referencing `Microsoft.AspNetCore.OpenApi`; the base HTTP transport + generator stay OpenAPI-free. No Elarion OpenAPI MSBuild package, no bespoke HTTP client generator (ADR-0026).
- `Elarion.AspNetCore.Identity` owns only host wiring; the web-free model is in `Elarion.EntityFrameworkCore.Identity`. The app composes Identity via `[GenerateElarionIdentity]`, not `IdentityDbContext` inheritance. Auth providers only populate `ICurrentUser`.
- EF packages own only markers, pagination primitives, and generation. Keep `Elarion.EntityFrameworkCore` markers-only; EF-dependent pagination runtime is in `Elarion.Paging`; provider-neutral pagination contracts (`Page<T>`, requests) stay in Abstractions.
- **Seam contracts are designed for the strongest impl**, never vetoed by pre-1.0 compat or a single-node/test tier (a weaker tier implements the closest semantics + documents the delta). Prefer an optional `XyzOptions? options = null` over repeatedly widening signatures.
- Prefer compile-time generation over runtime reflection scanning; preserve trimming/AOT friendliness on framework paths.

## Source generator conventions

A generator runs on **every keystroke**, so incrementality is correctness. Full rationale + rejected
alternatives: [ADR-0006](docs/decisions/0006-incremental-source-generator-conventions.md). Copy a reference
generator (`AppModuleDiscoveryGenerator`, `ElarionManifestGenerator`, `ModuleDefaultServicesGenerator`, any
of the six registration generators), not the old "scan and emit" shape. When adding/changing any generator:

- **Discover through the syntax provider**, never off `CompilationProvider`: `ForAttributeWithMetadataName`
  for attribute triggers (branch on `ctx.TargetSymbol` for methods vs types), or a predicate-filtered
  `CreateSyntaxProvider` for structural triggers. Never `RegisterSourceOutput(CompilationProvider, …)` with a
  `foreach (SyntaxTrees)` scan.
- **Every pipeline value must be value-equatable.** `ImmutableArray<T>.Equals` is reference equality — use
  `EquatableArray<T>` for every collection field, nested all the way down. Carry **strings** (FQNs via
  `SymbolDisplayFormat.FullyQualifiedFormat`), never `ISymbol`/`Compilation`/`SyntaxNode`/`Location`/`object?[]`.
- **Diagnostics are data.** Transforms stay pure (no `spc.ReportDiagnostic`): return
  `EquatableArray<DiagnosticInfo>` (built with `DiagnosticInfo.Create` + `LocationInfo`) and report in the
  `RegisterSourceOutput` callback.
- **Reuse shared discovery.** `ModuleProviders.CollectModules` + `ModuleScanner.FindBest`/`IsInScope`; gate
  assembly opt-ins with `ModuleProviders.HasTrigger`. Do not hand-roll a `ModuleInfo`/module scan/namespace matcher.
- **Output is a byte-identical contract, and caching is tested.** Preserve every emit-time `OrderBy`/`Sort`
  (provider order is unspecified). Tag collect/combine nodes with `.WithTrackingName` and add a
  `GeneratorCacheAssert.ReusesOutputsAfterIrrelevantEdit` test — the only check that catches a re-introduced
  non-equatable model. Run the generator's `*GeneratorTests.cs` after each change.
- **AOT/trim + cross-assembly.** Emit concrete statically-typed code (no reflection/open generics in hot paths).
  For cross-assembly discovery, emit/read `[assembly: AssemblyMetadata]` via `MetadataReferencesProvider`
  (`ElarionManifest` + `EntityConfigurationManifest` are the examples; `ElarionManifestReader` is the reader to copy),
  never scan referenced symbol trees.

## Handler dispatch and transports

`[Handler("module.action")]` (name optional — inferred `{module}.{operation}`), `[HttpEndpoint(verb?, "route")]`,
and MCP are **three parallel first-class optional transports over one handler definition**. Request/response
are read from `IHandler<TRequest, Result<TResponse>>` (success unwrapped from `Result<T>`), so they may be
nested or top-level. `[GenerateModuleBootstrapper]` is the **single wiring path** (see [Module-aware transport
gating](#module-aware-transport-gating)); there is no flat, ungated map.

- **JSON-RPC + MCP** share the `[Handler]` identity and differ only by the `HandlerTransports` flag
  (`JsonRpc`/`Mcp`/`All`, default `All`). Both are **adapters over the shared `HandlerDispatcher`**
  (`JsonRpcDispatcher`/`McpDispatcher` in `Elarion.JsonRpc`), each serving the subset its flag selects — one
  registry, built once. `[McpHandler(ToolName=…)]` customizes only the tool name. No resolvable shape → `ELRPC002`.
- **HTTP/REST** is a separate opt-in (`[HttpEndpoint]`) because it needs route/verb/param binding. Verb precedence:
  explicit verb → CQRS marker (`ICommand`→POST, `IQuery`→GET) → else `ELHTTP004`. The generator emits concrete-typed
  minimal-API lambdas that **resolve the handler typed-directly** (route pins the type — HTTP never goes through the
  bus) and translate `Result<T>` via `ElarionHttpResults` (RFC 7807 on failure). Per-property binding
  (`[FromRoute]`/`IFormFile`/…) is the DTO's opt-in, detected structurally. No resolvable shape → `ELHTTP001`.
  **Files are two-tiered** (ADR-0039). Small (≲4 MB): `ElarionFile` in the request/response — HTTP maps
  `ToFileResult` (`TypedResults.Bytes`; octet-stream + `format: binary` OpenAPI via marker), JSON-RPC/MCP ride the
  fixed base64 envelope (`{contentType, fileName?, data}`, both directions — a request property is an upload; schema
  marks it `x-elarion-file`, the TS client maps it to a **native `File`**); composes with `[Cacheable]`/`[Idempotent]`
  (envelope replay). Large: **staged blobs** — upload via tus/direct endpoints → handler gets the pending `BlobRef` and
  streams from `IBlobStore`; exports = handler saves a pending owner-scoped blob and returns the ref, client streams it
  from `MapElarionBlobDownloads`; never-committed blobs expire via GC (temp-file semantics). `IFormFile` stays the
  HTTP-multipart escape hatch.
- **OpenAPI** is the REST contract (`Elarion.AspNetCore.OpenApi`, ADR-0026): thin over `Microsoft.AspNetCore.OpenApi`,
  owning only canonical-JSON wiring, clean operationIds, and the idempotency transformer. Client-gen is off-the-shelf
  (`openapi-typescript`/Kiota); build-time export reuses `Microsoft.Extensions.ApiDescription.Server`.
- Schema/client chain: `[Handler]` → `rpc-schema.json` (`Elarion.AspNetCore.SchemaGeneration`) → the TS client generator.
  Generated TS stays portable (browser + NodeNext), standard `fetch`, `AbortSignal`.

**In-process, always call handlers typed-directly** — inject `IHandler<TRequest, Result<TResponse>>`, inject
`IHandlerSender` + `SendAsync<,>` (typed mediator, resolves from the ambient scope; `AddElarionHandlerSender`), use
`HandlerInvoker.InvokeAsync<,>` (fresh scope), or the typed `[GenerateModuleApi]` facade across modules — so a rename
is a compile error. The named bus is a **transport seam** (routes a wire *string* to a handler): it's injectable and
`DispatchAsync(name, …)` works, but it takes `object`/returns `Result<object>`, so use it only when you must dispatch
by a dynamic name. The event bus is pub/sub-only (no request/reply).

## JSON serialization model

Every subsystem reads **one canonical `JsonSerializerOptions`** via `IElarionJsonSerialization` (ADR-0023).

- `ElarionJsonOptions` (`Elarion.Abstractions.Serialization`) is the composed config: naming knobs, an **ordered**
  `TypeInfoResolvers` list, `EnableReflectionFallback`, `PostConfigure`. Plain mutable bag — **no `M.E.Options` dep**.
- `IElarionJsonSerialization` materializes + **freezes** (`MakeReadOnly()`) one `JsonSerializerOptions` on first access;
  exposes `Options`/`GetTypeInfo<T>()`. Subsystems depend on this accessor, **never a bare `JsonSerializerOptions` in
  DI** (so Elarion never collides with a host's own registration).
- Composition is a **contributor seam**: `AddElarionJson()` registers the accessor (idempotent); `ConfigureElarionJson(o
  => …)` accumulates. Transports insert their envelope context first (`Insert(0, …)`); generated `AddElarion` adds module
  contexts; host adds extras. Resolver order is **first-match-wins**; `OverrideTypeInfoResolvers` composes ahead of all.
- **AOT-strict by default** (matches `JsonSerializerIsReflectionEnabledByDefault=false`): a type missing from every
  source-gen context throws rather than reflecting, unless `EnableReflectionFallback` is set (isolated in a suppressed
  helper so core stays `IsAotCompatible`).

## Authorization model

Declarative, transport-neutral, provider-independent — runs in the handler pipeline, same under every transport
([ADR-0009](docs/decisions/0009-authorization-building-blocks.md), [concept](docs/concepts/authorization.mdx)).

- Class-level attributes: `[RequirePermission(resource, verb)]` (K8s-RBAC; sugar for a `{resource}.{verb}` claim of
  `PermissionClaimType`, open `Verbs` vocabulary), `[RequireRole]`, `[RequireClaim(type, values…)]` (values OR; empty =
  presence), `[RequirePolicy("name")]`, `[AllowAnonymous]`. Different kinds AND; multiple of one kind AND; OR only inside
  one `[RequireClaim]`.
- `HandlerRegistrationGenerator` auto-attaches `AuthorizationDecorator` as the outermost functional gate (just inside
  tracing) when a handler carries a `Require*`/`[RequirePolicy]` attribute; assembly/`[AppModule]`-scoped
  `[ElarionAuthorizationDefaults]` flips to deny-by-default (most-specific-wins). Reads requirements via `HandlerMetadata`
  (never `inner.GetType()`); guarded by `IResultFailureFactory<TResponse>` else `ELAUTH001`.
- Default `ClaimsAuthorizer` (`AddElarionAuthorization`) evaluates against `ICurrentUser` + `IAuthorizationPolicy`
  instances, no ASP.NET. Unauthenticated → `AppError.Unauthorized` (401); denied → `Forbidden` (403). Named policy =
  `[AuthorizationPolicy("name")]` (auto-registered per module, `ELPOL001`/`ELPOL002`) or `AddElarionAuthorizationPolicy`.
- Authentication is a host concern that only populates `ICurrentUser` (Identity, or Entra/OIDC-JWT via
  `AddElarionCurrentUser`). Permission catalog: `PermissionCatalogGenerator` emits the compile-time `ElarionPermissions`
  static (root namespace; `All`/`Roles`/`ByModule`/`ByResource`/`ByVerb` + typed accessors) + the runtime
  `IPermissionCatalog` from `[RequirePermission]`/`[RequireRole]`. Triggers `[UseElarion]`/`[GeneratePermissionCatalog]`;
  guarded handler under no module → `ELPERM001`, colliding accessor → `ELPERM002`.

## Feature flag model

Gates a handler at run time; declarative, transport-neutral, provider-agnostic — distinct from compile-time module
gating ([ADR-0016](docs/decisions/0016-feature-flag-gating.md), [concept](docs/concepts/feature-flags.mdx)).

- `[FeatureGate("name")]` (class-level, `AllowMultiple`): one+ names, optional `FeatureRequirement` (`All` default/`Any`),
  optional `Negate`. Stacked attributes AND.
- Auto-attached `FeatureGateDecorator` sits **just inside the authorization gate** (so a disabled feature is never revealed
  to an unauthenticated caller). `ELFEAT001` (response can't represent failure), `ELFEAT002` (no name). Closed gate →
  `AppError.NotFound` with a **generic** message (echoing the name would leak what the 404 hides).
- Calls the boolean `IFeatureFlagService.IsEnabledAsync` seam (Abstractions, no provider dep); targeting is ambient from
  `ICurrentUser`. Default targets **OpenFeature** (`Elarion.FeatureFlags.OpenFeature`); batteries-included =
  `Elarion.FeatureFlags.FeatureManagement`. Replace the backend wholesale by registering a different `IFeatureFlagService`.
- **Variant injection** (ADR-0019): ships a different *implementation* per allocated variant. The consuming handler is
  transparent; only impls carry `[FeatureVariant("feature", Variant="x")]` (a **modifier on `[Service]`** — contract(s)
  come from `[Service]`, not repeated; `[FeatureVariant]` without `[Service]` → `ELVAR007`). Async selection + sync ctor →
  the generator wraps such handlers in the `AsyncResolvedHandler` proxy (warms into a per-scope `VariantResolutionCache`).
  `IFeatureVariantService.GetVariantAsync` accessor; imperative escape hatch `IVariantServiceProvider<T>` (`ELVAR001`,
  `ELVAR003`–`ELVAR007`). Requires an OpenFeature provider surfacing the variant name (the FeatureManagement default
  doesn't yet — boolean gating unaffected). Vary handler behavior by a variant **strategy service**, not by swapping the handler.
- **Configuration-selected variants** (ADR-0028): the process-global sibling — selection by *what is configured*, not
  *who asks*. `[ConfigurationVariant("Email:Backend", Value="office365")]` (also a `[Service]` modifier; one axis per
  contract, `ELVAR008`) picks by a plain `IConfiguration` value (case-insensitive, default fallback). **Synchronous** — no
  proxy/warm-up; handlers keep the plain registration; each new DI scope observes the current value. Manual:
  `AddElarionConfigurationVariantService<T>(key, defaultKey)` (blank key → `ELVAR009`). **Choose the axis by who decides**:
  differs between two concurrent requests → `[FeatureVariant]`; one answer per process → `[ConfigurationVariant]`; when in
  doubt start configuration-selected.
- **Variant registry** (ADR-0029): a named default (`Value` + `IsDefault=true`, both axes) is fallback *and* selectable
  (two defaults → `ELVAR001`). `VariantCatalogGenerator` (`[UseElarion]`/`[GenerateVariantCatalog]`) emits the
  `ElarionVariants` static (accessor classes with value `const string`s usable in `[AllowedValues]`, + `VariantDescriptor`
  data), cross-assembly via the manifest; accessor collision → `ELVAR010`. Variants under no module are **platform** variants
  (`Module=null`). Generator registers **nothing** — host seeds `AddElarionVariantCatalog(ElarionVariants.All)` + optionally
  `AddElarionVariantValidation()` (startup/reload check; `Strict` fails startup). **Switch-ownership happy path**: in-module
  strategies use their module's declarations; switchable adapters default to **port-owned vocabulary** (consts beside the
  port, referenced by adapter attributes + the admin DTO's `[AllowedValues]`). **Pinning** (`[FromKeyedServices(ElarionVariants.X.Y)]`,
  both axes) bypasses selection (handler discovery skips pinned params); pin via the vocabulary consts, never raw strings.

## Client capability bootstrap

One **read-only UX projection** so a frontend can hide/adapt UI — **never an enforcement boundary** (the handler's
`[RequirePermission]`/`[FeatureGate]` is still the real gate). ADR-0030/0032, [concept](docs/concepts/client-capabilities.mdx).

- `[ClientFeatures("a","b")]` on an `[AppModule]` declares the flag/variant names that module **exposes to the client**
  (opt-in by enumeration — an internal flag never reaches the wire; a disabled module exposes nothing). A listed name needs
  no `[FeatureGate]` behind it. Collected into `configuration.GetClientCapabilityManifest()`, cross-assembly via the manifest.
- `SessionHandler` (`Elarion.Session`) composes manifest + `IFeatureFlagService` + `IFeatureVariantService` + `ICurrentUser`
  → `SessionResponse { user, modules, flags, variants }` (flag/variant + auth deps optional). Wiring follows the imperative
  seam (ADR-0031): `AddElarionSession(manifest)` (self-registers `SessionJsonContext` into the canonical JSON), the bus
  `MapElarionSession()` (a `HandlerDispatcher.Map` over `"elarion.session"`, chained into the host's `RegisterHandlers`), and
  the concrete REST `MapElarionSession(route)` in `Elarion.AspNetCore`.
- **Typed vocabulary** (ADR-0032): the exported schema carries an optional `capabilities` block (enabled modules +
  `[ClientFeatures]`, structured `[RequirePermission]`/`[RequireRole]` catalog, roles; omitted → byte-identical) built by
  `JsonRpcSchemaExporter.Generate(…, JsonRpcSchemaExportOptions)`, auto-resolved from DI (`ClientCapabilityManifest` lives in
  `Elarion.Abstractions.Modules`; `IPermissionCatalog`). The TS generator turns it into typed constants + literal unions in
  `session-client.ts` (`string` fallback on older schemas) — the frontend `ElarionPermissions`. Non-goals: no
  micro-frontends/module federation, no C#-declared UI, no UI kit.
- The bus seam `HandlerDispatcher.Map` is the general way to expose a handler whose class the host does not own
  (framework-shipped/third-party/startup-decided); REST stays a concrete hand-authored `MapElarionX(route)` per handler
  (RDG/AOT-safe — no generic HTTP map), ADR-0031.

## Validation model

Two-tier; the line is **what a wire contract can express**, not "simple vs complex"
([ADR-0027](docs/decisions/0027-declarative-request-validation.md), [concept](docs/concepts/validation.mdx)).

- **Tier 1** = standard `System.ComponentModel.DataAnnotations` on the request DTO (`[Range]`, length attrs,
  `[RegularExpression]`, `[EmailAddress]`, `[Url]`, `[Base64String]`, `[AllowedValues]`), enforced at runtime **and**
  exported to every schema surface (JSON-RPC, MCP, OpenAPI, Zod). Requiredness = NRT + `required` (no `[Required]`). Reusable
  custom constraints subclass a mapped attribute.
- **Tier 2** = cross-field/conditional/async/DB checks in the **handler** (or a domain `[Service]`), returning
  `AppError.Validation`/`Conflict` through `Result<T>`, **inside the transaction** (a pre-handler async check is TOCTOU).
- Auto-attached `ValidationDecorator` sits just inside the feature gate (tracing → authorization → feature gate →
  **validation** → `[DefaultPipeline]` → handler), only when the request graph carries validation attributes (zero cost
  otherwise). `ELVAL001` (response can't represent failure), `ELVAL002` (warning: attributes present but `Elarion.Validation`
  not referenced — documented but unenforced).
- Seam `IRequestValidator` (`Elarion.Abstractions.Validation`) → `RequestValidationErrors?` with `FieldErrors` keyed by
  **wire-named field path** (canonical JSON naming, indexers preserved: `deliveries[1].street`). HTTP → RFC 7807 `errors`;
  JSON-RPC → `error.data`. Default impl `Elarion.Validation`; `ValidationResolverGenerator` emits per-module resolvers with
  **constant-constructed attribute arrays** (no runtime attribute reflection).

## Module-aware transport gating

All three transports become **module-scoped + feature-flag-gated** under `[assembly: GenerateModuleBootstrapper]`, which
emits the fixed-name `ElarionBootstrapper` static (framework-owned name, ADR-0018 — never declare a partial).
`AppModuleDiscoveryGenerator` matches each handler to a module by longest-prefix namespace and emits:

- per-module `Map{Module}Http`, `Add{Module}Handlers` (with transport flags), `Get{Module}McpMetadata`;
- aggregates `services.AddElarion(config)`, `endpoints.MapElarion(config)`, `dispatcher.RegisterHandlers(config)` (builds the
  one shared bus), `config.GetMcpMetadata()`, `GetAllJsonTypeInfoResolvers()`, `IsModuleEnabled(name)`.

Core modules map unconditionally; feature modules gate on `Modules:{Name}:Enabled` (a disabled module disappears across
services, validation, endpoints, RPC, MCP). A handler under no module is mapped ungated + warned (`ELHTTP003`/`ELRPC001`).
A "no modules" host still declares one core `[AppModule]`. Per-endpoint auth/conventions are the host's job (the module's
optional static `ConfigureEndpointGroup` hook + the per-module extension method on a configured group); the generator never
reads `[Authorize]`/`[AllowAnonymous]`. A web-free module assembly declares neither endpoint hook (`IEndpointRouteBuilder`
is shared-framework) — a `[ModuleEndpoints("Name")]` static class (host compilation or referenced manifest) supplies the
same hooks, called inside the module's gate after the module's own (contributors in type-name order, group hooks chained);
unknown module → `ELMOD004`, hook-less class → `ELMOD005` (ADR-0040). RPC/MCP gating needs `IConfiguration` at compose time — use the config-aware
overloads (`AddJsonRpc(serializerOptions, ElarionBootstrapper.RegisterHandlers)`, `AddElarionMcp(config.GetMcpMetadata(),
serializerOptions, RegisterHandlers, configure)` — same delegate to both, bus built once).

## Event / messaging model

Two planes, each its own interface in `Elarion.Abstractions.Messaging`, organized by **relationship to the transaction**
([ADR-0001](docs/decisions/0001-event-transaction-phase.md)). Marker interfaces bind each event to one plane; a type
carrying both is rejected. Pub/sub-only — a non-`Unit` `Result<T>` is request/reply and is rejected
(`ELEVT005` handler-form, `ELEVT002` method-form) — [ADR-0010](docs/decisions/0010-event-bus-is-pub-sub-only.md).

- **Plane A — domain (`IDomainEventBus`)**: dispatched **inline in the caller's scope/transaction** (consumers share the
  `DbContext`, commit atomically, a failure fails the command). Fans out in ascending `[ConsumeEvent(Order=…)]`. Never
  broker-portable. A domain consumer must not open its own transaction/resilience scope (the `TransactionDecorator`'s
  `AppliesTo` matches only `ICommand`/`IIntegrationEvent`, so it never attaches to a domain-event handler).
- **Plane B — integration (`IIntegrationEventBus`)**: recorded in the caller's unit of work, delivered **after commit** on a
  separate scope, retried independently (a failure never fails the command; a rollback discards). The **only broker-portable
  plane**. Backends: in-memory (`Elarion.Messaging.InMemory`, best-effort) and the durable EF outbox (`Elarion.Messaging.Outbox`).
- `[ConsumeEvent]`, two forms — **handler form preferred**: a class implementing `IHandler<TEvent, Result<Unit>>` (the sugar
  `IHandler<TEvent>`) whose request *is* the event, with the full decorator pipeline; a failed `Result` → `EventConsumerFailedException`.
  **Method form** = a lightweight side effect on a `[Service]` (no pipeline), returning `void`/`Task`/`ValueTask` or the
  non-generic `Result` (throw / failed `Result` to fail); optional `IEventContext`/`CancellationToken` params supplied by the runtime.
- **Inbox (ADR-0022)**: handler-form integration consumers are **deduped by default** — a `Consumer`-scoped reuse of the
  ADR-0021 `IdempotencyDecorator`, keyed `(consumer identity, IEventContext.MessageId)`, claimed in the consumer's own
  transaction (it replaces `TransactionDecorator` there), `WaitThenReplay` on races, soft-attached (no `IIdempotencyStore`
  registered → un-deduped, never a resolution failure). `[AllowDuplicates]` opts out (the consumer-side `[AllowAnonymous]`:
  declares redelivery harmless, plain transaction returns; ELINBX001 off-plane). Claims expire after a fixed 24 h (transport-
  scoped invariant, deliberately no per-consumer knob). Domain + method-form consumers are never inboxed; pass `MessageId`
  as a downstream idempotency key to close the foreign-side-effect window.
- `EventConsumerRegistrationGenerator` (`[GenerateEventConsumers]`/`[UseElarion]`) discovers consumers, validates signatures
  (`ELEVT001`/`ELEVT002`/`ELEVT005`), emits a per-module `Add{Module}EventConsumers` wired into `ConfigureDefaultServices`.
  Module-scoped only — a consumer under no module → `ELEVT003`. The scheduler is symmetric (`SchedulerRegistrationGenerator`,
  a job under no module → `ELSG010`). Both are module-feature-gated.

## Module default services

Under `[GenerateModuleBootstrapper]`, each module's discovered handlers/services/validators/jobs/consumers/actors are registered +
gated automatically via a cross-generator partial-method aggregation:

- `ModuleDefaultServicesGenerator` emits, per `[AppModule]`, a `{ModuleType}ElarionModuleServices.ConfigureDefaultServices`
  calling the `static partial void` hooks (`AddHandlers`/`AddServices`/`AddVariantServices`/`AddValidators`/`AddScheduledJobs`/`AddEventConsumers`/`AddAuthorizationPolicies`/`AddPermissions`/`AddModuleApi`/`AddActors`/`AddClientEvents`).
  Each category generator contributes a filler; unimplemented hooks elide to no-ops.
- `AddElarion` calls `{Module}.ConfigureDefaultServices(services)` gated by `IsModuleEnabled`, **before** the module's optional
  hand-written `ConfigureServices` (now reserved for non-generated registrations). For a referenced module predating the
  skeleton generator, the host probes for the public sibling.

## Cross-module communication

Direct synchronous module-to-module calls go through a **published contract**, not another module's internals or a direct
typed handler call ([ADR-0002](docs/decisions/0002-cross-module-communication.md)). Mapping between a contract's DTOs and a
module's handler DTOs is the module's concern (no generated forwarder, no mapper dep).

- `[ModuleContract]` marks a module's published cross-module surface (interface/class); the impl stays `internal`. Applies to
  **every** module incl. `Kind=Core` (no core exemption — the analyzer reads only the module name).
- `ModuleBoundaryAnalyzer` (`ELMOD002`, warning) is **location-based**: anything under an `[AppModule]` is module-internal and
  may only be referenced cross-module through a `[ModuleContract]`; anything outside every module is shareable. Inspects the
  dependency surface (ctor params, fields, properties). Resolve a flag one of three ways: a `[ModuleContract]` (a genuine,
  sparingly-used cross-module domain call); a platform-capability **port** outside the modules with its adapter in
  infrastructure (like `IEmailSender`); or move shared value types to the **shared kernel** (under no module). Track new
  analyzer diagnostics in `AnalyzerReleases.Unshipped.md` (RS2008).
- `[GenerateModuleApi]` (optional) generates a typed in-process API over a module's own handlers so intra-module code (a
  contract impl) can call them by name — **not a transport** (dispatches typed-direct through the full pipeline, absent from
  the schema; module-internal). Membership mirrors `[EntityConfiguration]`/`[GenerateDbSets]` scope but **opt-out** (every
  non-excluded handler is in the default facade); `[ModuleApi]` configures (`Exclude`, scope tags). `ELAPI001` (partial),
  `ELAPI002` (top-level), `ELAPI003` (not under a module, warning), `ELAPI004` (duplicate method).

## TypeScript client + frontend contributions

- **JSON-RPC client** (`src/elarion-jsonrpc-client-generator`): deterministic output, ergonomic direct API
  (`rpc.clients.get(params, options)`) + a generic transport primitive, tuple-aware batch typing. Runtime validation via
  generated Zod by default, **both directions** (`rpcResultSchemas` + `rpcParamsSchemas`; `validateParams:false` opts out).
  Constraint-aware emitter (schema keywords → Zod, Zod v3+v4). Type-checks under browser + NodeNext. No React/TanStack/Vite
  import from generated runtime code.
- **Frontend contributions** (`src/elarion-contributions`, ADR-0032): the review-isolation model for the frontend — see the
  package line above + [frontend-modules](docs/concepts/frontend-modules.mdx). `when` is a UX projection, never security; the
  point payload shape is app-owned; the shell + route composition + module discovery are app-owned; the framework ships the
  kernel + `/react` + `/angular` bindings + the one `/tanstack-router` guard.

## C# coding standards

Applies to `**/*.cs` (Copilot scopes this section via `.github/instructions/csharp.instructions.md`).

### Style
- Latest C# supported by the repo (currently C# 14).
- Classes `sealed` unless intentionally designed for inheritance (document the contract when extensible).
- Immutable records for DTOs/options/data containers; prefer nominal property-based records with `required` + `init`
  (nullable `init` for optional). Positional records only for tiny internal helpers/tests.
- Read-only collection types (`IReadOnlyList<T>`, `ImmutableArray<T>`, …) for immutable public surfaces.
- Primary constructors for DI services when they stay concise.
- Mint `Guid` ids with `Guid.CreateVersion7()`, not `Guid.NewGuid()` (entities own their ids — the model
  declares Guid PKs `ValueGeneratedNever`, ADR-0038; v7 is time-ordered/index-friendly). Use v4 only where the
  id must be unpredictable. Documented convention, not build-enforced.

### Naming
- PascalCase for types/methods/public members; `_camelCase` private instance fields; `camelCase` locals/params;
  PascalCase static readonly + consts. `I`-prefix interfaces, `T`-prefix type params.

### Formatting
- `.editorconfig` is the source of truth. File-scoped namespaces, single-line usings, opening braces on the same line.
- Early returns over deep nesting; pattern matching / switch expressions / `nameof` where they clarify; final `return` on its own line.

### Comments and public API docs
- XML docs on public APIs (`<example>`/`<code>` where non-obvious). Regular comments only for intent/constraints/non-obvious
  tradeoffs — do not restate obvious code. Document a non-obvious compat/source-gen/AOT/perf pattern with its *why*.

### Nullable reference types
- Non-nullable by default; validate nullability at entry points. `is null`/`is not null` (not `==`/`!=`). Trust the type
  system — no redundant null checks.

### Async and background work
- No unobserved fire-and-forget. Thread `CancellationToken` through async flows (no `CancellationToken.None` without a
  documented reason). Long-lived/background work is owned by a host-managed abstraction (`IHostedService`, scheduler, explicit
  queue/loop), not hidden in a helper. Handle `OperationCanceledException` deliberately — do not log expected cancellation as error.

### Telemetry
- **Follow the OTel semantic conventions wherever one exists.** Duration histograms record "seconds as a floating
  point number with the highest precision available" (unit `s`) — never milliseconds — with the semconv bucket
  boundaries supplied via `InstrumentAdvice<double>` (the SDK's default buckets are ms-scaled and useless for
  second-valued histograms). Telemetry `Record*` helpers take `TimeSpan`, so the unit decision lives in one place
  per meter class. When semconv defines a name (e.g. `rpc.server.call.duration`), adopt it instead of minting a
  parallel one; custom names/attributes use a namespaced prefix (`elarion.*`). Metric tags stay **bounded**
  (type/operation/outcome names — never keys, payloads, or user identity); high-cardinality identity (actor key,
  user id) goes on **spans only**. Explicitly unit-suffixed span tags (`*_ms`) are exempt from the seconds rule —
  they are self-describing.

## Testing

- Add regression tests when fixing bugs. Follow nearby naming/capitalization; no `Arrange`/`Act`/`Assert` comments.
- Keep tests deterministic (no timing-sensitivity unless the test is specifically about concurrency/scheduling).
- Source-generator changes: add/update generator tests, keep emitted output deterministic + inspectable.
- Test database behavior against **real PostgreSQL via Testcontainers**, never the EF Core **InMemory** provider: InMemory
  silently diverges (e.g. it skips `SaveChanges`' affected-rows check, so a zero-row `UPDATE` passes there but throws on
  Postgres — the ADR-0038 client-assigned-key trap), so a green InMemory test is false confidence. Use a Docker-gated
  fixture (e.g. `PostgreSqlBlobStoreFixture`): spin up a real container when Docker is available, **skip** (never fail) when
  not, so `dotnet test` stays green Docker-free. Tag `[Trait("Category", "Integration")]`. (`Microsoft.EntityFrameworkCore.InMemory`
  is deliberately absent from `Directory.Packages.props` — do not add it.)

## Development and validation

```bash
dotnet restore Elarion.slnx
dotnet build Elarion.slnx --configuration Release
dotnet test --project tests/Elarion.Tests/Elarion.Tests.csproj --configuration Release
dotnet pack Elarion.slnx --configuration Release --no-build
dotnet run --project tests/Elarion.Benchmarks -c Release    # hot-path microbenchmarks (gate for ADR-0042 optimizations)

cd src/elarion-jsonrpc-client-generator
npm ci && npm run build && npm test && npm pack --dry-run
```

When changing the TS generator, also generate from a representative `rpc-schema.json` and type-check the emitted
`rpc-types.ts`/`rpc-schemas.ts`/`rpc-client.ts` under `moduleResolution: NodeNext`.

## Documentation website

Marketing + rendered docs live in `website/` (Next.js + [Fumadocs](https://fumadocs.dev), static export to GitHub Pages).
Content is **not** duplicated there — `website/source.config.ts` points Fumadocs at the top-level `docs/` (the single source
of truth). Static assets live in `docs/public/` (mirrored into `website/public/` by a sync script; only `website/public/CNAME`
is committed) — put new images in `docs/public/`, never `website/public/`.

```bash
cd website && npm install && npm run build   # static export to website/out (npm run dev for localhost:3000)
```

Served at `elarion.wimmesberger.dev` (no base path; `website/public/CNAME`). When adding a doc page, drop the `.mdx` under
`docs/` and list it in the relevant `meta.json`; register any Fumadocs MDX component beyond `Card`/`Cards`/`Callout`/`Step(s)`
in `website/components/mdx.tsx`; `icon:` frontmatter must be a valid Lucide name. Pushes to `main` touching `website/**` or
`docs/**` trigger `deploy-docs.yml`.

## Pull requests

Prefer **stacked PRs** for any change large enough to be hard to review: an ordered chain where each PR targets the branch
below it and only the bottom targets `main`, merged bottom-up ([guide](https://github.github.com/gh-stack/)). Keep each layer
small, single-purpose, and green.

**Guardrails against stranding a stacked PR** (a real incident — a PR merged into an orphaned base that never reached `main`,
shown as MERGED but its content absent):
- **One branch per PR** — never reuse a head branch across PRs (it defeats GitHub's auto-retarget/auto-delete).
- **"MERGED" ≠ "on `main`."** Before treating a stacked PR as done, verify the content landed:
  `git merge-base --is-ancestor <pr-head-sha> origin/main`, or confirm the base chain terminates at `main`.
- **After a base PR merges, confirm each dependent PR retargeted** to `main`/the next unmerged base; retarget by hand if not.
- **Do not delete or reuse a base branch** while any open PR still targets it.

## Publishing

Trusted publishing / OIDC. `<VersionPrefix>` in `Directory.Build.props` is the single source of truth for the *next* version
([RELEASING.md](RELEASING.md)). NuGet via `NuGet/login` + the `NUGET_USER` var/secret; npm via trusted publishers. Pushes to
`main` publish preview packages (`{VersionPrefix}-preview.{run}.{attempt}`). The **Release** workflow promotes the current
`VersionPrefix` to a stable release (syncs doc versions, rolls the changelog, tags `v<version>`, bumps to the next patch,
creates the GitHub Release → fires `publish.yml`); it runs as a GitHub App to bypass branch protection + trigger publish.
Keep workflow changes tokenless unless a registry requires otherwise (the release-identity App is the deliberate exception).

**GitHub Actions:** when adding/editing a workflow, look up the **latest** version of every `uses:` action first and pin to
it (remembered version numbers go stale and trigger runner deprecation warnings).
