---
type: Architecture page
title: "Architecture"
description: "How the framework is shaped and why, with the decisions behind it."
generated: { by: claude-code/claude-fable-5-2, at: 2026-11-21T11:30:00Z }
status: draft
---
# Architecture

Elarion is a .NET application framework delivered as NuGet and npm packages. An application writes handlers
inside modules; source generators emit the wiring at build time; opt-in packages project the same handlers
onto HTTP, JSON-RPC, MCP and gRPC and add persistence, events, actors, connections and blobs. It targets
small-to-mid applications: one to ten nodes on the one PostgreSQL the application already runs.

This page is compiled. The rules an agent must follow while changing the framework are normative in
`AGENTS.md` and `.github/instructions/framework.instructions.md`; the package list is normative in
[the package reference](reference/packages.mdx). When this page disagrees with either, they win and this page
gets fixed.

## Shape

- **`Elarion.Abstractions`**: implementation-neutral contracts (interfaces, attributes, markers, data
  records). Depends on nothing at runtime. Referenced alone only by contract-only assemblies.
- **`Elarion`** (core): the dependency-light runtime: modules, the handler pipeline and its decorators,
  scheduling, streams, current user, telemetry. Depends on Abstractions and `Microsoft.Extensions.*`
  abstractions only, never on ASP.NET Core, EF Core, a protocol package or a concrete provider. Bundles
  `Elarion.Generators`.
- **Generators** (`*.Generators`, `netstandard2.0`): incremental Roslyn generators and analyzers, bundled by
  the public package they belong to and not selectable on their own. They emit registration, dispatch,
  mapping and validation code and compile-time diagnostics.
- **Provider packages** (for example `Elarion.Validation`, `Elarion.Caching`, `Elarion.FeatureFlags.*`,
  `Elarion.Resilience`): the default implementation of a seam whose contract is in Abstractions and whose
  decorator is in core. Opt-in siblings, never pulled in by core.
- **Protocol packages** (`Elarion.JsonRpc`, `Elarion.Grpc`): a wire format and a dispatcher, bound to no host.
- **Host packages** (`Elarion.AspNetCore` and its siblings, `Elarion.Connections.Tcp`): bind a protocol or a
  subsystem to ASP.NET Core, sockets or another hosting environment. A host package may depend on a protocol
  package, never the reverse.
- **Persistence, two host tiers**: the EF tier (`Elarion.EntityFrameworkCore` and the `*.EntityFrameworkCore`
  stores) and the EF-free NativeAOT SQL tier (`Elarion.Sql` with one provider). `Elarion.Migrations` is a
  database-neutral core with one provider per database; SQL scripts, C# steps and EF migrations run as one
  plan.
- **Provider-neutral subsystem cores** (`Elarion.Blobs`, `Elarion.Connections`, `Elarion.ClientEvents`,
  `Elarion.Settings`, `Elarion.Actors`): each owns its contracts and has PostgreSQL, ASP.NET Core
  or other adapters as separate packages.
- **npm packages** (`src/elarion-*`): the TypeScript client generator, frontend contributions, web push and
  the installable-app shell. Generated client code imports no application framework.
- **`samples/`** shows the intended use; **`tests/`** holds one test project and the benchmarks;
  **`website/`** renders `docs/`.

## Why it is this shape

- Abstractions holds contracts and core holds behavior, so a contract-only assembly takes no runtime
  dependency ([ADR-0034](decisions/0034-abstractions-holds-contracts-not-implementations.md)).
- Core stays dependency-light and heavy defaults are opt-in packages, so an application pays only for the
  providers it uses ([ADR-0017](decisions/0017-dependency-light-core.md)).
- Wiring is generated at compile time instead of scanned at run time, which keeps trimming and NativeAOT
  working and turns mistakes into build errors
  ([ADR-0006](decisions/0006-incremental-source-generator-conventions.md),
  [ADR-0014](decisions/0014-cross-assembly-generator-composition.md),
  [ADR-0018](decisions/0018-generated-infrastructure-is-framework-named.md)).
- A handler is the one use-case boundary and every transport is a projection of it; HTTP stays explicit
  because routing and binding are transport-specific
  ([ADR-0031](decisions/0031-imperative-handler-transport-mapping.md),
  [ADR-0071](decisions/0071-generator-owned-http-endpoint-binding.md)).
- Modules own code, the data layer is shared: a feature is a plugin over the application's entities and
  reaches another module only through a published contract
  ([ADR-0007](decisions/0007-data-is-platform-module-as-plugin.md),
  [ADR-0002](decisions/0002-cross-module-communication.md)).
- Events have two planes defined by the transaction, and the bus is pub/sub only
  ([ADR-0001](decisions/0001-event-transaction-phase.md),
  [ADR-0010](decisions/0010-event-bus-is-pub-sub-only.md)).
- Defaults are sized for ten nodes on one PostgreSQL; beyond that a seam is replaced, a default does not grow
  ([ADR-0025](decisions/0025-distributed-scheduler-coordination.md),
  [ADR-0056](decisions/0056-postgres-extensions-posture.md)). Actors, role leases and
  virtual shards follow the same rule: a PostgreSQL lease, not a cluster
  ([ADR-0048](decisions/0048-single-homed-actors.md), [ADR-0049](decisions/0049-role-leases.md),
  [ADR-0061](decisions/0061-virtual-sharded-actors.md)).
- The two persistence tiers are kept apart and migrations are one plan across them
  ([ADR-0058](decisions/0058-aot-sql-row-mapping.md),
  [ADR-0060](decisions/0060-database-neutral-migration-core.md),
  [ADR-0081](decisions/0081-one-migration-plan.md)).
- Canonical JSON is one framework-owned configuration, so framework subsystems never collide with the host's
  JSON options ([ADR-0023](decisions/0023-canonical-json-serialization.md)).
- Runtime packages build for `net10.0` and `net11.0`; the `net11.0` build compiles with runtime async
  ([ADR-0092](decisions/0092-runtime-async-on-net11.md)).

## Sources

- `AGENTS.md` ("Architecture boundaries") and `.github/instructions/framework.instructions.md` (package layout
  and framework invariants)
- [Package reference](reference/packages.mdx), "Dependency rules"
- The decisions linked above; the full list is [decisions/index.md](decisions/index.md)
