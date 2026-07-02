# ADR-0026: OpenAPI for the HTTP transport

- Status: Accepted
- Date: 2026-07-02
- Related: [ADR-0023](0023-canonical-json-serialization.md) (the canonical JSON the schema pipeline reads),
  [ADR-0017](0017-dependency-light-core.md) (opt-in sibling packages carry heavy runtime dependencies),
  [ADR-0021](0021-idempotency.md) (the `[Idempotent]` contract this advertises over HTTP),
  [ADR-0018](0018-generated-infrastructure-is-framework-named.md) (the generated bootstrapper that maps `[HttpEndpoint]`).

## Context

The JSON-RPC transport has a complete schema-driven pipeline: a build-time schema export
(`Elarion.AspNetCore.SchemaGeneration` → `rpc-schema.json`) and a generated TypeScript client
(`elarion-jsonrpc-client-generator` → types + Zod + fetch client). The HTTP transport (`[HttpEndpoint]`)
had **no** machine-readable contract and **no** client generation — a consumer had to hand-write `fetch`
calls. That is a real parity gap, and it bites exactly the audience HTTP exists for.

HTTP's documented purpose (see [http-endpoints](../capabilities/transports/http-endpoints.mdx), "HTTP or
JSON-RPC?") is *resourceful URLs, HTTP caching/CDN semantics, file uploads, third-party/public consumers*.
Those consumers expect **OpenAPI**. So an HTTP contract is not redundant with the JSON-RPC TypeScript
client — it completes the HTTP transport's own value proposition and unlocks the entire off-the-shelf
client-generation ecosystem (Kiota, `openapi-typescript`, NSwag) instead of a bespoke generator we own.

Three facts made the Microsoft stack the obvious base rather than a from-scratch generator:

- The repo targets **`net10.0`** with `JsonSerializerIsReflectionEnabledByDefault=false`.
  `Microsoft.AspNetCore.OpenApi` 10.x is **AOT/trim-compatible for minimal APIs** — Microsoft rebuilt it
  precisely to avoid Swashbuckle/NSwag's reflection.
- The generated endpoints were already ~50% OpenAPI-shaped: `AppModuleDiscoveryGenerator` emits
  `.WithName`, `.WithDescription`, `.Produces<T>(200)`/`.Produces(204)`, and `.ProducesElarionErrors()`
  (validation problem + 401/403/404/409/422/500 ProblemDetails).
- OpenAPI schema generation in net10 uses the same `System.Text.Json` `JsonSchemaExporter` the JSON-RPC
  exporter already uses, so feeding it the canonical Elarion options yields schema shapes consistent across
  both transports.

"Parity" explicitly includes **idempotency**. The JSON-RPC schema emits a per-method `idempotent` flag and
the TypeScript client auto-attaches an idempotency key for those methods. For HTTP the server side already
works (`UseElarionIdempotencyKey` captures the `Idempotency-Key` header into the pipeline
`IdempotencyDecorator`); what was missing was only *advertising* which operations are idempotent and letting
the client *auto-attach* the key.

## Decision

Ship a thin opt-in sibling package, **`Elarion.AspNetCore.OpenApi`**, that wraps
`Microsoft.AspNetCore.OpenApi`. Do **not** build a custom OpenAPI generator or a bespoke HTTP client
generator. Elarion owns only the ~20% Microsoft cannot:

1. **Canonical JSON wiring (the core value).** OpenAPI schema generation, minimal-API **request-body binding**,
   and the **success response** (`ElarionHttpResults.ToResult` returns `TypedResults.Ok`) all read
   `Microsoft.AspNetCore.Http.Json.JsonOptions` — a base-transport concern, not an OpenAPI one: under
   **reflection off** the `[HttpEndpoint]` transport can't (de)serialize its DTOs if those options carry no
   source-gen resolver. So the wiring lives in the base HTTP transport as **`AddElarionHttpJson()`**
   (`Elarion.AspNetCore`), which mirrors the canonical `IElarionJsonSerialization` configuration — naming knobs
   and the source-generated `TypeInfoResolverChain` — onto those options; `AddElarionOpenApi()` calls it (and any
   `[HttpEndpoint]` host can call it directly). A type missing from every context then throws (surfacing a missing
   `[JsonSerializable]`) rather than silently reflecting. Serializing both directions through the one aligned
   options object (rather than a custom response serializer) keeps requests and responses consistent — including
   under a host override. The alignment is a deliberate, **global** change to the app's minimal-API JSON, runs in
   registration order, and is overridable — a host that needs different behavior calls `ConfigureHttpJsonOptions(…)`
   after it and wins. By default it equals canonical, so REST output matches JSON-RPC/MCP for the same DTO.
2. **Module tags.** The generator now emits `.WithTags("{Module}")` on each endpoint (it alone knows the
   longest-prefix `[AppModule]` match), so OpenAPI groups operations by module — the REST analog of the
   JSON-RPC module grouping.
3. **Operation-id normalization.** A document transformer strips the namespace and a trailing
   `Handler`/`Endpoint` suffix (`Billing.Invoicing.GetInvoice` → `GetInvoice`) so generated clients get
   readable method names, de-colliding by keeping the original id if two would collapse to the same name.
4. **Idempotency contract.** The generator marks `[Idempotent]` endpoints with an inert
   `ElarionIdempotentEndpointMetadata`; an operation transformer reads it and adds an optional
   `Idempotency-Key` header parameter plus an `x-elarion-idempotent: true` vendor extension — the OpenAPI
   analog of the JSON-RPC schema's `idempotent` flag, which is what lets a generated client auto-attach.

Client generation is **off-the-shelf**: the recommended toolchain is `openapi-typescript` +
`openapi-fetch` (portable `fetch`, small deps, optional Zod — matching the JSON-RPC client's ethos), with a
small reusable `openapi-fetch` middleware that reads `x-elarion-idempotent` and auto-attaches the
`Idempotency-Key` header, giving the same exactly-once-on-retry behavior as the JSON-RPC client. Kiota
(first-party, polyglot) and `@hey-api/openapi-ts` are documented alternatives.

Build-time export **reuses Microsoft's** `Microsoft.Extensions.ApiDescription.Server`
(`OpenApiGenerateDocuments=true`), which boots the host with an inert server to dump the document — the
same shape as the JSON-RPC schema tool. We do **not** ship an Elarion MSBuild package for OpenAPI.

### Reconciling the two build-time exporters

An audit of `Elarion.AspNetCore.SchemaGeneration` against `Microsoft.Extensions.ApiDescription.Server`
found them already structurally aligned — both are `GetDocument.Insider`-style host-launch tools with a
document-list cache, a `GetDocuments` target, and a `ProjectCapability`. The only material differences are
cosmetic branded property names and the default output directory (Microsoft defaults to the project
directory + `{Project}.json`; Elarion defaults `rpc-schema.json` to `obj/`). Because there is no
*functional* inconsistency, we do not rename Elarion's properties or change its defaults (a disruptive break
for marginal gain, pre-1.0 or not). Instead we document the property mapping and configure both exports to a
shared, committed location in guidance. Microsoft remains the reference; had a big inconsistency existed we
would have adjusted the Elarion-owned tooling to match it, not the other way around.

## Consequences

- HTTP consumers get a standard OpenAPI 3.x document and the full client-gen ecosystem; the same handler can
  be a REST endpoint, a JSON-RPC method, and an MCP tool at once, now all three with a machine-readable
  contract.
- The package is an **opt-in sibling** (ADR-0017 shape): referencing it is what pulls
  `Microsoft.AspNetCore.OpenApi`. Core, Abstractions, and the base HTTP transport stay OpenAPI-free.
- The generator changes (`.WithTags`, the idempotent marker) are inert without the OpenAPI package — a host
  that never references it sees identical behavior.
- Schema shapes are consistent across transports because both go through `System.Text.Json`'s schema
  exporter over the same canonical options.
- **Trade-off / non-goal:** we depend on Microsoft's OpenAPI implementation and the third-party client-gen
  ecosystem rather than owning an HTTP client generator. This is deliberate — reinventing either would be
  wasted effort and a maintenance burden, and `openapi-typescript`/Kiota/NSwag are mature. Build-time export
  requires the host not to do side-effecting work (e.g. database migration) before `app.Run()`, since the
  inert-server launch executes the program up to that point; hosts that migrate on startup should guard that
  work or export at runtime via `MapOpenApi()`.
