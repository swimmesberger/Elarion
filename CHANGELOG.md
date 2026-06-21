# Changelog

All notable changes to Elarion are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html). While Elarion is pre-1.0,
minor releases may include breaking changes.

## [Unreleased]

### Added
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

[Unreleased]: https://github.com/swimmesberger/Elarion/compare/main...HEAD
[0.1.0]: https://github.com/swimmesberger/Elarion/releases/tag/v0.1.0
