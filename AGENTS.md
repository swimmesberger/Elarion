# AGENTS.md — Elarion

Canonical agent and contributor guidance for this repository. Tool-specific entry
points point here and must not duplicate content:

- `CLAUDE.md` imports this file (`@AGENTS.md`).
- `.github/copilot-instructions.md` points here for repo-wide Copilot guidance.
- `.github/instructions/csharp.instructions.md` scopes the **C# coding standards**
  section below to `**/*.cs` for Copilot.

Add or change guidance **here**, not in the pointer files.

Elarion is a reusable .NET application framework. Keep it independent from all
downstream applications: do not mention, depend on, or optimize for any consuming
app by name. Application-specific domain code, database conventions, UI frameworks,
and deployment quirks belong in consuming repositories, not here.

**Scale positioning:** Elarion targets small-to-mid applications — roughly **1–10 nodes, vertical-first, on
the one PostgreSQL the app already runs**. Shipped defaults (scheduler occurrence claims, outbox leases,
`LISTEN/NOTIFY` settings changes, the `UNLOGGED` L2 cache) must work at that tier and need not scale beyond
it; a concern that only appears past ~10 nodes is resolved by **replacing the seam**
(`IScheduledOccurrenceCoordinator`, `ISettingsChangeSource`, `IIntegrationEventBus`, `IBlobStore`,
`IHandlerCache`, …) — e.g. with a dedicated job engine or broker — never by growing the default's
complexity or configuration surface. Apply this as the design test for new defaults: "does it cover 10
nodes on one Postgres?" (see ADR-0025's positioning section).

## Package layout

- `Elarion.Abstractions` — implementation-neutral attributes, handler contracts (`IHandler<TRequest, TResponse>` plus the `IHandler<T>` no-content convenience that inherits `IHandler<T, Result<Unit>>` via a default interface method bridging the non-generic `Result` — so the handler generator and decorators stay unchanged — plus optional CQRS request markers `IRequest`/`ICommand`/`IQuery` that drive HTTP verb inference, decorator generic constraints, and runtime branching), result types (`Result<T>`, the non-generic `Result`, and the `Unit` no-value payload — `Unit` lives in the dedicated `Elarion.Abstractions.Results` namespace, not the root namespace handlers import, so it never collides with a domain `Unit` type), pagination contracts (`Page<T>`, keyset/offset page requests), module metadata, scheduling contracts, the reusable variable-substitution building block (`IVariableSource`, the change-observable `IObservableVariableSource`, `VariableSubstitution`, `ConfigurationVariableSource` — Spring-style `${key:-default}` placeholders, whole-value and embedded, decoupled from any single subsystem; the scheduler consumes it and live-reschedules recurring jobs when a watched variable changes; see [the variable substitution concept doc](docs/concepts/variable-substitution.mdx)), messaging contracts (`IDomainEvent`/`IIntegrationEvent` markers, `IDomainEventBus`/`IIntegrationEventBus`, `IEventContext`, `[ConsumeEvent]`, `EventConsumerFailedException`, `EventSubscriptionDescriptor`), cross-module communication markers (`[ModuleContract]`, `[ModuleApi]`, `[GenerateModuleApi]`), the transport-neutral authorization building blocks (the `[RequireClaim]`/`[RequirePermission]`/`[RequireRole]`/`[RequirePolicy]`/`[AllowAnonymous]` attributes, the assembly/`[AppModule]`-scoped `[ElarionAuthorizationDefaults]` deny-by-default opt-in, the async `IAuthorizer` seam, the `IAuthorizationPolicy` named-policy seam, `AuthorizationOptions`, the `AuthorizationDecorator`, the Kubernetes-RBAC `[RequirePermission(resource, verb)]` shape (with the open `Verbs` vocabulary), and the generator-populated permission catalog (`IPermissionCatalog`/`PermissionCatalogModule`/`PermissionCatalogEntry`, plus the `[GeneratePermissionCatalog]` trigger) of declared `[RequirePermission]`/`[RequireRole]`; `AppError.Unauthorized` (401) joins `Forbidden` (403), and `ICurrentUser` exposes claim access via `HasClaim`/`GetClaimValues`), the transport-neutral feature-flag building blocks (the `[FeatureGate]` attribute, the `FeatureRequirement` `All`/`Any` enum, the boolean `IFeatureFlagService` seam, and the `FeatureGateDecorator` — gated handlers short-circuit with `AppError.NotFound`; see [Feature flag model](#feature-flag-model)), the variant-service building blocks (the sibling `IFeatureVariantService` accessor, the `[FeatureVariant]` modifier on `[Service]` — contract(s) inferred from the `[Service]`, not repeated — `IVariantServiceProvider<T>`, `VariantResolutionCache`, and the general `AsyncResolvedHandler` pipeline proxy — transparent per-variant implementation injection, ADR-0019), the **per-call dispatch-scope rail** (`DispatchScopeContext`, `IDispatchScopeInitializer`, and the `IServiceProvider.CreateDispatchScope`/`SeedScope` helpers in `Elarion.Abstractions.Dispatch`) that transports use to seed scoped state — e.g. the current user — into each handler call, the `IAppErrorTranslator<TError>` seam for mapping `AppError` to a transport's wire error, the **transport-neutral named handler bus** (`HandlerDispatcher`/`HandlerRoute` in `Elarion.Abstractions.Dispatch` — a name→handler registry that every name-routed transport adapts, owning routing + invocation but no serialization) plus the handler-exposure attributes that drive it (`[Handler]` with an optional, convention-inferred operation name, the `HandlerTransports` flag, and `[McpHandler]`), the canonical JSON-serialization building blocks (`ElarionJsonOptions`, the `IElarionJsonSerialization` accessor and its default impl, and the `AddElarionJson`/`ConfigureElarionJson` registration in `Elarion.Abstractions.Serialization` — the one framework-wide `JsonSerializerOptions` every subsystem reads, composed via a plain contributor seam with no `Microsoft.Extensions.Options` dependency, AOT-strict by default; it lives in Abstractions so the leaf subsystem packages that reference only Abstractions can register and read it; see [JSON serialization model](#json-serialization-model) and [ADR-0023](docs/decisions/0023-canonical-json-serialization.md)), and source-generation triggers. See [Authorization model](#authorization-model) and [Feature flag model](#feature-flag-model).
- `Elarion` — runtime primitives for handler metadata caches, decorators, modules, the dependency-light resilience **policy catalog** (`AddElarionResiliencePolicyCatalog`/`AddElarionResiliencePolicyMetadata` — the Polly-backed pipeline *runner* now lives in the opt-in `Elarion.Resilience`, and the HybridCache-backed `IHandlerCache` default in `Elarion.Caching`), current-user access, the default transport-neutral authorizer (`ClaimsAuthorizer` + `AddElarionAuthorization` + the named-policy registry `AddElarionAuthorizationPolicy` + the aggregated `IPermissionCatalog` of `[RequirePermission]`/`[RequireRole]` strings), the in-memory scheduler (with the cross-instance occurrence-claim seam `IScheduledOccurrenceCoordinator` from Abstractions — the local always-claims default lives here; the EF Core/PostgreSQL coordinator in `Elarion.Scheduling.EntityFrameworkCore` replaces it so recurring jobs fire once per occurrence cluster-wide, ADR-0025), the reusable variable-substitution registration (`AddElarionVariableSubstitution` → an `IConfiguration`-backed `IVariableSource`), and the in-memory **domain** event bus (Plane A inline dispatch, `AddElarionDomainEventBus`) plus the shared `EventSubscriptionRegistry`/`EventContext`. EF-agnostic: the in-memory integration tier lives in `Elarion.Messaging.InMemory`. The published NuGet package **bundles the `Elarion.Generators` analyzer** (`analyzers/dotnet/cs/`), so referencing `Elarion` directly wires the source generator — there is no standalone generator package. Because NuGet analyzer assets are not transitive, this is the single home for the generator: application libraries and hosts both reference `Elarion` directly. The core is **transport-agnostic** and **dependency-light** — it references no transport package (not even `Elarion.JsonRpc`) and pulls only `Elarion.Abstractions` plus the `Microsoft.Extensions.*` *Abstractions* packages; provider-backed defaults that carry a heavy runtime dependency (caching, resilience, feature flags) are opt-in sibling packages (see [ADR-0017](docs/decisions/0017-dependency-light-core.md)). The off-HTTP claims-based `ICurrentUser` (`AddElarionClaimsCurrentUser` / `ClaimsPrincipalCurrentUser`) and the typed-direct `HandlerInvoker` live here.
- `Elarion.Caching` — the HybridCache-backed `IHandlerCache` default for handler result caching (`HybridHandlerCache` + `AddElarionHandlerCaching`, namespace `Elarion.Caching`). Opt-in: referencing this package is what pulls `Microsoft.Extensions.Caching.Hybrid`, keeping core dependency-light. The `IHandlerCache` seam and the `[Cacheable]`/`[CacheInvalidate]` decorators stay in `Elarion.Abstractions` (see [ADR-0004](docs/decisions/0004-handler-result-caching.md)/[ADR-0017](docs/decisions/0017-dependency-light-core.md)). `HybridCache` is two-tier (in-process L1 + an optional `IDistributedCache` L2 it auto-discovers from DI); the **recommended L2** for most apps is `Elarion.Caching.PostgreSql` (below).
- `Elarion.Caching.PostgreSql` — the recommended **L2 distributed cache** for most Elarion applications: a PostgreSQL `UNLOGGED` table behind `HybridCache`, reusing the Postgres you already run instead of operating a separate Redis. `AddElarionPostgreSqlHandlerCaching(connectionString[, configure])` registers the official `Microsoft.Extensions.Caching.Postgres` distributed cache as the L2 — with Elarion defaults `UseWAL = false` (⇒ an auto-created `UNLOGGED` table `elarion_cache` in the `public` schema; `CreateIfNotExists = true`), all overridable via the optional `Action<PostgresCacheOptions>` which runs last — and then calls `AddElarionHandlerCaching`, so `HybridCache` picks up the `IDistributedCache`. An opt-in sibling of `Elarion.Caching` (the same `Elarion.Blobs.PostgreSql` ↔ `Elarion.Blobs` shape): depends only on `Elarion.Caching` + `Microsoft.Extensions.Caching.Postgres` (which pulls `Npgsql`) and, like the other Npgsql-backed packages, is not `IsAotCompatible`. The `UNLOGGED` tradeoff (truncated on crash, absent on physical replicas) is correct for a cache — worst case a cold repopulate, never data loss; Redis (or any `IDistributedCache` registered before `AddElarionHandlerCaching`) stays the better L2 for very high-throughput, independently-scaled, or multi-region caches (see [ADR-0020](docs/decisions/0020-postgres-unlogged-l2-cache.md) and [the caching capability doc](docs/capabilities/caching.mdx)).
- `Elarion.Resilience` — the Microsoft/Polly-backed `IResiliencePipelineRunner` default (`MicrosoftResiliencePipelineRunner` + `AddElarionResilience`, namespace `Elarion.Resilience`). Opt-in: referencing it pulls `Microsoft.Extensions.Resilience`. The lightweight policy catalog/metadata and the `ResilienceDecorator` stay in core/`Elarion.Abstractions`; `AddElarionScheduler` registers the catalog, so basic scheduling needs no resilience package, while `[Resilient]` handlers and deferred scheduler retries opt into the runner (see [ADR-0017](docs/decisions/0017-dependency-light-core.md)).
- `Elarion.Scheduling.EntityFrameworkCore` — **cross-instance scheduler coordination** over EF Core/PostgreSQL (ADR-0025): a node claims each recurring occurrence by inserting a `SchedulerClaimEntity` row right before executing it, so exactly one node in a cluster runs it (the losers record a `claimed-elsewhere` skip and continue their local chains). Cron occurrences claim their exact wall-clock slot (`INSERT … ON CONFLICT DO NOTHING`, the composite PK is the fence); fixed-rate/fixed-delay due times are node-anchored, so they dedupe by a one-interval window (a per-job `pg_advisory_xact_lock` serializes the conditional insert — a bare `NOT EXISTS` would race under read committed); one-time startup schedules stay per-node, and `[ScheduledJob(Placement = JobPlacement.EveryNode)]` opts a recurring job out of coordination entirely (for jobs maintaining process-local state — coordinated, they would leave the losing nodes silently stale; placement is a descriptor property, never a seam concern). Fail-closed: an unreachable claims database skips the occurrence rather than duplicating it. `UseElarionSchedulerClaims(ModelBuilder, tableName?, schema?, snakeCase = true)` maps the claims table (default `elarion_scheduler_claims`), or `[GenerateElarionSchedulerClaims(SnakeCase/TableName/Schema)]` on a `[GenerateDbSets]` context (a bundled generator `Elarion.Scheduling.EntityFrameworkCore.Generators`, `IsPackable=false`, `ELSCH001`); `AddElarionSchedulerEntityFrameworkCore<TDbContext>` replaces the local coordinator and registers the claim-retention purge worker (`SchedulerClaimsOptions`, default 7-day retention). Runtime one-shot jobs stay in-memory (durable one-shots are ADR-0025 phase 2). Targets PostgreSQL. Depends on `Elarion.Abstractions` and EF Core (`Microsoft.EntityFrameworkCore.Relational`).
- `Elarion.FeatureFlags.OpenFeature` — the OpenFeature-backed `IFeatureFlagService` default (`OpenFeatureFeatureFlagService` over the OpenFeature `IFeatureClient` + `AddElarionOpenFeature`), plus `ElarionEvaluationContext` mapping `ICurrentUser` → OpenFeature `EvaluationContext` (targeting key + the `UserId`/`Groups` attributes the MS provider reads), so `[FeatureGate]` works against any OpenFeature provider with no `HttpContext`. The host brings the provider via `AddOpenFeature(...)`. Depends only on `OpenFeature` core. See [Feature flag model](#feature-flag-model).
- `Elarion.FeatureFlags.FeatureManagement` — the batteries-included default: `AddElarionFeatureManagement(configuration)` wires the OpenFeature Microsoft.FeatureManagement provider (the `OpenFeature.Contrib.Provider.FeatureManagement` package, built on `IVariantFeatureManager`) behind `Elarion.FeatureFlags.OpenFeature`, so `[FeatureGate]` reads config-driven flags out of the box. One line of sugar over `AddOpenFeature(...) + AddElarionOpenFeature()`; isolating the Microsoft.FeatureManagement dependency here keeps it pay-for-what-you-use (see [ADR-0016](docs/decisions/0016-feature-flag-gating.md)).
- `Elarion.Blobs` — implementation-neutral, streaming-first blob storage contracts and DTOs. `IBlobStore` is a minimal core (`SaveAsync(BlobUploadRequest, Stream)`, `OpenReadAsync` returning a disposable `BlobDownload` of metadata + an open content stream, plus metadata/delete/exists), so callers never have to buffer a whole blob and backends implement only the primitives. Ergonomic `byte[]`/file saves and buffered/copy-to reads (`SaveFromFileAsync`, `DownloadContentAsync`, `ReadAllBytesAsync`, `DownloadToAsync`) live as `BlobStoreExtensions` over that core; `BlobUploadRequest.ContentLength` is an optional hint while the recorded `Size` is always the actual bytes written. Also owns the **upload lifecycle**: `BlobLifecycleState` (`Pending`/`Committed`) plus additive `BlobUploadRequest.InitialState` (default `Committed`, so a plain save stays permanent) and `ExpiresAt`, and the optional `IBlobLifecycle` capability (`CommitAsync` promotes a pending blob inside the caller's transaction — idempotent, atomic with the entity insert; `DeleteExpiredPendingAsync` is the garbage-collection entry point). This is the "pre-upload then reference, reclaim if abandoned" model that solves attaching files to an entity over a JSON transport — see [the blob uploads capability doc](docs/capabilities/blob-uploads.mdx).
- `Elarion.Blobs.PostgreSql` — PostgreSQL-backed blob storage using EF Core model configuration and Npgsql content I/O. Writes stream a seekable source straight into the `bytea` column without buffering, and stream a non-seekable source without buffering too when the caller supplies a `BlobUploadRequest.ContentLength` hint (the bind requires the length up front; the actual bytes are verified against the hint so the recorded `Size` stays truthful), buffering only when the hint is absent; reads **stream** too — `OpenReadAsync` draws a dedicated connection from the registered `NpgsqlDataSource` (`AddElarionPostgreSqlBlobStore<TDbContext>(connectionString | DI-resolved data source)`) and reads via `CommandBehavior.SequentialAccess` + `NpgsqlDataReader.GetStream`, with the reader/command/connection owned by the returned `BlobDownload` (released on its disposal, double-dispose safe); a read inside a caller-owned ambient transaction stays buffered on the context connection so it sees the caller's own uncommitted writes. `UseElarionBlobStorage(ModelBuilder, tableName?, contentTableName?, schema?, snakeCase = true)` maps the two tables (defaults `stored_blobs`+`blob_contents`, or `StoredBlobs`+`BlobContents` with `snakeCase: false`), or `[GenerateElarionBlobStorage(SnakeCase/TableName/ContentTableName/Schema)]` on a `[GenerateDbSets]` context (a bundled generator `Elarion.Blobs.PostgreSql.Generators`, `IsPackable=false`, `ELBLB001`) emits the `DbSet<StoredBlob>` (the content row type is `internal` — mapped, no `DbSet`) and applies it through the EF generator's per-feature model-config seam; the store's raw Npgsql SQL is built from the EF model (`ISqlGenerationHelper` + resolved column names), so the overrides apply to the raw content path too. Implements `IBlobLifecycle` on the same store (`state`/`expires_at` columns with a **partial index over pending rows**; `CommitAsync` mutates the tracked row so it persists on the caller's `SaveChangesAsync`; `DeleteExpiredPendingAsync` is a set-based delete that re-checks `state = Pending` so it never races a commit, with content removed by the existing `ON DELETE CASCADE`). `AddElarionPostgreSqlBlobLifecycle<TDbContext>` adds the lifecycle plus the `BlobGarbageCollector` hosted sweeper (outbox-style `PeriodicTimer` + partial index, lease-free because the idempotent delete is self-coordinating).
- `Elarion.Blobs.AspNetCore` — minimal direct blob-upload endpoint (`MapElarionBlobUploads`: `POST` bytes → a `Pending` `BlobRef`, `DELETE /{id}` → owner-scoped cancel; `AddElarionBlobUploads`). Not a new protocol — the irreducible "accept bytes, return a reference" surface over `IBlobStore`, fitting FilePond's `process`/`revert` and plain `fetch`/`<form>` clients. Bakes the current user's id into the stored name, enforces an optional content-type allow-list and size cap (early reject + a `LengthCappingStream` for unknown-length bodies → 413). Depends on `Elarion.Abstractions` (`ICurrentUser`) + `Elarion.Blobs`; ASP.NET via `FrameworkReference`.
- `Elarion.Blobs.Tus` — **tus 1.0** resumable-upload transport (`MapElarionTus`: `OPTIONS`/`POST`/`HEAD`/`PATCH`/`DELETE` for Creation + Core + Expiration + Termination; `AddElarionTus`). The open, resumable, large-file, browser-close-resilient protocol supported natively by Uppy (`@uppy/tus`) and `tus-js-client` — the recommended transport. A completed upload is written as a `Pending` blob whose reference is returned in the `Elarion-Blob-Ref` response header (also on `HEAD`). The `ITusUploadStore` seam stages bytes; the default `InMemoryTusUploadStore` is a singleton (per-upload state) that finalizes through a fresh scope. ASP.NET-only (no EF); depends on `Elarion.Abstractions` + `Elarion.Blobs`.
- `Elarion.Blobs.Tus.PostgreSql` — durable PostgreSQL `ITusUploadStore` (`PostgreSqlTusUploadStore<TDbContext>`) that stages in-progress bytes in a `tus_uploads` `bytea` column so an upload survives a restart and resumes on any instance. Each chunk is one conditional `UPDATE … SET data = data || …` guarded on the current offset (stale/concurrent `PATCH` → `TusOffsetConflictException`); on completion it writes the `Pending` blob and clears the staged bytes. `UseElarionTusStorage(ModelBuilder, tableName?, schema?, snakeCase = true)` adds the table (default `tus_uploads`, or `TusUploads` with `snakeCase: false`; partial index over incomplete sessions), or `[GenerateElarionTusStorage(SnakeCase/TableName/Schema)]` on a `[GenerateDbSets]` context (a bundled generator `Elarion.Blobs.Tus.PostgreSql.Generators`, `IsPackable=false`, `ELTUS001`) emits the `DbSet<TusUploadRow>` and applies it through the EF generator's per-feature model-config seam — the store's raw SQL (including the conditional append) is built from the EF model, so the overrides apply there too; `AddElarionTusPostgreSql<TDbContext>` replaces the in-memory default and registers the `TusUploadGarbageCollector` that reaps expired incomplete sessions (the *abort incomplete multipart upload* analog). Depends on `Elarion.Blobs.Tus` + `Elarion.Blobs` + EF Core.
- `Elarion.JsonRpc` — the JSON-RPC **protocol adapter** over the shared `HandlerDispatcher` bus (not a wire/host): `JsonRpcDispatcher` wraps a `HandlerDispatcher` and serves only the operations flagged `HandlerTransports.JsonRpc`, owning the JSON-RPC concerns — envelopes, result/error types, telemetry, schema export, JSON param (de)serialization, and `Result`→`RpcError` mapping (`AppErrorMapper`, the default `IAppErrorTranslator<RpcError>`). The MCP adapter (`McpDispatcher`) lives here too, over the same bus filtered to `HandlerTransports.Mcp`. References `Elarion.Abstractions` (for the handler/result/error contracts, the named bus, and the dispatch-scope rail) but stays ASP.NET-free — it knows nothing about HTTP/Kestrel and could be driven over any wire. The actual HTTP/Kestrel hosting of JSON-RPC lives in `Elarion.AspNetCore` (`MapJsonRpc`), which depends on this package. The named bus and the dispatch-scope rail themselves live in `Elarion.Abstractions`, not here.
- `Elarion.AspNetCore` — ASP.NET Core JSON-RPC endpoint mapping, batch execution, current-user middleware (the snapshot exposes `ICurrentUser` claim access), HTTP transport support, and minimal-API endpoint mapping for `[HttpEndpoint]` handlers (`HttpAppErrorMapper` maps `Unauthorized`→401/`Forbidden`→403, `ElarionHttpResults`).
- `Elarion.AspNetCore.Identity` — optional ASP.NET Core Identity **host wiring**. `AddElarionIdentity<TUser, TRole, TKey, TDbContext>` wires `AddIdentity` + EF stores against the application's **plain** `DbContext` (no `IdentityDbContext` inheritance), maps `ICurrentUser` to the Identity claim types, and calls the core `AddElarionAuthorization`. The web-free Identity *model* (`[GenerateElarionIdentity]`, `ApplyElarionIdentity`) and its bundled generator live in `Elarion.EntityFrameworkCore.Identity` (below) — this package no longer carries the model surface or its generator. Depends on `Microsoft.AspNetCore.Identity.EntityFrameworkCore`, `Elarion.Abstractions`, and `Elarion.AspNetCore` (and so the ASP.NET shared framework).
- `Elarion.EntityFrameworkCore.Identity` — the **web-free Identity model**, the EF half of the Identity integration (mirrors the `Elarion.EntityFrameworkCore` ↔ `Elarion.AspNetCore` split). `[GenerateElarionIdentity<TUser, TRole, TKey>(SnakeCase = true, Schema = …, TablePrefix = …)]` (on a `[GenerateDbSets]` context) drives a **bundled generator** (`Elarion.EntityFrameworkCore.Identity.Generators`, `IsPackable=false`) that emits the seven Identity `DbSet`s and implements the EF generator's `OnEntitiesConfigured` seam by calling `ModelBuilder.ApplyElarionIdentity<…>(schema?, tablePrefix?, snakeCase = true)` — the self-contained Identity model (keys, composite keys, indexes, snake_case table/column/index names; no `EFCore.NamingConventions` dependency). `Schema` applies to all seven tables and `TablePrefix` is prepended verbatim to every Identity table name (e.g. `"auth_"` → `auth_users`) — the table-name override (no per-table params); snake index names incorporate the prefixed table name. `ELIDN001` if `[GenerateDbSets]` is missing. **Depends only on `Microsoft.AspNetCore.Identity.EntityFrameworkCore` (EF Core + Identity stores) — no `FrameworkReference`**, so a persistence/application layer composes the snake_case Identity model without taking on the ASP.NET shared framework. The host wiring (`AddElarionIdentity`) stays in `Elarion.AspNetCore.Identity`.
- `Elarion.AspNetCore.Mcp` — Model Context Protocol (MCP) server integration: exposes MCP-surfaced `[Handler]` operations as tools over Streamable HTTP via the `McpDispatcher` adapter (over the shared `HandlerDispatcher`, filtered to `HandlerTransports.Mcp`), independent of the JSON-RPC endpoint (`AddElarionMcp`, `MapElarionMcp`). The only package referencing the `ModelContextProtocol` SDK.
- `Elarion.AspNetCore.SchemaGeneration` — MSBuild package and host-launching tool for generating JSON-RPC schemas during build.
- `Elarion.EntityFrameworkCore` — marker attributes for EF Core DbSet/configuration generation: `[EntityConfiguration]` (placed on an `IEntityTypeConfiguration<T>` implementation; the single source of truth that drives **both** the entity's `DbSet<T>` and its `Configure(...)` application — there is no separate entity marker, and a configuration class may implement `IEntityTypeConfiguration<T>` more than once so one class can contribute several DbSets), `[GenerateDbSets]` (on the concrete partial `DbContext` class — the generator emits the `DbSet<T>` properties and `ConfigureEntities` onto the context itself; there is no context interface, since the database is application logic handlers use directly), plus the assembly-level `[UseElarionEntityFrameworkCore(Provider = EfCoreProvider.Npgsql)]` opt-in (`EfCoreProvider` enum, default `Portable`) that lets EF generators emit provider-optimized variants. Dependency-free at runtime (no EF Core or runtime package references). The published NuGet package **bundles the `Elarion.EntityFrameworkCore.Generators` analyzer** (`analyzers/dotnet/cs/`), so referencing the marker package directly is sufficient for any assembly that declares `[EntityConfiguration]`/`[GenerateDbSets]` types (including a separate entity/configuration assembly, whose generator emits the per-assembly configuration manifest the context assembly reads) — there is no standalone generator package.
- `Elarion.Paging` — keyset (cursor) and offset pagination primitives: the `[Keyset<TEntity>]` attribute (declared on a dedicated partial class, not the entity, so an entity may have any number of orderings), `IKeysetDefinition<T>` plus generated-keyset support types, an opaque cursor codec, `SortMap`/`SortMapBuilder` (composite multi-column sorts with per-entry tiebreakers and a `SortDirection`), and the `IQueryable` paging extensions that produce the transport-neutral `Page<T>`. Both keyset and offset paging take an explicit definition (`MyKeyset.Definition` / a `SortMap`). Also hosts the **data-level authorization list filter** (Leg B of resource authorization, see [ADR-0013](docs/decisions/0013-resource-and-data-level-authorization.md) and [the resource-authorization concept doc](docs/concepts/resource-authorization.mdx)): the `[ResourceFilter<TEntity>]` attribute (a dedicated partial class, like `[Keyset]`) and the `IQueryable.WhereAuthorized(spec, user)` extension that composes a generated `IQueryAuthorizer<TEntity>` predicate **before** paging, so the database filters rows the caller may not see (no in-memory `@PostFilter`). The `[ResourceFilter]` generator (in `Elarion.EntityFrameworkCore.Generators`) emits `AND(scope) AND OR(grant)` rules — `OwnerProperty`, `TenantProperty`, and a `Shared` rule that becomes a correlated `EXISTS` over the grants table for the caller's user + roles (`ELRES001`–`ELRES005`). Depends on EF Core (`Microsoft.EntityFrameworkCore.Relational`) and `Elarion.Abstractions`.
- `Elarion.Authorization.EntityFrameworkCore` — the **data-level authorization grants backend** (resource sharing), the EF half of resource authorization ([ADR-0013](docs/decisions/0013-resource-and-data-level-authorization.md)). Ships a DB-native generic `ResourceGrant` table keyed by `(resourceType, resourceId, principalKind, principalId, operation)` — sharing a resource with a **user or a role** — plus `IResourceGrantStore`/`EfCoreResourceGrantStore<TDbContext>`, the grants-backed `IResourceAuthorizer` (the `[RequireResource]` point-check default), and `IResourceGrantSource` (the `IQueryable<ResourceGrantEntity>` a `Shared` filter's `EXISTS` consults). `ApplyElarionResourceGrants(ModelBuilder, tableName?, schema?, snakeCase = true)` maps the table (default `elarion_resource_grants`, or `ElarionResourceGrants` with `snakeCase: false`; the principal index name derives from the table name — `ix_{table}_principal` / `IX_{table}_Principal`), or the Identity-consistent `[GenerateElarionResourceGrants(SnakeCase/TableName/Schema)]` (a **bundled generator** `Elarion.Authorization.EntityFrameworkCore.Generators`, `IsPackable=false`, `ELRG001`) emits the `DbSet` and applies it through the EF generator's per-feature model-config seam. `AddElarionResourceAuthorization<TDbContext>` wires it. **Deliberately auth-provider-neutral — not folded into `Elarion.AspNetCore.Identity`**: grants key off principal *strings* from `ICurrentUser` (user id + role names), so it works for Entra ID / OIDC / JWT and composes with Identity (`ApplyElarionResourceGrants` beside `ApplyElarionIdentity`). Depends on `Elarion.Abstractions` and EF Core (`Microsoft.EntityFrameworkCore.Relational`).
- `Elarion.Settings` — runtime-changeable, key/value settings with **swappable abstractions on both sides** (see [ADR-0011](docs/decisions/0011-runtime-settings-subsystem.md) and [the settings concept doc](docs/concepts/settings.mdx)). The **sink side** is `ISettingsStore` (read/write/enumerate, keyed by an extensible `SettingsScope` — `Global` and `User(ownerId)` ship, modeled as an open `(Kind, Owner)` value so `tenant`/`environment` add no contract change — plus a hierarchical `:`-separated key whose hierarchy is *virtual* like env vars; writes carry optimistic-concurrency `SettingWriteResult`) and the listen seam `ISettingsChangeSource`/`ISettingsChangePublisher` handing out `IChangeToken`s. The shipped default backend is in-process (`InProcessSettingsStore` + `InProcessSettingsChangeSource`, single-instance notify); the database backend ships in `Elarion.Settings.EntityFrameworkCore` and the `IConfiguration`/`IOptionsMonitor` adapter in `Elarion.Settings.Configuration` (both below), while the cross-instance change source ships in `Elarion.Settings.PostgreSql` (Postgres `LISTEN/NOTIFY`, below; Redis pub/sub is a possible future provider over the same contracts). The **consuming side** is the scoped, AOT-clean `ISettingsManager` accessor (typed get/set via source-gen `JsonTypeInfo<T>`, raw string get/set, `Watch`, and the per-user scope resolved from `ICurrentUser`, failing closed when unauthenticated — mirroring the handler cache). Wired by `AddElarionSettings`. Depends on `Elarion.Abstractions` and `Microsoft.Extensions.{Configuration,DependencyInjection}.Abstractions`.
- `Elarion.Settings.EntityFrameworkCore` — the EF Core database backend for settings: `EfCoreSettingsStore<TDbContext>` implements `ISettingsStore` against the caller's `DbContext`. Writes are **change-tracker-free and immediate** (like the outbox store): `ExecuteUpdate`/`ExecuteDelete` for update/remove and a raw provider-correct `INSERT` (identifiers resolved from the EF model via `ISqlGenerationHelper`) for create, so a settings write never flushes the caller's unrelated tracked changes. Optimistic concurrency is enforced explicitly (an update guarded on the read version → zero rows ⇒ `ConcurrencyConflict`; a create losing to a concurrent insert is detected by re-checking existence). The `Setting` entity flattens `SettingsScope` into `(kind, owner, key)` (a non-owned scope stores an empty `owner` because a relational PK cannot be nullable); `UseElarionSettings(ModelBuilder, tableName?, schema?, snakeCase = true)` maps the settings table (default name `elarion_settings`, or `ElarionSettings` with `snakeCase: false`; overridable table/schema) with snake_case columns and a `version` concurrency token, or `[GenerateElarionSettings(SnakeCase/TableName/Schema)]` on a `[GenerateDbSets]` context (a bundled generator `Elarion.Settings.EntityFrameworkCore.Generators`, `IsPackable=false`, `ELSET001`) emits the `DbSet<Setting>` and applies it through the EF generator's per-feature model-config seam. Writes are announced through the `IEfCoreSettingsChangeNotifier` seam (the store hands it the `DbContext`): the default publishes via the in-process `ISettingsChangePublisher` (single-instance) and skips writes inside a caller-owned ambient transaction (no phantom notifications), while `Elarion.Settings.PostgreSql` swaps in a notifier that rides the store's own connection so the database commit-gates delivery. Wired by `AddElarionSettingsEntityFrameworkCore<TDbContext>` (replaces the in-process store); the app maps the table in `OnModelCreating` and owns the migration. Depends on `Elarion.Settings` and EF Core (`Microsoft.EntityFrameworkCore.Relational`).
- `Elarion.EntityFrameworkCore.UnitOfWork` — the framework-owned EF Core transaction / unit-of-work boundary. `EfUnitOfWork<TDbContext>` implements the EF-free `IUnitOfWork`/`IUnitOfWorkScope` seam (`Elarion.Abstractions.Pipeline`) over `DbContext.Database.BeginTransactionAsync`, with PostgreSQL `SET LOCAL lock_timeout` (from `UnitOfWorkOptions.LockTimeout`, provider-detected by name so it stays Npgsql-package-free) and savepoint support. `AddElarionUnitOfWork<TDbContext>` registers it (replacing any default). The framework `TransactionDecorator` (promoted from the Billing sample into `Elarion.Abstractions.Pipeline`, written against `IUnitOfWork` not `DbContext`, `AppliesTo` = `ICommand`/`IIntegrationEvent` and not `[Idempotent]`) and the idempotency decorator both compose this one boundary. Depends on `Elarion.Abstractions` and EF Core (`Microsoft.EntityFrameworkCore.Relational`). See [ADR-0021](docs/decisions/0021-idempotency.md) and [the idempotency concept doc](docs/concepts/idempotency.mdx).
- `Elarion.Idempotency.EntityFrameworkCore` — the durable EF Core idempotency store (the atomic exactly-once guarantee). `EfCoreIdempotencyStore<TDbContext>` claims a key with a change-tracker-free `INSERT … ON CONFLICT (scope, owner, key) DO NOTHING` run **inside the caller's transaction** (so the key row commits atomically with the handler's business writes) — the `ON CONFLICT` form never poisons the transaction: a returned row is the claim, zero rows means the key already completed (read it and replay), and a PostgreSQL `55P03` `lock_timeout` while blocked on an in-flight duplicate means "in progress" (409). The composite-PK unique constraint serializes concurrent duplicates across nodes with **no external lock** (the DB row is the fence). `IdempotencyKeyEntity` mirrors the settings `Setting` (composite key, `version` token); `ApplyElarionIdempotencyKeys(ModelBuilder, tableName?, schema?, snakeCase = true)` maps `elarion_idempotency_keys` (or `ElarionIdempotencyKeys` with `snakeCase: false`; the purge index name derives from the table name — `ix_{table}_purge` / `IX_{table}_Purge`), or the Identity-consistent `[GenerateElarionIdempotencyKeys(SnakeCase/TableName/Schema)]` (a **bundled generator** `Elarion.Idempotency.EntityFrameworkCore.Generators`, `IsPackable=false`, `ELIDEMEF001`) emits the `DbSet` and applies it through the EF generator's per-feature model-config seam. `AddElarionIdempotencyEntityFrameworkCore<TDbContext>` wires the store, the EF unit of work, and the retention `IdempotencyKeyPurgeService`. Targets PostgreSQL. The transport-neutral `[Idempotent]` attribute, seams (`IIdempotencyStore`/`IIdempotencyKeyAccessor`/`IIdempotencyPayloadPolicy`), the `IdempotencyDecorator`, and the in-memory default store (`AddElarionIdempotency`) live in `Elarion.Abstractions`/`Elarion`; HTTP `Idempotency-Key` capture (`UseElarionIdempotencyKey`) in `Elarion.AspNetCore`. Depends on `Elarion`, `Elarion.EntityFrameworkCore.UnitOfWork`, and EF Core. See [the idempotency concept doc](docs/concepts/idempotency.mdx) and [ADR-0021](docs/decisions/0021-idempotency.md).
- `Elarion.Settings.Configuration` — the consuming-side `IConfiguration` adapter. `SettingsConfigurationSource`/`SettingsConfigurationProvider` surface the `Global` settings as an `IConfiguration` provider (settings keys are already `:`-separated, mapping straight onto the config hierarchy), with `IChangeToken` reload — so `IConfiguration`/`IOptionsMonitor<T>` consumers, and the scheduler's `${...}` variable substitution (which re-resolves per occurrence), pick up runtime changes. Authoring a provider is AOT-safe (flat string key/value; reflection is the consumer's opt-in via `.Get<T>()`). Because configuration is built before DI, the store-agnostic `SettingsConfigurationRefresher` (a `BackgroundService`) performs the initial load once the container exists and reloads on each global change (change-token-driven via a coalescing channel, reading through `ISettingsStore` on a fresh scope). Wired by `AddElarionSettingsConfiguration(IHostApplicationBuilder)`; only the `Global` scope is surfaced (per-user settings are not app-wide config — read those via `ISettingsManager`). Depends on `Elarion.Settings` and `Microsoft.Extensions.{Configuration,Hosting.Abstractions,DependencyInjection.Abstractions,Logging.Abstractions}`.
- `Elarion.Settings.PostgreSql` — the **cross-instance settings change source** over PostgreSQL `LISTEN/NOTIFY` (ADR-0024, the multi-node companion to the EF Core store): `AddElarionPostgreSqlSettingsChanges(connectionString | NpgsqlDataSource[, configure])` replaces the in-process `ISettingsChangeSource`/`ISettingsChangePublisher` (authoritative in either registration order) so a settings write on one node fires `IChangeToken` watchers — and everything built on them: `ISettingsManager.Watch`, the settings `IConfiguration` provider, and the scheduler's `${...}` live rescheduling — on every node. A hosted `PostgreSqlSettingsChangeListener` holds one dedicated `LISTEN` connection per node (reconnect with exponential backoff; after a reconnect it fires **all** watches, since PostgreSQL does not queue notifications for absent listeners), and watch tokens fire **only** from that loop — a local publish loops back through the database, so every node observes changes in commit order through one path. The EF store's writes go through the swapped-in `PostgreSqlTransactionalSettingsChangeNotifier`, which issues `pg_notify` on the store's own connection — PostgreSQL makes `NOTIFY` transactional, so a write inside an ambient transaction is announced only on commit and never on rollback (closing the skipped-transactional-notification gap the default notifier documents). Channel and backoff via `PostgreSqlSettingsChangeOptions` (default channel `elarion_settings_changed`). Like the other Npgsql-backed packages it is not `IsAotCompatible`. Depends on `Elarion.Settings`, `Elarion.Settings.EntityFrameworkCore` (the notifier seam), and `Npgsql`.
- `Elarion.Messaging.InMemory` — the simple, best-effort **in-memory integration-event (Plane B) bus**, commit-gated by the EF Core DbContext transaction (`AddElarionInMemoryIntegrationEventBus<TContext>`; `AddElarionInMemoryEventBus<TContext>` also wires the `Elarion` domain tier). A non-durable sibling of the EF Core outbox: `InMemoryIntegrationEventBus` buffers events into an internal per-scope `EventDispatchScope`, which EF Core interceptors (`EventDispatchSaveChangesInterceptor`/`EventDispatchTransactionInterceptor`) flush to the hosted `EventDispatchPump` after commit and discard on rollback. The `<TContext>` overload attaches those scoped interceptors to the app's context automatically via an `IDbContextOptionsConfiguration<TContext>` (EF Core does not auto-discover application-DI `IInterceptor` services), so a plain `AddDbContext<TContext>` suffices; a low-level non-generic `AddElarionInMemoryIntegrationEventBus` registers the building blocks without attaching them. There is no public dispatch-scope seam. Depends on EF Core (`Microsoft.EntityFrameworkCore.Relational`), `Elarion` (for the shared registry/context), and `Elarion.Abstractions`; not needed with the transactional outbox.
- `Elarion.Messaging.Outbox` — EF Core transactional outbox: a durable `IIntegrationEventBus` (Plane B) that records each integration event as an `OutboxMessage` row in the caller's `DbContext` (committed atomically with the business data, discarded on rollback) and a hosted `OutboxDeliveryService` that polls, claims via a provider-neutral conditional `ExecuteUpdate` lease, dispatches to integration consumers on isolated scopes (at-least-once), and finalizes/purges. An `IOutboxStore` seam isolates the EF Core SQL so the bus, dispatcher, and delivery loop stay database-agnostic and unit-testable. `UseElarionOutbox(ModelBuilder, tableName?, schema?, snakeCase = true)` adds the table (default name `elarion_outbox_messages`, or `ElarionOutboxMessages` with `snakeCase: false`; overridable table/schema; with a partial index over pending rows — filter `processed_on_utc IS NULL` / `"ProcessedOnUtc" IS NULL` — so the poll stays an indexed probe), or `[GenerateElarionOutbox(SnakeCase/TableName/Schema)]` on a `[GenerateDbSets]` context (a bundled generator `Elarion.Messaging.Outbox.Generators`, `IsPackable=false`, `ELOBX001`) emits the `DbSet<OutboxMessage>` and applies it through the EF generator's per-feature model-config seam; `AddElarionOutbox<TDbContext>(...)` wires the tier. The claim/delivery logic is provider-neutral; the partial index assumes a provider that supports filtered indexes (PostgreSQL/SQL Server/SQLite — MySQL users supply an unfiltered index). Depends on EF Core (`Microsoft.EntityFrameworkCore.Relational`) and `Elarion.Abstractions`.
- `Elarion.Generators` — Roslyn source generators for handlers, validators, services, modules, the module-scoped RPC/HTTP/MCP transport maps (`AppModuleDiscoveryGenerator`), resilience policies, scheduled jobs, event consumers, and the permission catalog (`PermissionCatalogGenerator` — the per-module runtime `IPermissionCatalog` contributions plus the compile-time `ElarionPermissions` static, from `[RequirePermission]`/`[RequireRole]`; cross-assembly entries ride the `ElarionManifest`), plus the per-module `ConfigureDefaultServices` aggregation (`ModuleDefaultServicesGenerator`) that auto-wires and feature-gates all of a module's registrations. `HandlerRegistrationGenerator` also **auto-attaches the authorization decorator** as the outermost functional gate (just inside tracing) by inspecting the handler for `[Require*]` attributes — opt-in by default, or to every non-`[AllowAnonymous]` handler under an in-scope `[ElarionAuthorizationDefaults]` — reporting `ELAUTH001` when the handler's response cannot represent failure. It likewise **auto-attaches the feature-gate decorator** (`FeatureGateDecorator`) just inside the authorization gate for any handler carrying `[FeatureGate]`, reporting `ELFEAT001` (response cannot represent failure) and `ELFEAT002` (gate with no feature name). For **variant service injection** (ADR-0019) it wraps any handler whose constructor depends on a `[FeatureVariant]` contract in the `AsyncResolvedHandler` proxy (warming the variant before construction), and the sibling `VariantServiceRegistrationGenerator` emits the keyed/binding/transparent-factory/provider registrations (`ELVAR001`–`ELVAR007`; `[FeatureVariant]` is a modifier on `[Service]`, which `ModuleServiceRegistrationGenerator` then skips so the variant path owns the registration). Also hosts the cross-module communication tooling: the `ModuleApiGenerator` (a typed in-process API per `[GenerateModuleApi]` facade) and the `ModuleBoundaryAnalyzer` (the repo's first `DiagnosticAnalyzer`, `ELMOD002`). See [Cross-module communication](#cross-module-communication). **Not published as a standalone package** (`IsPackable=false`); its analyzer DLL is bundled into the `Elarion` runtime package.
- `Elarion.EntityFrameworkCore.Generators` — Roslyn generators for DbSet properties and entity configuration application (driven by `[EntityConfiguration]` — `DbContextGenerator` derives each context's entity set from the configurations it discovers, emits a `DbSet<T>` per configured entity and a `modelBuilder.ApplyConfiguration<T>(...)` per configuration in the generated `ConfigureEntities`, reports `ELEFC001` for an `[EntityConfiguration]` that implements no `IEntityTypeConfiguration<T>`, emits `ConfigureEntities` for **every** `[GenerateDbSets]` context — even one with no `[EntityConfiguration]` entities — ending it with a neutral `partial void OnEntitiesConfigured(ModelBuilder)` seam that other generators implement (the Identity generator uses it to compose Identity onto a plain context), and reads cross-assembly configurations from a per-assembly `EntityConfigurationManifest` that the dedicated sibling `EntityConfigurationManifestGenerator` emits — split out so the DbSet generator emits only members onto the partial context, never compilation-mutating assembly attributes, keeping its output refresh reliable in IDE source-generator hosts; the two share `[EntityConfiguration]` discovery so they cannot drift, and `ELEFC001` is reported only by `DbContextGenerator` so it is never duplicated), and for keyset pagination definitions (the `[Keyset<TEntity>]` emitter targeting `Elarion.Paging`). **Not published as a standalone package** (`IsPackable=false`); its analyzer DLL is bundled into the `Elarion.EntityFrameworkCore` marker package. It fills each annotated partial class with the `IKeysetDefinition<TEntity>` implementation and a static `Definition` singleton, so handlers page with the explicit `source.ToKeysetPageAsync(request, MyKeyset.Definition, selector)` overload (symmetric with offset paging, which passes a `SortMap`). The keyset class is decoupled from the entity, so multiple orderings per entity each emit a distinct definition; a non-partial or nested keyset class is reported (`ELKEY005`). The keyset emitter is provider-aware: with `[assembly: UseElarionEntityFrameworkCore(Provider = EfCoreProvider.Npgsql)]` and a uniform-direction multi-column keyset it emits a PostgreSQL row-value seek (`EF.Functions.GreaterThan`/`LessThan` over `ValueTuple`s), otherwise it falls back to the portable lexicographic predicate.
- `@swimmesberger/elarion-jsonrpc-client-generator` — TypeScript CLI/library that converts exported Elarion JSON-RPC schemas into method contracts, Zod result schemas, and a portable fetch client. Lives in `src/elarion-jsonrpc-client-generator`.

## Architecture boundaries

- Core framework packages must stay reusable and domain-neutral. Do not add consuming-application names, domain logic, deployment conventions, or app-specific dependencies.
- `Elarion.Abstractions` must not depend on runtime integration packages.
- `Elarion` core is **dependency-light**: it pulls only `Elarion.Abstractions` plus `Microsoft.Extensions.*` *Abstractions* packages. A provider-backed default that carries a heavy runtime dependency lives in its own opt-in sibling package so the dependency is pulled only when used — `Elarion.Caching` (HybridCache), `Elarion.Resilience` (Polly), `Elarion.FeatureFlags.OpenFeature`/`Elarion.FeatureFlags.FeatureManagement` (feature flags). The seam + pipeline decorator stay in `Elarion.Abstractions`; the heavy default lives one layer out (see [ADR-0017](docs/decisions/0017-dependency-light-core.md)). Do not reintroduce a concrete third-party runtime dependency into core or Abstractions.
- `Elarion` is **transport-agnostic**: it must not reference any protocol or host package (including `Elarion.JsonRpc`), ASP.NET Core, or EF Core. Two distinct layers sit above core: **protocol** packages (`Elarion.JsonRpc` — message format + dispatcher, no wire/host) and **host** packages that bind protocols to a wire (`Elarion.AspNetCore` serves JSON-RPC over `/rpc` plus REST; `Elarion.AspNetCore.Mcp` hosts MCP over Streamable HTTP). Host packages depend on the protocol packages, not vice versa.
- The event bus stays split across the two packages: `Elarion.Abstractions` owns the transport-neutral messaging contracts and the domain/integration plane split, while `Elarion` owns only the in-memory implementation. An alternative backend (transactional outbox, message broker) replaces the in-memory runtime by implementing `IIntegrationEventBus` — the only broker-portable plane — without touching the abstractions.
- `Elarion.Blobs` must stay provider-neutral. Provider implementations such as `Elarion.Blobs.PostgreSql` own storage-specific dependencies and schema configuration. The blob upload **lifecycle** (`IBlobLifecycle`, `BlobLifecycleState`) stays in the neutral core and must stay S3-free; the upload **protocol** is a source/transport at the HTTP layer only — `Elarion.Blobs.AspNetCore` (direct upload) and `Elarion.Blobs.Tus` (tus) map onto `IBlobStore` and carry no storage dependency, and durable tus staging lives in its own provider package (`Elarion.Blobs.Tus.PostgreSql`). The S3 wire protocol (SigV4/`aws-chunked`/XML) is a deliberate non-goal.
- `Elarion.JsonRpc` owns the JSON-RPC and MCP **adapters** over the shared named bus: `JsonRpcDispatcher` (envelopes, telemetry, schema export, `Result`→`RpcError` via `AppErrorMapper` / default `IAppErrorTranslator<RpcError>`) and `McpDispatcher`, each serving the bus subset their `HandlerTransports` flag selects. It references `Elarion.Abstractions` but must stay ASP.NET-free. The named bus (`HandlerDispatcher`/`HandlerRoute`) and the dispatch-scope rail (`DispatchScopeContext`, `IDispatchScopeInitializer`, `CreateDispatchScope`/`SeedScope`) both live in `Elarion.Abstractions.Dispatch`, not here.
- `Elarion.AspNetCore` owns HTTP/JSON-RPC endpoint integration and ASP.NET Core-specific behavior. Keep JSON-RPC runtime contracts, telemetry, and schema export in `Elarion.JsonRpc`.
- `Elarion.AspNetCore.Mcp` owns MCP transport integration only; the MCP adapter (`McpDispatcher`) over the shared bus lives in `Elarion.JsonRpc`, and the `HandlerTransports` flag plus `[McpHandler]`/`[Handler]` attributes live in `Elarion.Abstractions`.
- Authorization must stay transport-neutral and ASP.NET-free: the attributes, `IAuthorizer`, `IAuthorizationPolicy`, and `AuthorizationDecorator` live in `Elarion.Abstractions`, and the default `ClaimsAuthorizer` in `Elarion` core. Do not couple the authorization decision path to `HttpContext` or ASP.NET's policy engine; named policies are Elarion-native `IAuthorizationPolicy` implementations evaluated against `ICurrentUser` + the request.
- Feature-flag gating follows the same seam/impl split and stays transport-neutral: the `[FeatureGate]` attribute, the `IFeatureFlagService` seam, and the `FeatureGateDecorator` live in `Elarion.Abstractions` (no OpenFeature/provider dependency); the OpenFeature-backed default lives in `Elarion.FeatureFlags.OpenFeature` and the Microsoft.FeatureManagement default in `Elarion.FeatureFlags.FeatureManagement`. Swapping providers must never touch a `[FeatureGate]`; do not bake a specific provider into core or Abstractions.
- `Elarion.AspNetCore.Identity` owns ASP.NET Core Identity **host wiring** only and is **optional**; the web-free Identity model (`[GenerateElarionIdentity]`, `ApplyElarionIdentity`) lives in `Elarion.EntityFrameworkCore.Identity`, so the assembly owning the `DbContext` composes Identity without the ASP.NET shared framework (the same EF-only ↔ web split as `Elarion.EntityFrameworkCore` ↔ `Elarion.AspNetCore`). The base authorization building blocks must not depend on either; authentication providers (Identity, Entra ID, any OIDC/JWT) only populate `ICurrentUser`. The app's `DbContext` composes Identity via `[GenerateElarionIdentity]` rather than inheriting `IdentityDbContext`.
- EF Core packages own only EF-specific marker APIs, pagination primitives, and source generation. Keep `Elarion.EntityFrameworkCore` dependency-free (markers only); EF Core-dependent runtime such as the pagination execution helpers belongs in `Elarion.Paging`, while provider-neutral pagination contracts (`Page<T>`, keyset/offset requests) stay in `Elarion.Abstractions`.
- **Seam contracts are designed for the most capable implementation, not the least.** Pre-1.0 backward
  compatibility and single-node/test-tier implementations never veto a seam parameter: a tier that cannot
  fully honor one implements the closest semantics (for example, non-durable where the durable tier persists)
  and documents the delta. Prefer an optional options bag (`XyzOptions? options = null`) over widening
  signatures repeatedly, so future options land additively.
- Prefer compile-time generation over runtime reflection scanning. Source generators should emit deterministic, inspectable code and fail with diagnostics for unsupported patterns.
- Preserve trimming and AOT friendliness on framework code paths. Avoid hidden runtime discovery and APIs that undermine linker safety.

## Source generator conventions

A generator runs on **every keystroke** in the IDE, so incrementality is correctness, not an
optimization. Follow these when adding or changing any generator in `Elarion.Generators` or
`Elarion.EntityFrameworkCore.Generators`. The full rationale and the rejected alternatives are in
[ADR-0006](docs/decisions/0006-incremental-source-generator-conventions.md); copy a reference generator
(`AppModuleDiscoveryGenerator`, `ElarionManifestGenerator`, `ModuleDefaultServicesGenerator`, or any of
the six registration generators), not the old "scan and emit" shape.

- **Discover through the syntax provider, never off `CompilationProvider`.** Use
  `context.SyntaxProvider.ForAttributeWithMetadataName(...)` for attribute triggers (one call handles an
  attribute on methods *and* types — branch on `ctx.TargetSymbol`), or a predicate-filtered
  `CreateSyntaxProvider` when the trigger is a base type (validators, handlers). **Never**
  `RegisterSourceOutput(context.CompilationProvider, …)` with a `foreach (compilation.SyntaxTrees)`
  scan — that re-binds every file on every edit with no caching.
- **Every pipeline value must be value-equatable.** `ImmutableArray<T>.Equals` is *reference* equality
  and silently kills the cache — use `EquatableArray<T>` for every collection field (nest it all the
  way down). Carry **strings** (FQNs via `SymbolDisplayFormat.FullyQualifiedFormat`), never `ISymbol`,
  `Compilation`, `SyntaxNode`, `Location`, or `object?[]` in a model.
- **Diagnostics are data.** Transforms stay pure (no `spc.ReportDiagnostic`): return
  `EquatableArray<DiagnosticInfo>` (built with `DiagnosticInfo.Create`, capturing `LocationInfo`) and
  report it (`diagnostic.ToDiagnostic()`) or compute cross-item diagnostics in the `RegisterSourceOutput`
  callback.
- **Reuse shared discovery.** Source modules from `ModuleProviders.CollectModules(context)` and match
  with `ModuleScanner.FindBest`/`IsInScope`; gate assembly opt-ins with `ModuleProviders.HasTrigger`
  (a projected `bool`). Do not hand-roll a `ModuleInfo` record, a module scan, or a namespace
  `StartsWith` matcher.
- **Output is a byte-identical contract, and caching is tested.** Keep generated text byte-identical
  across refactors (preserve every emit-time `OrderBy`/`Sort` — provider order is unspecified). Tag
  collect/combine nodes with `.WithTrackingName(…)` and add a
  `GeneratorCacheAssert.ReusesOutputsAfterIrrelevantEdit` test — it is the only check that catches a
  re-introduced non-equatable model. Run the generator's `*GeneratorTests.cs` after each change.
- **AOT/trim and cross-assembly.** Emit concrete, statically-typed code (no reflection/open generics in
  generated hot paths). For cross-assembly discovery emit/read assembly metadata rather than scanning
  referenced symbol trees — emit `[assembly: AssemblyMetadata(key, value)]` and read referenced assemblies
  via `context.MetadataReferencesProvider` (cached per reference; a source edit re-reads nothing).
  `ElarionManifest` (handlers/modules/RPC) and `EntityConfigurationManifest` (`[EntityConfiguration]` for
  DbContext cross-assembly discovery) are the two existing examples; `ElarionManifestReader` is the
  PE-metadata reader to copy.

## JSON-RPC model

JSON-RPC is a first-class optional transport:

1. Application handlers declare `[Handler("module.action")]` (the name is optional — when omitted the generator infers `{module}.{operation}` from the handler type name, see [HTTP endpoint model](#http-endpoint-model)/below; specify it explicitly for stable wire contracts). The `Transports` flag (`HandlerTransports.JsonRpc`/`Mcp`/`All`, default `All`) selects which name-routed transports expose the handler — JSON-RPC, MCP, or both. Request/response are read from the handler's `IHandler<TRequest, Result<TResponse>>` interface (success type unwrapped from `Result<T>`), so they may be nested or top-level; a handler with no resolvable shape reports `ELRPC002`.
2. `AppModuleDiscoveryGenerator` (triggered by `[GenerateModuleBootstrapper]`) emits the module-scoped, feature-flag-gated dispatcher registration code.
3. Hosts configure `JsonRpcDispatcher` with the same `JsonSerializerOptions` used at runtime.
4. `Elarion.JsonRpc.JsonRpcSchemaExporter` or `Elarion.AspNetCore.SchemaGeneration` exports `rpc-schema.json` from registered methods.
5. `elarion-jsonrpc-client-generator` emits `rpc-types.ts`, `rpc-schemas.ts`, and `rpc-client.ts`.
6. When a host opts into `[GenerateModuleBootstrapper]`, RPC registration is **module-scoped and feature-flag-gated** — see [Module-aware transport gating](#module-aware-transport-gating).

Generated TypeScript should remain portable across browser and Node.js server
runtimes. Prefer standard `fetch`, injectable transport, `AbortSignal`, and small
common dependencies such as Zod when they materially improve safety.

The named `HandlerDispatcher` bus is a **transport seam**, not an in-process call path: it exists so name-keyed
transports (JSON-RPC, MCP) can route a wire *string* to a handler. **In-process, always call handlers
typed-directly** — inject `IHandler<TRequest, Result<TResponse>>`, inject `IHandlerSender` and call
`SendAsync<,>` (the typed mediator send — resolves by type from the ambient scope/transaction; register with
`AddElarionHandlerSender`), use `HandlerInvoker.InvokeAsync<,>` (custom transports/jobs, fresh scope), or, across
modules, the typed `[GenerateModuleApi]` facade behind a `[ModuleContract]` — so a rename is a compile error, not
a runtime surprise. The bus *is* an injectable singleton and `DispatchAsync(name, …)` works, but it takes
`object`/returns `Result<object>` (no compile-time type, no schema), so use it from application code only when you
must dispatch by a dynamic/string name and cannot reference the handler's type (the mediator niche); prefer
`[ModuleContract]` for cross-module decoupling. The event bus is **pub/sub-only** — it has no request/reply (see
[Event / messaging model](#event--messaging-model)). See [Cross-module communication](#cross-module-communication).

## HTTP endpoint model

Plain HTTP/REST is a parallel first-class optional transport that maps the same handlers:

1. Application handlers declare `[HttpEndpoint("route")]` or `[HttpEndpoint(HttpVerb.X, "route")]`. The request/response are read from the handler's `IHandler<TRequest, Result<TResponse>>` interface (success type unwrapped from `Result<T>`), so they may be nested or top-level — nesting/naming carry no semantic weight. Verb precedence: an explicit verb wins; else the request's CQRS marker (`ICommand` → POST, `IQuery` → GET); else `ELHTTP004`. A handler with no resolvable shape reports `ELHTTP001`.
2. `AppModuleDiscoveryGenerator` (triggered by `[GenerateModuleBootstrapper]`) emits per-module `Map{Module}Http(this IEndpointRouteBuilder)` extension-method bodies of strongly-typed minimal-API registrations (one `MapGet`/`MapPost`/... per handler), aggregated and feature-gated by the `endpoints.MapElarion(configuration)` extension method.
3. Each emitted lambda binds the request (`[AsParameters]` for GET/DELETE and opt-in custom-bound requests; JSON body for default POST/PUT/PATCH), **resolves the handler typed-directly** (`[FromServices] IHandler<TRequest, Result<TResponse>>` — HTTP does **not** go through the named bus, since the route already pins the type), and translates `Result<T>` via `ElarionHttpResults` — `200`/`204` on success, RFC 7807 ProblemDetails on failure with the status from `HttpAppErrorMapper`.

Keep the generator emitting concrete-typed lambdas (no open generics, no reflection
binding) so output stays AOT/RDG friendly. The `[HttpEndpoint]` attribute lives in
`Elarion.Abstractions` and must carry no ASP.NET dependency; per-property binding
customization (`[FromRoute]`/`[FromHeader]`/`[FromForm]`/`IFormFile`, etc.) is the
consuming DTO's opt-in via `Microsoft.AspNetCore.Http.Abstractions`, detected by the
generator structurally through the `Microsoft.AspNetCore.Http.Metadata.IFrom*Metadata`
interfaces and `IFormFile` types. When a host opts into `[GenerateModuleBootstrapper]`,
HTTP mapping is **module-scoped and feature-flag-gated** — see
[Module-aware transport gating](#module-aware-transport-gating).

## MCP model

MCP is a parallel first-class optional transport, a peer of JSON-RPC and HTTP, served by `Elarion.AspNetCore.Mcp`:

1. A handler opts into MCP via its `[Handler(Transports = ...)]` flag (default `All` includes MCP). `[McpHandler(ToolName = ...)]` customizes only the tool name — it carries no enable/disable flag (use `Transports` for that).
2. `AppModuleDiscoveryGenerator` emits the gated `dispatcher.RegisterHandlers(configuration)` (which builds the single shared `HandlerDispatcher` with every operation's flags) and `configuration.GetMcpMetadata()` (the reflection-free tool table).
3. MCP is an **adapter over the shared bus**: `McpDispatcher` (in `Elarion.JsonRpc`) wraps the same `HandlerDispatcher` and serves only the operations flagged `HandlerTransports.Mcp`. An MCP-only handler is therefore genuinely absent from `/rpc` and the exported JSON-RPC schema; a JSON-RPC-only handler is never dispatchable as a tool; a "both" handler is reachable from either surface — all from one registry.
4. Hosts call `AddElarionMcp(metadata, serializerOptions, registerHandlers, configure)` and `MapElarionMcp()`; `MapJsonRpc()` is never required. The same `RegisterHandlers` delegate is passed to both `AddElarionJsonRpc` and `AddElarionMcp`, and the bus is built once (shared). When a host opts into `[GenerateModuleBootstrapper]`, MCP registration is **module-scoped and feature-flag-gated** — see [Module-aware transport gating](#module-aware-transport-gating).

REST stays a separate opt-in via `[HttpEndpoint]` (it needs route/verb/param binding that don't fit a flags enum); JSON-RPC and MCP share the single define-once `[Handler]` identity and differ only by the `Transports` flag.

## JSON serialization model

Every subsystem that (de)serializes JSON — JSON-RPC, MCP, idempotency, caching, the outbox, settings — reads
**one canonical `JsonSerializerOptions`** through the `IElarionJsonSerialization` accessor, so a host configures
the JSON type context once instead of threading options into each subsystem. See
[ADR-0023](docs/decisions/0023-canonical-json-serialization.md).

1. **`ElarionJsonOptions`** (`Elarion.Abstractions.Serialization`) is the composed configuration: naming/knobs
   (`PropertyNamingPolicy`, `PropertyNameCaseInsensitive`, `DefaultIgnoreCondition`), an **ordered**
   `TypeInfoResolvers` list, an `EnableReflectionFallback` flag, and a `PostConfigure` escape hatch. It is a plain
   mutable bag — the abstractions/core packages carry **no `Microsoft.Extensions.Options` dependency**.
2. **`IElarionJsonSerialization`** (accessor + default impl both in `Elarion.Abstractions.Serialization`, so the
   leaf subsystem packages that reference only Abstractions can register and read it) materializes a single
   `JsonSerializerOptions` from that config, **freezes it** (`MakeReadOnly()`) on first access, and exposes
   `Options`, `GetTypeInfo<T>()`, and `GetTypeInfo(Type)`. Subsystems depend on this accessor — **never on a bare
   `JsonSerializerOptions` in DI** — so Elarion never collides with a host's own `JsonSerializerOptions`
   registration (ASP.NET Core / MVC).
3. **Composition is a contributor seam**, not the options pattern: `AddElarionJson()` registers the accessor
   (idempotent, called by each subsystem's `Add…`); `ConfigureElarionJson(o => …)` accumulates a contribution.
   Contributions apply in registration order when the options first materialize — transports insert their
   envelope context first (`TypeInfoResolvers.Insert(0, …)`), the generated `AddElarion(configuration)` adds the
   module contexts from `GetAllJsonTypeInfoResolvers()`, the host adds extras. Resolver order is
   **first-match-wins**; the host-priority `OverrideTypeInfoResolvers` segment composes ahead of every
   `TypeInfoResolvers` entry, so a host can override even a type a transport envelope context registers.
4. **AOT-strict by default:** no reflection tail is added unless `EnableReflectionFallback` is set, matching the
   repo-wide `JsonSerializerIsReflectionEnabledByDefault=false`. A type missing from every source-generated
   context throws at runtime (surfacing a missing `[JsonSerializable]`) rather than silently reflecting. The
   reflection fallback isolates its `DefaultJsonTypeInfoResolver` in a suppressed helper so core stays
   `IsAotCompatible`.

## Authorization model

Authorization is **declarative, transport-neutral, and independent of the authentication provider** — it
runs in the handler pipeline, so the same rules apply under JSON-RPC, MCP, and HTTP. See
[ADR-0009](docs/decisions/0009-authorization-building-blocks.md) and
[the authorization concept doc](docs/concepts/authorization.mdx).

1. A handler declares requirements with class-level attributes: `[RequirePermission(resource, verb)]` — the
   Kubernetes-RBAC `(resource, verb)` shape, sugar for a claim of the configured `PermissionClaimType` (default
   `"permission"`) composed as `{resource}.{verb}` — `[RequireRole]`, the general
   `[RequireClaim(type, values…)]` (values OR; empty = presence), `[RequirePolicy("name")]` (a named
   `IAuthorizationPolicy`), and `[AllowAnonymous]`. Different attribute kinds AND; multiple of one kind AND;
   OR lives inside one `[RequireClaim]`.
2. `HandlerRegistrationGenerator` auto-attaches `AuthorizationDecorator` as the outermost functional gate
   (just inside tracing) when a handler carries a `Require*`/`[RequirePolicy]` attribute. An assembly- or
   `[AppModule]`-scoped `[ElarionAuthorizationDefaults]` flips to **deny-by-default** (every in-scope handler
   requires authentication unless `[AllowAnonymous]`, resolved most-specific-wins like `[DefaultPipeline]`).
   The decorator reads requirements through `HandlerMetadata` (never `inner.GetType()`) and is guarded by
   `IResultFailureFactory<TResponse>` (else `ELAUTH001`).
3. The decorator calls `IAuthorizer.AuthorizeAsync(requirements, request, ct)`. The default `ClaimsAuthorizer`
   (`Elarion` core, `AddElarionAuthorization`) evaluates everything against `ICurrentUser` + registered
   `IAuthorizationPolicy` instances — **no ASP.NET dependency**, with the handler request as the policy
   resource. An unauthenticated caller → `AppError.Unauthorized` (401); authenticated-but-denied →
   `AppError.Forbidden` (403). A named policy is an `IAuthorizationPolicy` marked `[AuthorizationPolicy("name")]`
   (auto-registered per module like `[Service]`, `ELPOL001`/`ELPOL002`) or registered manually via
   `AddElarionAuthorizationPolicy`.
4. Authentication is a host concern that only populates `ICurrentUser` (ASP.NET Identity via the optional
   `Elarion.AspNetCore.Identity`, or Entra ID / any OIDC-JWT via `AddElarionCurrentUser` claim mapping). The
   base authorization path stays in `Elarion.Abstractions` + `Elarion` with no ASP.NET coupling.
5. Every `[RequirePermission(resource, verb)]`/`[RequireRole]` is surfaced as a catalog so seeding and
   role→permission policy enumerate the full set instead of a hand-kept `Permissions.All` list (zero central
   edits per guarded handler). The permission models the Kubernetes-RBAC `(resource, verb)` shape — the enforced
   claim is composed `{resource}.{verb}` (the attribute exposes that composed `Permission`, so the authorizer
   path is unchanged), and the verb vocabulary is open (the `Verbs` constants or any string).
   `PermissionCatalogGenerator` emits two surfaces: the **compile-time** `ElarionPermissions` static (in the
   assembly's root namespace — a non-module namespace, so static role policy referencing it never trips
   `ELMOD002`) with `All`/`Roles`/`ByModule`/`ByResource`/`ByVerb` and typed accessors
   (`ElarionPermissions.Properties.Read`), aggregated cross-assembly from the Elarion manifest; and the
   **runtime** `IPermissionCatalog` (`Elarion.Abstractions.Authorization`, registered by
   `AddElarionAuthorization`) aggregating one `PermissionCatalogModule` per module through the module's gated
   `ConfigureDefaultServices` (so it spans referenced module assemblies and a disabled module contributes
   nothing). Role policy reads like K8s rules — `ByResource["properties"]` (all verbs on a resource),
   `ByVerb["read"]` (a verb across resources). Trigger `[UseElarion]` / `[GeneratePermissionCatalog]`; a guarded
   handler under no module → `ELPERM001`, a colliding typed accessor → `ELPERM002`.

## Feature flag model

Feature flags gate a **handler** at run time and are **declarative, transport-neutral, and
provider-agnostic** — the same shape as authorization, and distinct from compile-time module gating
(`Modules:{Name}:Enabled`, which removes a whole module). See
[ADR-0016](docs/decisions/0016-feature-flag-gating.md) and
[the feature-flags concept doc](docs/concepts/feature-flags.mdx).

1. A handler declares `[FeatureGate("name")]` (class-level, `AllowMultiple`, shaped like MVC `[FeatureGate]`):
   one or more feature names, an optional `FeatureRequirement` (`All` default / `Any`), and an optional
   `Negate` (gate on a feature being *disabled*). Stacked attributes AND; within one attribute `All`/`Any`
   combine the names.
2. `HandlerRegistrationGenerator` auto-attaches `FeatureGateDecorator<TRequest, TResponse>` **just inside the
   authorization gate** (authorization stays the outermost functional gate, so a disabled feature is never
   revealed to an unauthenticated caller and never touches caching/pipeline/handler). It validates the
   response implements `IResultFailureFactory<TResponse>` (else `ELFEAT001`, like ELAUTH001) and warns on a
   gate with no feature name (`ELFEAT002`). A closed gate returns `AppError.NotFound` with a **generic**
   message — echoing the feature name would leak the thing the 404 hides. The decorator reads gates off the
   concrete handler type via `HandlerMetadata` (cached per type), never `inner.GetType()`.
3. The decorator (and imperative app code) call the boolean `IFeatureFlagService.IsEnabledAsync(name, ct)`
   seam in `Elarion.Abstractions` (no provider dependency). Targeting is **ambient**: the default provider
   derives the evaluation context from `ICurrentUser`, so percentage/targeting rollouts work off-HTTP.
4. The default `IFeatureFlagService` targets **OpenFeature** (the vendor-neutral standard), so `[FeatureGate]`
   works against any OpenFeature provider. `Elarion.FeatureFlags.OpenFeature` (`AddElarionOpenFeature`, bring your own
   provider) is the base; `Elarion.FeatureFlags.FeatureManagement` (`AddElarionFeatureManagement(config)`) is the
   batteries-included Microsoft.FeatureManagement default over it. Replace the backend wholesale by
   registering a different `IFeatureFlagService` — the gate and all imperative checks follow, with no
   `[FeatureGate]` change. The seam, attribute, and decorator must stay free of any provider dependency.
5. **Variant service injection** (ADR-0019) ships a different *implementation* per allocated variant. The
   consuming handler is transparent (injects the contract like any service); only the implementations carry
   `[FeatureVariant("feature", Variant="x")]`. Because variant selection is an async flag evaluation
   but constructor injection is synchronous, the handler generator registers any handler whose constructor
   depends on a variant contract behind the general **`AsyncResolvedHandler`** proxy (`Elarion.Abstractions.Pipeline`),
   which defers pipeline construction to the first (async) `HandleAsync`, warms the variant into a per-scope
   `VariantResolutionCache`, then DI-constructs normally — opt-in, so non-variant handlers are byte-for-byte
   unchanged and no transport/HTTP path is touched. The variant accessor is the sibling seam
   `IFeatureVariantService.GetVariantAsync` (boolean seam stays frozen); `VariantServiceRegistrationGenerator`
   emits keyed impls + binding + the transparent contract factory + the imperative `IVariantServiceProvider<T>`
   escape hatch (`ELVAR001`, `ELVAR003`–`ELVAR007`). `[FeatureVariant]` is a **modifier on `[Service]`**, not a
   separate registration: the impl must also carry `[Service]` (which declares the service, its contract(s), and
   lifetime; a `[FeatureVariant]` without `[Service]` is `ELVAR007`), and `ModuleServiceRegistrationGenerator` skips
   the plain registration for a `[FeatureVariant]` class so the keyed + transparent registration is the only one. The
   contract(s) are **not repeated** on the attribute — they come from the `[Service]` via the shared
   `ServiceContractResolver` (implemented interfaces, or explicit `[Service(typeof(IX))]`), so a service registering
   under several interfaces is variant-resolved on each. So `[Service]` stays the one consistent way to register a
   service — the modifier just makes it variant-keyed. The recommended way to vary
   handler behaviour by variant is a variant **strategy service**, not swapping the whole handler (handlers are
   selected by request type, so a handler is never itself a variant). Detection is
   same-compilation (direct ctor deps); transitive/cross-assembly variants use the imperative provider. **Requires
   an OpenFeature provider that surfaces the variant name** (spec §1.4.6) — native providers do; the preview
   Microsoft.FeatureManagement provider does not yet, so variant injection is unavailable through the
   `AddElarionFeatureManagement` default (boolean gating is unaffected).

## Module-aware transport gating

All three transports become **module-scoped and feature-flag-gated** when a host opts in with
`[assembly: GenerateModuleBootstrapper]` — an assembly trigger that emits the fixed-name `ElarionBootstrapper`
static into the host's root namespace (framework-owned for cross-project consistency, [ADR-0018](docs/decisions/0018-generated-infrastructure-is-framework-named.md); you never declare a partial).
`AppModuleDiscoveryGenerator` associates each `[Handler]`/`[HttpEndpoint]`
handler with a module by longest-prefix namespace match and emits, on the generated `ElarionBootstrapper`:

- per-module **extension methods** `Map{Module}Http(this IEndpointRouteBuilder)`, `Add{Module}Handlers(this HandlerDispatcher)` (maps the module's `[Handler]` operations with their transport flags), and the parameterless `Get{Module}McpMetadata()`;
- aggregate **extension methods** `services.AddElarion(configuration)`, `endpoints.MapElarion(configuration)` (module `MapEndpoints` + `[HttpEndpoint]`), `dispatcher.RegisterHandlers(configuration)` (builds the single shared `HandlerDispatcher`), `configuration.GetMcpMetadata()`, `configuration.GetAllJsonTypeInfoResolvers()`, and `configuration.IsModuleEnabled(name)`.

The single registry carries each operation's `Transports` flag, so the JSON-RPC adapter serves only
JSON-RPC-surfaced operations and the MCP adapter only MCP-surfaced ones. Core modules map
unconditionally; feature modules are gated by `Modules:{Name}:Enabled` (via `IsModuleEnabled`), so a disabled
module disappears across services, validators, `MapEndpoints`, `[HttpEndpoint]`, JSON-RPC, and MCP. Handlers
whose namespace is under no module emit a warning (`ELHTTP003`/`ELRPC001`; MCP reuses `ELRPC001` since it is
built on `[Handler]`) and are mapped ungated so they are never silently dropped.

`[GenerateModuleBootstrapper]` is the **single transport-wiring path** — there is no flat, ungated map. A host
that conceptually has "no modules" declares one core `[AppModule]` (core modules map unconditionally). Schema
export reads the gated `RegisterHandlers` (filtered to the JSON-RPC surface); feature modules default to enabled,
so the exported schema is the complete contract unless a module is explicitly disabled for the schema build.

A module owns the route group / authorization policy / conventions for its generated `[HttpEndpoint]` routes via
an optional static `ConfigureEndpointGroup(IEndpointRouteBuilder) → IEndpointRouteBuilder` hook on the
`[AppModule]` type (detected structurally like `MapEndpoints`). `MapElarion` maps that module's `MapEndpoints`
hook and generated routes onto the builder it returns; absent the hook, they map onto the root. There are no
per-module URL prefixes by default — each `[HttpEndpoint]` carries its full route template and modules share a
flat URL space; a conventions-only group (`MapGroup("")`) adds policy without a prefix, while `MapGroup("/x")`
opts the whole module into a prefix. The generator never reads `[Authorize]`/`[AllowAnonymous]` from handlers.

Per-endpoint authorization/conventions are the host's job, via the per-module extension method on a configured group
(e.g. `app.MapGroup("/billing").RequireAuthorization(policy).MapBillingHttp()`) plus the module
`MapEndpoints` escape hatch — the generator never reads `[Authorize]`/`[AllowAnonymous]`. RPC/MCP gating needs
`IConfiguration` at registry-compose time, so `AddElarionHandlerDispatcher`/`AddElarionJsonRpcDispatcher`
(transport-neutral, via `IServiceProvider`) and `AddJsonRpc`/`AddElarionMcp` (ASP.NET, via `IConfiguration`)
expose config-aware registration overloads, used as `AddJsonRpc(serializerOptions, ElarionBootstrapper.RegisterHandlers)`
and `AddElarionMcp(config.GetMcpMetadata(), serializerOptions, ElarionBootstrapper.RegisterHandlers, configure)`
(the same `RegisterHandlers` delegate is passed to both as a method group, so the shared bus is built once).

## Event / messaging model

The event bus is an in-process eventing subsystem whose API is organized around its
**relationship to the database transaction**, not around a verb. There are two planes,
each its own interface in `Elarion.Abstractions.Messaging`; see
[`docs/decisions/0001-event-transaction-phase.md`](docs/decisions/0001-event-transaction-phase.md)
for the full rationale.

1. **Plane A — domain events (`IDomainEventBus`).** In-process notifications dispatched
   **inline within the caller's DI scope**, and therefore within the caller's transaction:
   consumers share the scoped `DbContext`, their writes commit atomically with the command,
   and a consumer failure fails the command. `PublishAsync<TEvent> where TEvent : IDomainEvent`
   fans out to every consumer in ascending `[ConsumeEvent(Order = …)]`, aggregating failures.
   Pub/sub only — never broker-portable.
2. **Plane B — integration events (`IIntegrationEventBus`).** Notifications **recorded within
   the caller's unit of work and delivered after the transaction commits**, on a separate
   scope, retried independently; a consumer failure never fails the command, and a rollback
   discards the event. `PublishAsync<TEvent> where TEvent : IIntegrationEvent`. This is the
   **only broker-portable plane** — an outbox or message-broker backend implements only this
   interface. Two backends ship: the in-memory tier in `Elarion.Messaging.InMemory`
   (best-effort, lost on crash) and the durable EF Core transactional outbox in
   `Elarion.Messaging.Outbox`
   (at-least-once; records the event in the same transaction and delivers after commit via a
   polling worker). The durable tier needs no per-scope buffer — the database transaction
   provides the commit-gating the in-memory scope buffer otherwise supplies.

Marker interfaces `IDomainEvent` / `IIntegrationEvent` bind each event to exactly one plane;
the generator rejects a type carrying both. `[ConsumeEvent]` applies in two forms, and the
**handler form is the preferred way** — a consumer is a first-class unit of business logic with
the full decorator pipeline, exactly like a command/query handler. The consumer is a class
implementing `IHandler<TEvent, Result<T>>` (or the `IHandler<TEvent>` sugar) whose request type
*is* the event, annotated with a class-level `[ConsumeEvent]`. It is dispatched through its full
decorator pipeline (tracing, resilience, validation, cache-invalidation) — the generated
descriptor resolves the `IHandler<,>` interface from DI so the decorated chain runs. Every
consumer is a **fan-out subscriber**: `Result<Unit>` (or the `IHandler<TEvent>` sugar) whose failed
`Result` surfaces as an `EventConsumerFailedException` (each backend handles it per its plane —
domain aggregates and rethrows, failing the command; the in-memory integration pump logs and
isolates; the outbox retries). The event bus is **pub/sub-only** (see
[ADR-0010](docs/decisions/0010-event-bus-is-pub-sub-only.md)) — a non-`Unit` `Result<T>` response is
request/reply, not a notification, and is rejected (`ELEVT005` handler-form, `ELEVT002` method-form);
for a typed reply, call a handler **by type** with `IHandlerSender`/`IHandler` (see
[the handler-call guidance](docs/concepts/handlers.mdx#calling-a-handler-from-other-code)). A
class-level `[ConsumeEvent]` on a non-handler is reported (`ELEVT005`).

Because the domain plane dispatches **inline in the publisher's scope**, a domain-event handler's
decorator pipeline runs **nested within the command's** (same scope, `DbContext`, and transaction), so
a domain consumer must not open its own transaction or resilience scope. The recommended way is a
transaction decorator that declares a `static bool AppliesTo(HandlerMetadata handler)` predicate matching only
`ICommand`/`IIntegrationEvent` requests (see [ADR-0003](docs/decisions/0003-decorator-attachment-predicates.md)
and [decorator pipelines](docs/concepts/decorator-pipelines.mdx)): the predicate then never attaches it
to a domain-event handler, while integration consumers run on a **fresh post-commit scope** and
correctly take the transaction (their request is an `IIntegrationEvent`). Generic `where` constraints
are evaluated at compile time by `HandlerRegistrationGenerator`; the `AppliesTo` predicate is a **runtime
call** the generated registration evaluates once per closed handler type (cached in a generated
`static readonly bool`), never per request.

The **method form** is a lightweight alternative for a small side effect on an existing
`[Service]`: the consumer is an instance method on a `[Service]` class (no decorator pipeline);
the message parameter's marker selects the plane. It is a fan-out subscriber, returning
`void`/`Task`/`ValueTask` (throw to fail) or the **non-generic `Result`**/`Task<Result>`/`ValueTask<Result>`
(a failed `Result` → `EventConsumerFailedException`, the same failure channel as the handler form) — a
`Result<T>` with a value is request/reply and is rejected. Its optional `IEventContext`/`IEventContext<TEvent>`
and `CancellationToken` parameters are supplied by the runtime.

`EventConsumerRegistrationGenerator` (triggered by `[GenerateEventConsumers]` or
`[UseElarion]`) discovers consumers, validates signatures (diagnostics `ELEVT001`/`ELEVT002`/
`ELEVT005`), and emits a per-module `Add{Module}EventConsumers(IServiceCollection)` (longest-prefix
namespace match) that registers each consumer service plus an `EventSubscriptionDescriptor`, wired
into that module's `ConfigureDefaultServices` — see
[Module default services](#module-default-services). The in-memory domain tier in `Elarion/Messaging`
is wired by `AddElarionDomainEventBus`: `InMemoryDomainEventBus` dispatches Plane A inline. The
in-memory integration tier lives in `Elarion.Messaging.InMemory`, wired by
`AddElarionInMemoryIntegrationEventBus<TContext>` (or `AddElarionInMemoryEventBus<TContext>` for both): `InMemoryIntegrationEventBus`
buffers Plane B into the internal scoped `EventDispatchScope`, whose `FlushAsync`/`Discard` are driven
after commit/on rollback by the package's EF Core interceptors, handing events to the
`EventDispatchPump` (a hosted `BackgroundService` with a bounded channel) for after-commit delivery. The
`<TContext>` overload auto-attaches those interceptors to the app's context via an
`IDbContextOptionsConfiguration<TContext>` (so a plain `AddDbContext<TContext>` suffices); a low-level
non-generic `AddElarionInMemoryIntegrationEventBus` registers the building blocks without attaching them, for
hosts that wire the interceptors by hand.

The in-memory **scheduler** registration is symmetric: `SchedulerRegistrationGenerator` emits a
per-module `Add{Module}ScheduledJobs` wired into `ConfigureDefaultServices`, plus the assembly-level
typed job-references type. So under `[GenerateModuleBootstrapper]` both scheduled jobs and event
consumers are **module-feature-gated** (a disabled module registers neither). Like handlers, services,
and validators, jobs and consumers are module-scoped only — there is no flat assembly-wide registration
method; a job or consumer whose namespace falls under no module is reported (`ELSG010`/`ELEVT003`) and
left unregistered.

## Module default services

When a host opts into `[GenerateModuleBootstrapper]`, each module's discovered **handlers, services,
validators, scheduled jobs, and event consumers** are registered automatically and gated, without the
module author wiring them by hand. The mechanism is a cross-generator partial-method aggregation:

- `ModuleDefaultServicesGenerator` emits, per `[AppModule]`, a sibling
  `public static partial class {ModuleType}ElarionModuleServices` with
  `ConfigureDefaultServices(IServiceCollection)` calling eight `static partial void` hooks
  (`AddHandlers`/`AddServices`/`AddValidators`/`AddScheduledJobs`/`AddEventConsumers`/`AddAuthorizationPolicies`/`AddPermissions`/`AddModuleApi`). Its
  fully-qualified name is exactly the module's `TypeFqn + "ElarionModuleServices"`, so the host needs
  no extra manifest metadata to call it.
- Each category generator contributes a filler partial implementing its hook (calling the existing
  per-module `Add{Module}…`). Unimplemented hooks elide to no-ops, so a module that uses only some
  categories costs nothing for the rest. All generators ship in `Elarion.Generators` and so always run
  together; isolated generator unit tests must run `ModuleDefaultServicesGenerator` alongside.
- `AddElarion` in the bootstrapper calls `{Module}.ConfigureDefaultServices(services)` gated
  by `IsModuleEnabled`, **before** the module's optional hand-written `ConfigureServices` (now reserved
  for non-generated registrations). For a referenced module whose assembly predates the skeleton
  generator, the host omits the call (it probes for the public sibling via `GetTypeByMetadataName`);
  current-compilation modules always emit it since the skeleton runs in the same pass.

## Cross-module communication

Direct, synchronous module-to-module calls (the in-process analog of a gRPC call) go through a
**published contract**, not through another module's internals or a direct typed handler call
(`IHandlerSender`/`IHandler` couple you to the other module's types and don't survive extraction). See
[ADR-0002](docs/decisions/0002-cross-module-communication.md). The framework owns the convention and
the analyzer; mapping between a contract's DTOs and a module's handler DTOs is the **module's concern**
(hand-written or any mapper) — there is no generated forwarder and no mapper dependency.

- **`[ModuleContract]`** marks an interface (or class) as a module's published cross-module surface.
  The owning module keeps the implementation `internal` and registers it (commonly a thin `[Service]`
  adapter that maps to, and forwards to, the module's handlers); other modules inject the contract. This
  applies to **every** module, including `Kind = Core` foundation modules — there is no core exemption
  (the analyzer reads only the module name, never `Kind`), so a module's cross-module surface is always
  explicit: a `[ModuleContract]`, or a platform-capability port that lives outside the modules.
- **`ModuleBoundaryAnalyzer` (`ELMOD002`, warning)** is purely **location-based**: everything under an
  `[AppModule]` is module-internal and may not be referenced from another module except through a published
  `[ModuleContract]`; everything **outside** every module is shareable. It reports a cross-module dependency
  on another module's internal type — an entity, DTO, `[Service]`, handler, or `[EntityConfiguration]`
  placed inside the module — and inspects only the dependency surface (constructor parameters, fields,
  properties) to stay precise. Resolve a flag one of three ways: a `[ModuleContract]` for a genuine,
  sparingly-used cross-module *domain* call; a platform-capability **port** outside the modules with its
  adapter in infrastructure (the port/adapter pattern, like `IEmailSender`); or move shared data/value
  types to the **shared kernel** (under no module). A shared-kernel entity is shareable because it lives
  *outside* a module — not because entities are special; placing an entity inside a module makes it
  module-owned (how a mini bounded context earns data ownership — see ADR-0008). This is the repo's first
  `DiagnosticAnalyzer`; new analyzer diagnostics must be tracked in `AnalyzerReleases.Unshipped.md` (RS2008).
- **`[GenerateModuleApi]` (optional ergonomic layer)** generates a typed in-process API over a module's
  own handlers so intra-module code (notably a contract implementation) can call handlers by name. It is
  **not a transport** — it dispatches typed-direct to the decorated `IHandler<,>` (full pipeline), crosses
  no serialization boundary, and is absent from the JSON-RPC/MCP schema. Because its methods expose
  handler DTOs it is module-internal and must not cross a boundary. `ModuleApiGenerator` emits the method
  declarations (one per handler, named after the handler type), an `internal` forwarder, and a DI
  registration wired into the module's gated `ConfigureDefaultServices` (the `AddModuleApi` hook).
- **Membership mirrors `[EntityConfiguration]`/`[GenerateDbSets]`** with the same scope vocabulary, but **opt-out**
  by default (handlers are structural, so every non-excluded handler is in the module's default facade).
  `[ModuleApi]` is a pure configurator: `Exclude = true` removes a handler from every facade; scope tags
  place it on the matching `[GenerateModuleApi("scope")]` facades (additively — it stays in the default
  facade). A default `[GenerateModuleApi]` facade includes every non-excluded handler in the owning
  module (longest-prefix namespace match); a scoped facade includes handlers whose tags intersect, and is
  the ISP-friendly way to expose a narrow surface. Diagnostics: `ELAPI001` (must be partial), `ELAPI002`
  (must be top-level), `ELAPI003` (not under any module — warning), `ELAPI004` (duplicate method name).

## TypeScript client generator

The npm package lives in `src/elarion-jsonrpc-client-generator`.

- Keep generated output deterministic.
- Keep the generated direct API ergonomic, for example `rpc.clients.get(params, options)`.
- Keep the generic transport primitive available for advanced adapters.
- Preserve tuple-aware batch typing through generated `$request` helpers and `$batch`.
- Runtime validation should use generated Zod schemas by default, with opt-outs or transform hooks for consumers that need them.
- Generated code must type-check under modern browser projects and NodeNext projects with Node fetch types.
- Do not import React, TanStack, Vite, or any downstream framework from generated runtime code.

## C# coding standards

Applies to `**/*.cs`. Copilot scopes this section via
`.github/instructions/csharp.instructions.md`; other agents should apply it when
editing C#.

### Style

- Always use the latest C# version supported by the repo, currently C# 14 features.
- All classes should be declared `sealed` unless intentionally designed for inheritance. When a class is intentionally extensible, add XML docs explaining the inheritance contract.
- Prefer immutable records for DTOs, options, and other data containers.
- Prefer nominal, property-based records for public DTOs and API models. Use `required` + `init` for non-nullable members and nullable `init` properties for optional values. Positional records are acceptable for tiny internal helpers or tests when they are clearly more readable.
- Prefer read-only collection types (`IReadOnlyList<T>`, `IReadOnlyCollection<T>`, `ImmutableArray<T>`, etc.) for immutable public surfaces.
- Prefer primary constructors for DI-oriented services when they keep the type concise and readable.

### Naming

- PascalCase for type names, method names, and public members.
- Private instance fields use `_camelCase`.
- Local variables and parameters use `camelCase` without underscores.
- Static readonly fields and constants use PascalCase.
- Prefix interfaces with `I`; prefix type parameters with `T`.

### Formatting

- Follow `.editorconfig` as the formatting source of truth.
- Prefer file-scoped namespaces and single-line using directives.
- Keep opening braces on the same line.
- Prefer early returns over deep nesting.
- Use pattern matching, switch expressions, and `nameof` where they improve clarity.
- Ensure the final return statement of a method is on its own line.

### Comments and public API docs

- Add XML doc comments for public APIs. Include `<example>` and `<code>` where usage is non-obvious.
- Add regular code comments only to clarify intent, constraints, or non-obvious tradeoffs. Do not comment every function or restate obvious code.
- When a public API uses a non-obvious pattern for compatibility, source generation, AOT, or performance reasons, document why.

### Nullable reference types

- Declare values non-nullable by default and validate nullability at entry points.
- Use `is null` / `is not null` instead of `== null` / `!= null`.
- Trust the type system; avoid redundant null checks when annotations already guarantee non-null values.

### Async and background work

- Never introduce unobserved fire-and-forget tasks.
- Pass cancellation tokens through async flows; do not use `CancellationToken.None` unless there is a documented, deliberate reason.
- Long-lived or background work must be owned by a host-managed abstraction (`IHostedService`, scheduler service, explicit queue/loop abstraction, etc.), not hidden inside helper methods.
- Handle `OperationCanceledException` deliberately; do not log expected cancellation as an error.

## Testing

- Always add regression tests when fixing bugs.
- Follow nearby test naming and capitalization conventions.
- Do not emit `Arrange`, `Act`, or `Assert` comments.
- Keep tests deterministic; avoid timing-sensitive behavior unless the test specifically verifies concurrency or scheduling.
- For source generator changes, add or update generator tests and keep emitted output deterministic and inspectable.
- Database-backed integration tests use Testcontainers and a Docker-gated fixture (for example `PostgreSqlBlobStoreFixture`): they spin up a real container when Docker is available and **skip** (never fail) when it is not, so `dotnet test` stays green on machines without Docker. Tag them `[Trait("Category", "Integration")]`.

## Development and validation

```bash
dotnet restore Elarion.slnx
dotnet build Elarion.slnx --configuration Release
dotnet test --project tests/Elarion.Tests/Elarion.Tests.csproj --configuration Release
dotnet pack Elarion.slnx --configuration Release --no-build

cd src/elarion-jsonrpc-client-generator
npm ci
npm run build
npm test
npm pack --dry-run
```

When changing the TypeScript generator, also generate from a representative
`rpc-schema.json` and type-check the emitted `rpc-types.ts`, `rpc-schemas.ts`, and
`rpc-client.ts` under `moduleResolution: NodeNext`.

## Documentation website

The marketing landing page and rendered documentation live in `website/` — a
Next.js + [Fumadocs](https://fumadocs.dev) app statically exported to GitHub
Pages. The MDX/JSON content is **not** duplicated there: `website/source.config.ts`
points Fumadocs at the repository's top-level `docs/` folder, which stays the
single source of truth. `next.config.mjs` sets `turbopack.root`/`outputFileTracingRoot`
to the repo root so the bundler can resolve `../docs`.

Static assets follow the same rule: `docs/public/` is the single home for doc and
brand assets (it lives with the content and is also used by the root `README.md`).
`website/scripts/sync-public-assets.mjs` (run by the `predev`/`prebuild`/`postinstall`
hooks) mirrors `docs/public/` into `website/public/` and derives the favicon
(`website/app/icon.svg`) from the canonical brand symbol. Those generated outputs are
gitignored — only `website/public/CNAME` (the custom domain) is committed. Put new
images in `docs/public/`, never in `website/public/`.

```bash
cd website
npm install
npm run dev          # http://localhost:3000
npm run build        # static export to website/out
npm start            # preview the export locally
```

The site is served at the custom domain `elarion.wimmesberger.dev` (pinned by
`website/public/CNAME`, copied into the export), so it builds with **no base
path**. For a GitHub Pages *project* URL instead
(`https://swimmesberger.github.io/Elarion/`), remove the CNAME and build with
`PAGES_BASE_PATH="/Elarion"`. Pushes to `main` that touch `website/**` or
`docs/**` trigger `.github/workflows/deploy-docs.yml`, which builds the static
export and publishes it to Pages (enable once via **Settings → Pages → Source →
GitHub Actions**).

When adding a doc page, drop the `.mdx` under `docs/` and list it in the relevant
`meta.json`. If a page or any sidebar uses Fumadocs MDX components beyond
`Card`/`Cards`/`Callout`/`Step`/`Steps`, register them in `website/components/mdx.tsx`;
new `icon:` frontmatter values must be valid Lucide icon names (resolved in
`website/lib/source.ts`).

## Publishing

Publishing uses GitHub Actions trusted publishing/OIDC. `<VersionPrefix>` in
`Directory.Build.props` is the single source of truth for the *next* version; see
[RELEASING.md](RELEASING.md) for the full flow.

- NuGet publishing uses `NuGet/login` and the `NUGET_USER` repository variable or secret.
- npm publishing uses trusted publishers for `@swimmesberger/elarion-jsonrpc-client-generator`.
- Pushes to `main` publish preview packages as `{VersionPrefix}-preview.{run}.{attempt}`.
- The **Release** workflow (`release.yml`) promotes the current `VersionPrefix` to a stable release:
  it syncs the doc `Version="…"` literals and rolls the changelog (`scripts/release.mjs`), tags
  `v<version>`, auto-bumps `VersionPrefix` to the next patch, and creates a GitHub Release — which
  fires the `release: published` job in `publish.yml` to publish the stable packages. It runs as a
  GitHub App (`RELEASE_APP_ID`/`RELEASE_APP_PRIVATE_KEY`) so it can bypass branch protection and
  trigger the publish.
- Published GitHub releases or manual dispatches can also publish explicit stable or prerelease SemVer versions.

Keep workflow changes tokenless unless a registry explicitly requires otherwise (the release
identity App is the deliberate exception — branch-protection bypass and downstream triggering both
require a non-`GITHUB_TOKEN` actor).
