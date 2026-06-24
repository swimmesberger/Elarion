# Changelog

All notable changes to Elarion are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html). While Elarion is pre-1.0,
minor releases may include breaking changes.

## [Unreleased]

### Added
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
  Documents the recommended layout for consuming apps: keep `[DbEntity]` entities in a shared-kernel
  namespace (under no `[AppModule]`, so cross-aggregate references never trip the `ELMOD002` boundary
  analyzer), let each module own its `IEntityTypeConfiguration<T>` beside its handlers, keep
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
  `Elarion` directly, and any assembly declaring `[DbEntity]`/`[GenerateDbSets]` types references
  `Elarion.EntityFrameworkCore` directly. This fixes the silent failure where a separate entity
  assembly referencing only the EF Core marker package emitted no manifest and produced zero `DbSet`s.
  To upgrade: remove the `Elarion.Generators` / `Elarion.EntityFrameworkCore.Generators`
  `PackageReference`s; add a direct `Elarion` reference to host projects that previously relied on the
  standalone generator package.

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

[Unreleased]: https://github.com/swimmesberger/Elarion/compare/v0.2.0...HEAD
[0.2.0]: https://github.com/swimmesberger/Elarion/releases/tag/v0.2.0
[0.1.0]: https://github.com/swimmesberger/Elarion/releases/tag/v0.1.0
