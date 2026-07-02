# ADR-0023: Canonical JSON serialization configuration

- Status: Accepted
- Date: 2026-07-01
- Related: [ADR-0011](0011-runtime-settings-subsystem.md) (settings' source-gen `JsonTypeInfo<T>` access),
  [ADR-0017](0017-dependency-light-core.md) (dependency-light core; where seams vs. defaults live),
  [ADR-0018](0018-generated-infrastructure-is-framework-named.md) (the generated bootstrapper that aggregates
  module contexts), [ADR-0006](0006-incremental-source-generator-conventions.md) (generator conventions).

## Context

An increasing number of Elarion subsystems (de)serialize JSON — JSON-RPC, MCP, idempotency, caching, the outbox,
and settings. Today each one obtains its serialization configuration in its own way, and the host has to satisfy
all of them separately:

- **JSON-RPC / MCP** — the host builds a `JsonSerializerOptions` **by hand** (combining the framework envelope
  context, `configuration.GetAllJsonTypeInfoResolvers()`, and a reflection tail) and passes it into
  `AddElarionJsonRpc(options, …)` / `AddElarionMcp(…, options, …)`. It also registers that instance as a **bare
  `JsonSerializerOptions` singleton**.
- **Idempotency** and **caching** silently depend on that bare singleton (`sp.GetRequiredService<JsonSerializerOptions>()`);
  neither registers one, so they only work because the host happened to.
- **Outbox** carries its own `OutboxOptions.SerializerOptions`, defaulting to a **reflection-based** resolver
  (not trim/AOT-safe) — inconsistent with the source-gen posture everywhere else.
- **Settings** takes a source-generated `JsonTypeInfo<T>` on **every** `GetAsync`/`SetAsync` call.

The result: no single source of truth, a hidden DI coupling, a latent AOT gap, and a bare `JsonSerializerOptions`
in DI that can collide with a host's own registration (ASP.NET Core / MVC resolve `JsonSerializerOptions` from
DI). Meanwhile the module system **already** aggregates per-module source-generated contexts: each `[AppModule]`
optionally exposes `static IJsonTypeInfoResolver GetJsonTypeInfoResolver()`, and the generated `ElarionBootstrapper`
emits `IConfiguration.GetAllJsonTypeInfoResolvers()` over the enabled modules. The pieces to unify already exist.

## Decision

Introduce a single **canonical JSON serialization configuration** that every subsystem reads, composed from
per-layer contributions, exposed through a dedicated accessor — never a bare `JsonSerializerOptions` in DI.

1. **`ElarionJsonOptions` (in `Elarion.Abstractions`)** — a mutable bag of knobs (`PropertyNamingPolicy`,
   `PropertyNameCaseInsensitive`, `DefaultIgnoreCondition`), an ordered `IList<IJsonTypeInfoResolver>
   TypeInfoResolvers`, an `EnableReflectionFallback` flag, and a `PostConfigure` escape hatch. The defaults
   reproduce the knobs the host previously set by hand.

2. **`IElarionJsonSerialization` (accessor; interface and default impl in `Elarion.Abstractions`)** — materializes a
   single `JsonSerializerOptions` from the composed `ElarionJsonOptions`, **freezes it** (`MakeReadOnly()`) on
   first access, and exposes `Options`, `GetTypeInfo<T>()`, and `GetTypeInfo(Type)`. Subsystems depend on this,
   **not** on a bare `JsonSerializerOptions`.

   The accessor, its options, and the `AddElarionJson`/`ConfigureElarionJson` registration all live in
   `Elarion.Abstractions` (which already hosts the pipeline decorators and `HandlerDispatcher`, not just
   contracts) — because the subsystem packages that consume it (`Elarion.Caching`, `Elarion.Messaging.Outbox`,
   `Elarion.Settings`, `Elarion.JsonRpc`) reference only `Elarion.Abstractions`, not `Elarion` core.

3. **Contributor composition (no `Microsoft.Extensions.Options` dependency).** `AddElarionJson()` registers the
   accessor (idempotent); `ConfigureElarionJson(Action<ElarionJsonOptions>)` accumulates a contribution. Every
   registered contribution is applied, in registration order, when the options first materialize — so several
   layers each add their resolvers/knobs without the host wiring them together. This mirrors the framework's
   existing `TryAddEnumerable` contributor seams (e.g. `IDispatchScopeInitializer`) rather than taking on the
   options pattern, which `Elarion.Abstractions`/`Elarion` deliberately avoid.

4. **Resolver order is first-match-wins**, by contribution order: transport envelope contexts insert first
   (`TypeInfoResolvers.Insert(0, …)`), module DTO contexts from `GetAllJsonTypeInfoResolvers()` next, host extras
   after. This preserves the ordering the host previously composed by hand. Because the transports' index-0 insert
   would otherwise be unbeatable, `OverrideTypeInfoResolvers` provides a host-priority segment composed ahead of
   every `TypeInfoResolvers` entry — the sanctioned way to override a type an envelope context also registers. The
   full chain is: overrides → contributed resolvers → the always-seeded framework context → the optional
   reflection fallback.

5. **AOT-strict by default.** No reflection tail is added unless `EnableReflectionFallback` is set. A type missing
   from every source-generated context throws at runtime (surfacing a missing `[JsonSerializable]`) instead of
   silently reflecting — matching the repo-wide `JsonSerializerIsReflectionEnabledByDefault=false`. The reflection
   fallback isolates its `DefaultJsonTypeInfoResolver` in a suppressed helper (documented non-AOT-safety), so core
   stays `IsAotCompatible`.

6. **The framework's own context is always seeded.** `ElarionFrameworkJsonContext` — a source-generated context
   for the framework's own types that are **not statically reachable** from an app's `[JsonSerializable]` roots
   (so no module/host context would register them) — is appended to the resolver chain by the accessor. The
   canonical case is a payload behind a polymorphic `object` slot: `AppError.Data` is typed `object`, so a
   transport serializing a failed `Result` (e.g. the JSON-RPC error object) dispatches on the runtime type and
   needs a `JsonTypeInfo` for each concrete payload — which the STJ source generator never pulls into a module
   context because the `object` breaks static reachability. The context holds those types (currently
   `ValidationErrorData`). It is appended **last**, so any host/module context still wins first-match for an
   overlapping type, and reflection-free, so it keeps core AOT-strict. This means a validation failure serializes
   under source generation with no per-app registration, closing a gap where an AOT-strict host got a
   `NotSupportedException` (→ HTTP 500) on any validation error instead of the intended `-32602`. It also
   guarantees the chain is never empty, so the options can always be frozen (no empty sentinel resolver is
   needed). The context is named for the *category*, not "errors", so when the framework introduces another such
   type its `[JsonSerializable]` goes here and neither the seeding logic nor the type's name changes.

### Consuming-side migration (the shape follow-ups implement)

This ADR ships the foundations (the options, the accessor, `AddElarionJson`/`ConfigureElarionJson`, the ADR, and
tests). Wiring the subsystems is deferred but fixed in shape:

- **Generated `AddElarion(configuration)`** contributes the module resolvers via
  `ConfigureElarionJson(o => o.TypeInfoResolvers.AddRange(configuration.GetAllJsonTypeInfoResolvers()))`.
- **JSON-RPC / MCP** drop the `JsonSerializerOptions` parameter from `AddElarionJsonRpc`/`AddElarionMcp`; the
  dispatcher factories resolve `IElarionJsonSerialization.Options`, and each inserts its envelope context first.
- **Idempotency / caching** resolve the accessor instead of a bare `JsonSerializerOptions`; their `Add…` methods
  call `AddElarionJson()`.
- **Outbox** makes `OutboxOptions.SerializerOptions` nullable, defaulting to the canonical options (removing the
  reflection default).
- **Settings** replaces the explicit `JsonTypeInfo<T>` overloads with ergonomic `GetAsync<T>(key, fallback,…)` /
  `SetAsync<T>(key, value,…)` that resolve type info from the accessor.

## Consequences

**Positive**

- One place configures JSON for the whole framework; the host stops hand-building a `JsonSerializerOptions`.
- No bare `JsonSerializerOptions` in DI, so Elarion never collides with a host's own registration, and the hidden
  idempotency/caching coupling is removed.
- AOT-strict by default closes the outbox reflection gap and makes a missing context a loud, early failure.

**Negative / accepted**

- **Breaking (pre-1.0, acceptable):** `AddElarionJsonRpc`/`AddElarionMcp` lose their options parameter; the
  outbox and settings serialization surfaces change; hosts delete their hand-built options. Migration is
  mechanical and documented above.
- A host that relied on the old reflection tail to serialize a type absent from every context must add its
  `[JsonSerializable]` (or opt into `EnableReflectionFallback`). This is the intended behavior change.

## Implementation

- `src/Elarion.Abstractions/Serialization/`: `ElarionJsonOptions`, `IElarionJsonSerialization`,
  `ElarionJsonSerialization` (materialize + freeze, seeding `ElarionFrameworkJsonContext`),
  `ElarionFrameworkJsonContext` (the framework's always-seeded context), the internal `ElarionJsonConfigurator`
  contribution, and `ElarionJsonServiceCollectionExtensions` (`AddElarionJson`/`ConfigureElarionJson`).
- Tests: `tests/Elarion.Tests/Serialization/ElarionJsonSerializationTests.cs` and
  `tests/Elarion.Tests/JsonRpc/RpcDispatcherHandlerTests.cs` (the error-payload serialization regression).
