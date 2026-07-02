# Changelog

All notable changes to Elarion are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html). While Elarion is pre-1.0,
minor releases may include breaking changes.

## [Unreleased]

### Added
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
- **Breaking:** `ConfigPlaceholder` (`Elarion.Abstractions.Scheduling`) is removed in favor of the general
  `VariableSubstitution`. The common surface — `ScheduledJobSchedule.Resolve(IConfiguration)` — is unchanged;
  only direct callers of the low-level helper need to switch (same `${key:-default}` semantics).

### Documentation
- Tutorial `features.mdx` now shows the no-reflection `IResultFailureFactory<TResponse>` /
  `TResponse.Failure(...)` pattern for validation decorators instead of a reflection-based helper.
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

[Unreleased]: https://github.com/swimmesberger/Elarion/compare/v0.2.2...HEAD
[0.2.2]: https://github.com/swimmesberger/Elarion/releases/tag/v0.2.2
[0.2.0]: https://github.com/swimmesberger/Elarion/releases/tag/v0.2.0
[0.1.0]: https://github.com/swimmesberger/Elarion/releases/tag/v0.1.0
