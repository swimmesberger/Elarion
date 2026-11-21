---
description: 'Package layout and framework invariants: what must stay true when the framework changes'
applyTo: 'src/**,tests/**,samples/**'
---

# Package layout and framework invariants

The rules an agent needs while changing the framework's packages, their tests or the samples. This file is
their source; `AGENTS.md` holds the rules that apply to every task (among them the architecture boundaries) and
points here. Use the owning page for detail: package selection in `docs/reference/packages.mdx`, current
behavior and public API in `docs/concepts/` and `docs/capabilities/`, rationale in `docs/decisions/`.

## Package layout

[`docs/reference/packages.mdx`](../../docs/reference/packages.mdx) is the canonical grouped public-package
reference. Start an application/module assembly with `Elarion`, then add only the provider and host adapters
it uses. Add `Elarion.Abstractions` only to contract-only assemblies that cannot take runtime behavior.

- Core packages stay independent of ASP.NET Core, EF Core, and concrete providers. Provider/host packages
  are opt-in siblings, not dependencies pulled into `Elarion` or `Elarion.Abstractions`.
- Choose a host tier deliberately: EF applications author EF migrations and run them as steps of the one
  migration plan (`Elarion.Migrations.EntityFrameworkCore`); EF-free NativeAOT hosts pair `Elarion.Sql` with
  `Elarion.Migrations` and exactly one migration provider. SQL scripts, C# code steps, and EF migrations share
  one version sequence, history, and lock (ADR-0081).
- `Elarion` bundles `Elarion.Generators`, and `Elarion.EntityFrameworkCore` bundles its EF generator.
  Analyzer assets are not transitive: every assembly that needs a bundled generator must reference the
  appropriate public package directly.
- Generator implementation projects are bundled implementation details, not independently selectable
  packages. Do not reconstruct a second package catalog here.
- Runtime packages build for `net10.0` (LTS) and `net11.0` (`Directory.Build.props`); `*.Generators` projects stay
  `netstandard2.0`. Packages on the .NET release train resolve to the major of the framework being built
  (`Directory.Packages.props`); the EF Core stack stays on 10.x for both until the Npgsql EF Core 11 provider ships.
  Bridge an API difference between the majors with `#if NET11_0_OR_GREATER` in the package, and, for generated
  code, by having the generator detect which shape the compilation references (as `ValidationResolverGenerator`
  does) — generated code compiles against the consumer's framework, not ours. Keep the generators' Roslyn floor at
  the oldest consumer SDK band, not at `global.json`.
- Package-specific API shape, dependencies, supported providers, and typical references belong in the
  package reference. Read the relevant capability page before adding actors, connections, blobs, devices,
  migrations, a transport, or frontend tooling.

## Framework invariants

### Modules, handlers, and transports

- A handler is the framework's use-case boundary. JSON-RPC, MCP, and HTTP are parallel optional projections
  of one handler definition; HTTP is explicit because routing and binding are transport-specific. Keep
  handler behavior transport-neutral.
- `[GenerateModuleBootstrapper]` and the generated module registration/mapping path are canonical. Modules
  own handlers, services, validators, jobs, consumers, actors, and client-event topics by namespace. Do not
  add flat, ungated handler maps or manually duplicate generated default registrations.
- Core modules map unconditionally; feature modules are enabled through configuration and must disappear
  consistently from registration, validation, HTTP, JSON-RPC, and MCP when disabled. A host still declares a
  core module even if it has no feature modules.
- In-process request/reply is always typed: inject `IHandler<TRequest, Result<TResponse>>`, use
  `IHandlerSender`, `HandlerInvoker`, or a typed generated module API. The named `HandlerDispatcher` is a
  transport seam for dynamic wire names, not a normal application mediator. Event buses are pub/sub only and
  never implement request/reply.
- HTTP handlers resolve the typed handler directly and translate `Result` through framework HTTP helpers.
  Keep endpoint conventions in the host; do not infer host authorization policy from handler attributes.
- Small, in-memory files use `ElarionFile`; large exports/uploads use the staged-blob tier; an HTTP-only
  `[HttpEndpoint]` (no `[Handler]`) may stream a file with `ElarionStreamedFile` (ADR-0098), never a host-mapped
  route that loses the handler's authorization and feature gate. Every framework file response carries
  `X-Content-Type-Options: nosniff`. Consult the file and blob capability docs before changing envelope,
  ownership, lifecycle, or resumable-upload behavior.
- The schema/client chain is `[Handler]` → `rpc-schema.json` → generated TypeScript. Do not hand-write a
  parallel JSON-RPC fetch client or a second transport registry.

### Serialization and AOT

- All framework subsystems obtain canonical JSON through `IElarionJsonSerialization`, never a bare
  `JsonSerializerOptions` from DI. Configure framework JSON through `AddElarionJson`/
  `ConfigureElarionJson`; this prevents collisions with host JSON configuration.
- Source-generated metadata is the default and expected path. Fix a missing type by contributing its type
  info resolver/context. Do not casually enable reflection fallback to make a missing contract work.
- Resolver composition is ordered and first-match-wins. Transport envelope contexts, generated module
  contexts, and host contexts must use the established contributor seam; read the serialization capability
  page before changing resolver ordering or reflection behavior.

### Authorization, feature gates, and validation

- Authorization is declarative, transport-neutral, provider-independent, and enforced in the handler
  pipeline. Hosts authenticate and populate `ICurrentUser`; handlers express authorization requirements.
  Never move a business authorization decision solely into an HTTP endpoint or frontend condition.
- Requirement kinds compose as documented. Unauthenticated and forbidden outcomes remain normal `Result`/
  `AppError` values, not exceptions. A provider replacement must not require changing `[Require*]` contracts.
- Feature gates are runtime handler gates, distinct from compile-time module enablement. A closed feature
  returns the documented generic not-found outcome so its name is not leaked. Use a feature variant when
  selection differs between concurrent requests; use a configuration variant when one process-wide choice is
  configured. Prefer a strategy service over swapping a handler implementation.
- Client capability/session data and frontend contribution `when` conditions are read-only UX projections.
  They may hide or adapt UI, but never enforce a permission or feature decision; the handler gate remains the
  authority.
- Validation is two-tier. Standard DataAnnotations express wire-contract rules and flow to schema surfaces;
  nullability expresses requiredness (ADR-0082). Cross-field, conditional, asynchronous, and database/business
  rules live in the handler or a domain service and return `AppError.Validation`/`Conflict` **inside the
  transaction**. A pre-handler async check is a TOCTOU bug.
- The validation provider receives wire-named paths and produces structured errors. Do not reintroduce runtime
  attribute scanning where generated validation metadata exists. See the authorization, feature-flag,
  client-capability, and validation concepts for exact attribute/provider APIs.

### Events, modules, and cross-module communication

- Events have two transaction-defined planes. Domain events are inline in the caller's scope and transaction:
  consumers share the transaction and a failure fails the command. Integration events are recorded with the
  caller's unit of work and delivered after commit in an independent scope; they are the only broker-portable
  plane. An event belongs to exactly one plane.
- An event bus is pub/sub. A non-`Unit` result is request/reply and belongs behind a typed handler call,
  not a consumer. Prefer handler-form consumers when the full pipeline is needed; use method-form consumers
  only for lightweight side effects that do not require it.
- Durable integration delivery needs an outbox. Handler-form integration consumers are inbox-deduped when a
  durable idempotency store is registered; opting out declares redelivery safe. Method-form consumers and
  external side effects still need explicit idempotency reasoning around the message id.
- All generated event consumers, scheduled jobs, and other discovered module content remain module-scoped and
  feature-gated. Do not bypass generated catalog/registration paths to make a consumer run in a disabled
  module.
- Synchronous cross-module calls use a published `[ModuleContract]`; implementations remain internal. Solve
  a boundary violation with a genuine contract, a platform capability port outside modules, or a shared-kernel
  type outside modules—never another module's internal handler/service type. `ELMOD002` is location-based;
  track any new analyzer diagnostics in `AnalyzerReleases.Unshipped.md`.
- A generated module API is an in-process, typed facade over a module's handlers, not a transport or a way
  to make an internal module surface public. Mapping from a published contract to a module's handler DTOs is
  the module's responsibility.

### Actors, connections, and live updates
- **Actor placement API.** Actor placement is `ActorPlacementMode.Local` (default), `SingleHome`, or
  `VirtualShards` on `[Actor]`/`ActorOptions` (ADR-0061). Do not reintroduce the legacy `SingleHomed` boolean;
  use `Placement = ActorPlacementMode.SingleHome`.
- **Default to the database.** Optimistic concurrency, constraints, `[Idempotent]`, jobs, and normal
  request/reply solve classical web-app concurrency. An actor is justified only when the consistency unit is
  a live in-memory thing: a stateful external resource/connection, hot loss-tolerant ephemeral state, or an
  event-driven decide-once coordinator. If it sketches as a table with a version column, use the table.
- Actor methods apply a pure state transition, write durable state explicitly when required, then perform a
  side effect. State records are query contracts: keep interpretation and pure transitions on the state type,
  not in an activation-only migration. Snapshot conflicts can replay a turn, so side effects before a write
  must be idempotent or moved after the write. Read the actor state/placement concept before changing actor
  lifecycle, snapshots, routing, or streaming.
- Default to request/reply plus client events. A bidirectional connection is justified only for interactive
  command rates, a connection that is itself state (such as a device link), or server-to-client RPC. The
  registry is node-local; multi-node ingress is co-located with the actor home/role holder rather than made
  into an implicit balancer.
- Client events are at-most-once, latest-wins re-query hints. Keep their payloads light and make clients
  converge by re-querying; they are not a durable event plane. Use an ordered stream only when losing a
  message makes the consumer wrong and a single live sequencer exists for that key. If a missed update only
  makes the consumer stale, use a client event instead.
- Connection adapters own framing and transport lifecycle; codecs own protocol encoding/decoding. Socket-less
  simulated connections are first-class. Use the simulation package for gateway behavior tests; reserve real
  sockets for adapter integration tests.
- Device pairing/authentication, actor placement, role routing, and cross-node client-event fan-out are
  security/correctness-sensitive seams. Preserve fail-closed behavior; consult the capability and ADR docs
  before changing identity, leases, proxying, or durable delivery targeting.

### Persistence, blobs, and SQL

- Application handlers inject the concrete `DbContext`; do not introduce repositories or an `IAppDbContext`
  abstraction. The generator owns discovered `DbSet`s and entity configuration application.
- `Guid` entity keys are client-assigned and use the repository's v7 GUID convention. Preserve the model's
  `ValueGeneratedNever` behavior; explicit configuration or a store-generated default wins where designed.
- Bulk inserts, SQL mapping, migrations, settings, blobs, and auditing each have deliberate transaction and
  provider seams. Preserve ambient-transaction behavior and connection ownership; do not add a second
  connection/data source merely for convenience.
- Blob contracts remain provider-neutral and streaming-first. Pending blobs are temporary until committed in
  the caller's transaction; owner checks fail closed; staged uploads are protocol-neutral. Listing is a
  browse/operations surface, not a substitute for an application query model.
- The AOT SQL tier is not an ORM: no change tracking, LINQ, reflection fallback, or query-builder DSL. SQL
  interpolation binds values; only explicit trusted identifier fragments are verbatim. Use real bulk COPY for
  bulk throughput rather than growing convenience batch APIs into a second bulk subsystem.
- The migration core is database-neutral and roll-forward only. A provider is a new package, not a dialect
  configuration flag; keep no-transaction behavior explicit and fail closed on an unresolved migration.

### TypeScript client and frontend contributions

- Generated TypeScript output is deterministic and portable: keep the ergonomic direct API and generic
  transport primitive, tuple-aware batching, Zod validation, browser support, and NodeNext support. Generated
  runtime code must not import React, TanStack, Vite, or an application framework.
- The contributions package supplies typed extension points and capability-aware UX composition, not a UI kit,
  route system, or security model. Applications own point payloads, module discovery, shell composition, and
  route composition. Duplicate co-visible contribution ids are an error because they are render keys.
- Frontend `when` axes remain strict against the application's declared vocabulary and fail closed for omitted
  axes. They are still UX projections; server-side authorization and feature gates protect the action.
