# AGENTS.md — Elarion

Canonical agent and contributor guidance. Tool entry points point here and must not duplicate content:
`CLAUDE.md` imports this file (`@AGENTS.md`); `.github/copilot-instructions.md` points here; and
`.github/instructions/csharp.instructions.md` scopes the **C# coding standards** section to `**/*.cs` for
Copilot. Add or change mandatory repository guidance here, not in the pointer files.

Elarion is a reusable .NET application framework. Keep it independent of every downstream application: do
not mention, depend on, or optimize for a consuming app by name. Application domain code, database
conventions, UI frameworks, deployment quirks, and application-specific generators belong in consuming
repositories.

This file states the rules an agent needs while changing the framework. Use the owning source for detail:

| Need | Canonical source |
| --- | --- |
| Package selection and dependencies | [`docs/reference/packages.mdx`](docs/reference/packages.mdx) |
| Current behavior and public API | [`docs/concepts/`](docs/concepts/) and [`docs/capabilities/`](docs/capabilities/) |
| Attributes, diagnostics, and configuration | [`docs/reference/`](docs/reference/) |
| Design rationale, trade-offs, and rejected alternatives | [`docs/decisions/`](docs/decisions/) — ADRs are rationale, not necessarily current API |
| Build, validation, and contributor workflow | [`CONTRIBUTING.md`](CONTRIBUTING.md) |
| Release operation | [`RELEASING.md`](RELEASING.md) |
| Mechanical formatting | [`.editorconfig`](.editorconfig) |

**Scale positioning.** Elarion targets small-to-mid apps: roughly **1–10 nodes, vertical-first, on the
one PostgreSQL the application already runs**. Shipped defaults must work at that tier, but need not scale
past it. If a concern appears only beyond that tier, replace the relevant seam with a dedicated job engine,
broker, cache, or actor system; do not grow a default's complexity or configuration surface. Test every new
default with: “does it cover ten nodes on one Postgres?” (ADR-0025). PostgreSQL extensions are composition,
not scale-out (ADR-0056): document extension recipes, but keep packages usable without extensions.

**Actor placement API.** Actor placement is `ActorPlacementMode.Local` (default), `SingleHome`, or
`VirtualShards` on `[Actor]`/`ActorOptions` (ADR-0061). Do not reintroduce the legacy `SingleHomed` boolean;
use `Placement = ActorPlacementMode.SingleHome`.

## Package layout

[`docs/reference/packages.mdx`](docs/reference/packages.mdx) is the canonical grouped public-package
reference. Start an application/module assembly with `Elarion`, then add only the provider and host adapters
it uses. Add `Elarion.Abstractions` only to contract-only assemblies that cannot take runtime behavior.

- Core packages stay independent of ASP.NET Core, EF Core, and concrete providers. Provider/host packages
  are opt-in siblings, not dependencies pulled into `Elarion` or `Elarion.Abstractions`.
- Choose a host tier deliberately: EF applications retain EF migrations; EF-free NativeAOT hosts pair
  `Elarion.Sql` with `Elarion.Migrations` and exactly one migration provider.
- `Elarion` bundles `Elarion.Generators`, and `Elarion.EntityFrameworkCore` bundles its EF generator.
  Analyzer assets are not transitive: every assembly that needs a bundled generator must reference the
  appropriate public package directly.
- Generator implementation projects are bundled implementation details, not independently selectable
  packages. Do not reconstruct a second package catalog here.
- Package-specific API shape, dependencies, supported providers, and typical references belong in the
  package reference. Read the relevant capability page before adding actors, connections, blobs, devices,
  migrations, a transport, or frontend tooling.

## Architecture boundaries

- Core packages remain reusable and domain-neutral. Do not add consuming-app names, application domain
  types, host-specific UI code, deployment conventions, or application infrastructure to Elarion packages.
- `Elarion.Abstractions` holds implementation-neutral contracts: interfaces, attributes, markers, and data
  records. It has no runtime-integration dependencies. Concrete behavior, defaults, pipeline decorators, and
  telemetry live in dependency-light `Elarion` core or an opt-in sibling (ADR-0034).
- `Elarion` core remains dependency-light and transport-neutral: it depends on Abstractions and
  `Microsoft.Extensions.*` abstractions, not ASP.NET, EF, a protocol package, or a concrete third-party
  runtime provider. Heavy defaults belong in opt-in siblings; keep their seam in Abstractions and their
  decorator in core where appropriate (ADR-0017).
- Preserve the protocol/host split. Protocol packages define a format and dispatcher without binding a wire;
  host packages bind protocol behavior to ASP.NET, sockets, or another hosting environment. Hosts may depend
  on protocols, never the reverse.
- Provider-neutral cores own provider-neutral contracts. Blobs remain streaming-first and S3-wire-free;
  upload protocols are adapters over the staged-upload seam. Connections, client events, settings, migrations,
  and integration messaging expose replaceable seams rather than assuming a single provider.
- Authorization, feature gating, and request validation use the same seam/implementation shape: attribute
  and contract in Abstractions, transport-neutral decorator in core, provider default in an opt-in package.
  Swapping a provider must not change a handler attribute or DTO.
- Validation uses standard `System.ComponentModel.DataAnnotations`. Do not add an Elarion validation
  attribute or a FluentValidation dependency. Business rules belong in handlers/domain services, not a
  pre-handler validator.
- `Elarion.AspNetCore.OpenApi` is the only package that references `Microsoft.AspNetCore.OpenApi`; do not
  leak OpenAPI into the base HTTP transport or introduce an Elarion OpenAPI MSBuild/client-generator package.
  Identity's web-free model stays in `Elarion.EntityFrameworkCore.Identity`; the ASP.NET package owns only
  host wiring.
- EF packages own EF mapping, EF generation, and EF-specific runtime behavior. Provider-neutral contracts
  such as results, pagination requests, and unit-of-work seams stay outside the EF layer. NativeAOT SQL stays
  EF-free; do not blur the two host tiers.
- Design every seam for its strongest intended implementation, never for pre-1.0 compatibility or a weak
  test/single-node implementation. A weaker implementation provides the closest semantics and documents the
  difference. Prefer an optional options bag to repeatedly widening signatures.
- Prefer compile-time generation over runtime reflection scanning. Framework paths must preserve trimming
  and AOT friendliness; a reflection fallback is an explicit, isolated opt-in, not a quiet default.

## Source generator conventions

Generators run on every keystroke, so incrementality is correctness. Read
[ADR-0006](docs/decisions/0006-incremental-source-generator-conventions.md) for rationale, and copy a
current reference generator (`AppModuleDiscoveryGenerator`, `ElarionManifestGenerator`,
`ModuleDefaultServicesGenerator`, or a registration generator), not the old “scan and emit” shape.

When adding or changing a generator:

- **Discover through a syntax provider, never by scanning `CompilationProvider`.** Use
  `ForAttributeWithMetadataName` for attribute triggers (branch on `ctx.TargetSymbol` for methods versus
  types), or a predicate-filtered `CreateSyntaxProvider` for structural triggers. Never enumerate
  `SyntaxTrees` from `RegisterSourceOutput(CompilationProvider, …)`.
- **Make every pipeline value value-equatable.** `ImmutableArray<T>.Equals` is reference equality; use
  `EquatableArray<T>` for every collection field, including nested collections. Carry immutable facts and
  fully qualified strings, not `ISymbol`, `Compilation`, `SyntaxNode`, `Location`, or `object?[]` through a
  pipeline.
- **Treat diagnostics as data.** Transforms stay pure and return `EquatableArray<DiagnosticInfo>` built with
  `DiagnosticInfo.Create` and `LocationInfo`; report diagnostics only in the final
  `RegisterSourceOutput` callback. Do not call `spc.ReportDiagnostic` from a transform.
- **Reuse shared discovery.** Use `ModuleProviders.CollectModules`, `ModuleScanner.FindBest`/`IsInScope`, and
  `ModuleProviders.HasTrigger`. Do not hand-roll module scanning, namespace matching, or assembly-trigger
  detection.
- **Make emitted output deterministic and prove caching.** Provider order is unspecified, so preserve
  emit-time sorting. Tag collect/combine nodes with `.WithTrackingName`; add
  `GeneratorCacheAssert.ReusesOutputsAfterIrrelevantEdit`; and run the affected `*GeneratorTests.cs` suite.
  Generated text is a byte-identical contract, not an incidental implementation detail.
- **Emit concrete AOT-safe code.** Avoid reflection and open generics in generated hot paths. For
  cross-assembly discovery, emit/read assembly metadata through `MetadataReferencesProvider` using the
  existing Elarion/EF manifests; never scan referenced symbol trees.
- Add new analyzer diagnostics to `AnalyzerReleases.Unshipped.md` (RS2008) and preserve stable diagnostic
  behavior. Prefer a clear compile-time error over a delayed, silent runtime fallback.

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
- Small, in-memory files use `ElarionFile`; large exports/uploads use the staged-blob tier. Consult the file
  and blob capability docs before changing envelope, ownership, lifecycle, or resumable-upload behavior.
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
  NRT plus `required` expresses requiredness. Cross-field, conditional, asynchronous, and database/business
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

## C# coding standards

Applies to `**/*.cs` (Copilot scopes this section through
`.github/instructions/csharp.instructions.md`).

### Style

- Use the latest C# supported by the repository (currently C# 14).
- Make classes `sealed` unless they intentionally support inheritance; document the extensibility contract.
- Use immutable records for DTOs, options, and data containers. Prefer nominal property-based records with
  `required` and `init` properties (nullable `init` for optional values); reserve positional records for tiny
  internal helpers and tests.
- Use read-only collection types (`IReadOnlyList<T>`, `ImmutableArray<T>`, and similar) for immutable public
  surfaces. Use concise primary constructors for DI services.
- Mint general identifiers with `Guid.CreateVersion7()`, not `Guid.NewGuid()`. Its important benefit for
  persisted values is index locality plus a debuggable timestamp; use it for never-persisted identifiers too
  for consistency. Never use v7 where guessability matters: it leaks creation time and has 74 random bits.
  Capability-style codes, tokens, and sharing values use v4 or a real CSPRNG token, and an id is never the
  only authorization gate.

### Naming

- Use PascalCase for types, methods, and public members; `_camelCase` for private instance fields; camelCase
  for locals and parameters; PascalCase for constants/static readonly members. Interfaces begin with `I` and
  type parameters with `T`.

### Formatting

- `.editorconfig` is the source of truth. Use file-scoped namespaces, single-line usings, and opening braces
  on the same line.
- Prefer early returns to deep nesting. Use pattern matching, switch expressions, and `nameof` when they
  clarify intent; leave the final `return` on its own line.

### Comments and public API docs

- Public APIs need XML documentation; include `<example>`/`<code>` for non-obvious APIs. Comments explain
  intent, constraints, or non-obvious trade-offs, never restate code. Document *why* for a non-obvious
  compatibility, source-generation, AOT, or performance pattern.

### Nullable reference types

- Non-nullable is the default. Validate nullability at entry points, use `is null`/`is not null`, and trust
  the type system instead of adding redundant null checks.

### Async and background work

- Do not fire-and-forget unobserved work. Thread `CancellationToken` through async flows; use
  `CancellationToken.None` only with a documented reason. Long-lived work belongs to a host-managed
  `IHostedService`, scheduler, explicit queue, or loop, not a hidden helper. Handle expected
  `OperationCanceledException` deliberately and do not log it as an error.

### Telemetry

- Follow OpenTelemetry semantic conventions when they exist. Duration histograms record floating-point
  **seconds** (`s`), never milliseconds, and use semconv bucket boundaries through
  `InstrumentAdvice<double>`; SDK default buckets are millisecond-scaled and unsuitable for seconds-valued
  histograms. `Record*` helpers take `TimeSpan` so this unit decision lives in one place per meter.
- Reuse semconv names such as `rpc.server.call.duration`; namespace custom names and attributes with
  `elarion.*`. Metric tags stay bounded (type, operation, outcome), never payloads, keys, or user identity.
  Put high-cardinality identity on spans only. Explicitly unit-suffixed span tags such as `*_ms` are
  self-describing and exempt from the seconds rule.

## Testing

- Add a regression test for every bug fix. Follow nearby naming/capitalization and avoid
  `Arrange`/`Act`/`Assert` comments.
- Keep tests deterministic. Timing-sensitive tests are appropriate only when concurrency, scheduling, or a
  timing contract is itself under test.
- Generator changes require generator tests, inspectable deterministic output, and the incremental cache test
  described above. Run the affected generator suite after each change.
- Test database behavior against **real PostgreSQL through Testcontainers**, never EF Core InMemory. InMemory
  diverges from PostgreSQL semantics and produces false confidence. Use Docker-gated fixtures: start a real
  container when Docker is available and **skip**, rather than fail, when it is not. Tag integration tests
  `[Trait("Category", "Integration")]`. `Microsoft.EntityFrameworkCore.InMemory` is deliberately absent from
  `Directory.Packages.props`; do not add it.
- Hot-path changes need the relevant benchmark in `tests/Elarion.Benchmarks`; connection, actor, SQL-mapping,
  and bulk-operation claims require measured evidence, not an allocation intuition. TypeScript generator
  changes also require generating a representative schema and NodeNext type-checking emitted client files.

## Development and validation

The complete contributor workflow is in [`CONTRIBUTING.md`](CONTRIBUTING.md). Run the relevant checks before
requesting review; the normal baseline is:

```bash
dotnet restore Elarion.slnx
dotnet build Elarion.slnx --configuration Release
dotnet test --project tests/Elarion.Tests/Elarion.Tests.csproj --configuration Release
dotnet pack Elarion.slnx --configuration Release --no-build

cd src/elarion-jsonrpc-client-generator
npm ci && npm run build && npm test && npm pack --dry-run
```

Run `dotnet run --project tests/Elarion.Benchmarks -c Release` for relevant performance-sensitive changes.
When changing the TypeScript generator, generate from a representative `rpc-schema.json` and type-check
`rpc-types.ts`, `rpc-schemas.ts`, and `rpc-client.ts` under `moduleResolution: NodeNext`.

## Documentation website

Marketing and rendered docs live in `website/`, but top-level `docs/` is the sole content source: Fumadocs
reads it through `website/source.config.ts`. Put static assets in `docs/public/`, never `website/public/`;
only `website/public/CNAME` is committed. New pages need a relevant `meta.json` entry, and new MDX components
beyond the existing standard set need registration in `website/components/mdx.tsx`. `icon:` frontmatter must
name a valid Lucide icon.

```bash
cd website && npm install && npm run build
```

The static export is `website/out`; `npm run dev` serves localhost. Pushes to `main` that touch `docs/**` or
`website/**` trigger `deploy-docs.yml`.

## Pull requests

Prefer stacked PRs when a change is too large for one focused review: each layer is small, single-purpose, and
green; each PR targets the preceding branch; only the bottom targets `main`; merge bottom-up.

Avoid stranding a stack:

- Use one branch per PR; never reuse a head branch.
- **“MERGED” is not “on `main`.”** Before treating work as complete, verify
  `git merge-base --is-ancestor <pr-head-sha> origin/main`, or confirm the base chain terminates at `main`.
- After a base merges, confirm every dependent PR retargeted to `main` or the next unmerged base; retarget it
  manually when necessary.
- Do not delete or reuse a base branch while an open PR still targets it.

## Publishing

Trusted publishing uses OIDC. `<VersionPrefix>` in `Directory.Build.props` is the source of truth for the
next version; see [`RELEASING.md`](RELEASING.md). Pushes to `main` publish
`{VersionPrefix}-preview.{run}.{attempt}`. The Release workflow promotes the current version to stable,
synchronizes doc version literals, rolls the changelog, tags `v<version>`, bumps the next patch, and creates
the GitHub Release that triggers stable publishing. It uses a GitHub App intentionally to bypass protection
and trigger the downstream workflow; keep other workflow changes tokenless unless a registry requires
credentials.

**GitHub Actions:** whenever adding or editing a workflow, look up the latest release of **every** `uses:`
action first and pin it. Do not rely on remembered versions: stale action pins cause runner deprecation
warnings and avoidable breakage.
