# ADR-0071: Generator-owned HTTP endpoint binding — RequestDelegate registrations, not RDG

- Status: Accepted
- Date: 2026-07-30
- Amends: [ADR-0031](0031-imperative-handler-transport-mapping.md) (the "RDG-friendly" mechanism claim; the
  concrete-per-handler rule itself stands)
- Related: [ADR-0006](0006-incremental-source-generator-conventions.md) (generator conventions),
  [ADR-0023](0023-canonical-json-serialization.md) (canonical JSON), [ADR-0026](0026-openapi-http-transport.md)
  (OpenAPI stays Microsoft's document pipeline), and the
  [http-endpoints](../capabilities/transports/http-endpoints.mdx) capability page.

## Context

ADR-0031 and the HTTP capability page claimed the generated per-handler minimal-API lambdas "work with the
ASP.NET Core Request Delegate Generator" and therefore stay Native-AOT/trim-safe. Empirical verification
(GitHub issue #131) showed the chain cannot hold:

- **RDG never sees generated call sites.** Roslyn source generators run unordered over the same input
  compilation; no generator can observe another generator's output. RDG is itself a source generator, so the
  `MapGet`/`MapPost` calls emitted into `ElarionBootstrapper.g.cs` are invisible to it. Building a sample with
  RDG enabled produced exactly one interceptor — for a hand-written control endpoint — and none for the
  generated calls.
- **The compile-time warning signal is suppressed.** The typed-lambda `Map*` overloads take `System.Delegate`
  and are `[RequiresUnreferencedCode]`/`[RequiresDynamicCode]`. RDG ships a `DiagnosticSuppressor` that
  suppresses IL2026/IL3050 at `Map*` call sites — including generated sites it can never intercept — so a
  build with RDG enabled looks clean while the reflection fallback is still in play.
- **Publish fails or the binary is broken.** `dotnet publish -p:PublishAot=true` re-surfaces IL2026/IL3050 at
  the generated call sites (fatal under `TreatWarningsAsErrors`). Forcing the publish anyway produced a binary
  in which the first request 500s **every** route: the runtime `RequestDelegateFactory` fallback throws
  `NotSupportedException` (no `JsonTypeInfo` for `Task<IResult>`) while the endpoint table is built, taking
  down the whole route matcher.
- The same applies to hand-authored framework `Map*` extensions (`MapElarionSession`,
  `MapElarionClientEvents`, `MapElarionStream<T>`, blob/Tus endpoints): their call sites live in precompiled
  framework assemblies, where RDG — which runs only in the compilation that contains the call — can never
  intercept them.

The concrete-per-handler rule of ADR-0031 remains correct: a generic `MapElarionHandler<TRequest,TResponse>`
seam is illegible to every static tool in the chain (trimmer, ILC) and hides the per-endpoint types. What was
wrong is only the claimed mechanism ("RDG-friendly"). The fix must make the AOT claim true on Elarion's own
terms.

## Decision

**The generator owns HTTP request binding.** `[HttpEndpoint]` registrations use the AOT-safe
`Map*(string, RequestDelegate)` overloads — which carry no `RequiresUnreferencedCode`/`RequiresDynamicCode`
annotations and bypass `RequestDelegateFactory` entirely — and the emitted delegate performs its own binding:

- **Compile-time member classification.** For member-wise shapes (GET/DELETE, plus the `[AsParameters]`,
  `[From*]`, and file opt-ins), `HttpEndpointEmission` classifies every constructor parameter and
  settable/init property: route (name matches a route token, or `[FromRoute]`), query (default, or
  `[FromQuery]`), header, form value, `IFormFile`/`IFormFileCollection`, or one `[FromBody]` member. Parse
  strategies are `string`, enum (case-insensitive), and `IParsable<T>` value types (invariant culture), plus
  arrays of those from repeated query keys. An unbindable member is a compile-time diagnostic (`ELHTTP005`)
  rather than a runtime surprise. POST/PUT/PATCH without opt-ins bind the whole request from the JSON body,
  as before.
- **A small, allocation-free runtime binder.** The emitted delegate calls the static
  `Elarion.AspNetCore.ElarionHttpEndpointBinder` helpers — reflection-free parsing with failures accumulated
  in a by-`ref` `ElarionHttpBindingErrors` struct whose error dictionary only materializes on the failure
  path, and JSON bodies read through a `JsonTypeInfo<T>` resolved once at mapping time from the minimal-API
  `Http.Json` options (so `AddElarionHttpJson` keeps request binding on the canonical source-generated
  contexts, and a host `ConfigureHttpJsonOptions` override still applies). A successfully bound request
  allocates nothing beyond the DTO itself — benchmarked against the RDG-compiled equivalent of the same
  endpoint in `tests/Elarion.Benchmarks` (`HttpEndpointDispatchBenchmarks`), which is the gate for changes to
  this path. Responses execute the existing `ElarionHttpResults` translation via `IResult.ExecuteAsync`.
- **Binding failures are RFC 7807.** Malformed or missing wire values produce the same `ValidationProblem`
  shape as handler-tier validation (field-keyed `errors` map, 400), keyed by the route/query/header/form name;
  a non-JSON body is 415. Previously these were undocumented, ASP.NET-shaped rejections.
- **OpenAPI keeps working through a shape method plus explicit metadata.** ApiExplorer only describes
  endpoints whose metadata carries a `MethodInfo` (used for gating, controller name, and return type) — and
  since .NET 9 it builds parameter descriptions exclusively from `IParameterBindingMetadata` and synthesizes
  request bodies from `IAcceptsMetadata`, never from the method signature. Each registration therefore
  attaches: a never-invoked private static "API shape" method whose parameters are the flattened bound
  members (attributed with the classified `[From*]` source and wire name), obtained via delegate creation
  against a generated exact-signature delegate type — not `GetMethod` reflection, so trim/AOT-safe; one
  `ElarionHttpParameterBindingMetadata` per member pointing at the matching shape parameter; and, for
  body-mode endpoints, an `AcceptsMetadata` carrying the request type. Response metadata moves from the
  `RouteHandlerBuilder`-only `Produces*` sugar to explicit `ProducesResponseTypeMetadata`;
  `ProducesElarionErrors` became generic over `IEndpointConventionBuilder`.
  For member-wise shapes the generator also copies each DTO member's constant-argument DataAnnotations
  attributes onto the matching shape parameter (fully-public, parameter-applicable attributes only), so
  route/query/header/form constraints such as `[Range]` and `[StringLength]` keep flowing into the served
  document; JSON-body constraints ride the schema transformer as before.
- **The manifest carries the classification.** Cross-assembly module endpoints ride
  `Elarion.Manifest.HttpEndpoint.v2`, which appends the nested binding-member blob. v1 entries are not
  decoded (pre-1.0; rebuild the module assembly against the matching Elarion version).
- **Framework `Map*` extensions follow the same rule.** Session, client events, streams, connection sockets,
  and blob/Tus endpoints register `RequestDelegate`s and bind by hand, attaching shape methods where their
  OpenAPI description matters.

## Consequences

**Positive**

- The AOT claim is true by construction: no `RequestDelegateFactory`, no RDG dependency, zero IL2026/IL3050 at
  any Elarion-owned `Map*` site; `[HttpEndpoint]` apps publish and run under Native AOT.
- One binding code path under JIT and AOT — no environment-dependent behavior split.
- Binding-tier 400s join the documented, canonical ProblemDetails contract instead of ASP.NET's opaque
  rejection shapes.
- Unsupported request shapes fail at compile time (`ELHTTP005`) with an explanation, matching the repo's
  "clear compile-time error over silent runtime fallback" rule.

**Negative / accepted**

- Elarion owns HTTP binding semantics forever: route/query/header/form inference, requiredness (`required`
  modifier, non-nullable without default, constructor defaults, property initializers via a probe-instance
  fallback), and parse rules are now framework contract, tested in the binder/e2e suites. The supported
  member-type matrix is deliberately bounded — string, enum, `IParsable<T>` value types, their nullables,
  query arrays, form files, one `[FromBody]` member.
- The shape method is metadata-only duplication of each endpoint's signature (a few generated lines per
  endpoint) — the price of keeping ApiExplorer/OpenAPI on Microsoft's pipeline per ADR-0026 without RDG.
- `Produces*` sugar is unavailable on `IEndpointConventionBuilder`; emitted metadata is the raw
  `ProducesResponseTypeMetadata`, and `ProducesElarionErrors<TBuilder>` is a binary (not source) breaking
  change. Extensions that returned `RouteHandlerBuilder` now return `IEndpointConventionBuilder`.
- Antiforgery is no longer auto-required on form endpoints (that enforcement was a `RequestDelegateFactory`
  behavior); the emitted `.DisableAntiforgery()` marker is kept as explicit intent, and hosts needing
  antiforgery on form posts enforce it deliberately.
