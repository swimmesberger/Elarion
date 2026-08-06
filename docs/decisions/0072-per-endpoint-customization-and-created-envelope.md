# ADR-0072: Per-endpoint HTTP customization hook and the created-resource envelope

- Status: Accepted
- Date: 2026-08-01
- Related: [ADR-0071](0071-generator-owned-http-endpoint-binding.md) (the registration shape the hook attaches
  to), [ADR-0040](0040-host-declared-module-endpoints.md) (module-level endpoint hooks), [ADR-0039](0039-binary-file-responses.md)
  (the `ElarionFile` envelope precedent), and the
  [http-endpoints](../capabilities/transports/http-endpoints.mdx) capability page.

## Context

A review of the ecosystem's attribute-driven handler-to-minimal-API mappers — source generators occupying
the same design point as `[HttpEndpoint]` — surfaced two genuine gaps in Elarion's HTTP transport; the rest
of the surveyed feature space is either already present or conflicts with framework invariants (a
per-handler result transform would fork the central `AppError` → RFC 7807 contract; reading
`[Authorize]`/`[AllowAnonymous]` off handlers would split authorization across two mechanisms;
module-orthogonal route groups would blur "modules are the single hosting path").

The gaps:

1. **Per-endpoint conventions had no seam.** The capability page said "per-endpoint policy is out of scope —
   a handler that needs a different policy belongs in its own module or the hand-written `MapEndpoints`
   hook." That escape hatch forfeits generated binding, ProblemDetails translation, the OpenAPI shape
   method, and duplicate-route checking — all to add one `.RequireRateLimiting()` or an endpoint-specific
   authorization policy.
2. **Success responses could not express creation semantics.** The generated mapping produced `200`/`204`
   only; a resourceful POST could not answer `201 Created` with a `Location` header without leaving the
   generated path entirely.

## Decision

**A handler may declare `public static void CustomizeEndpoint(IEndpointConventionBuilder)`.** The generated
registration captures the builder and calls the hook after the emitted metadata chain, so handler-declared
conventions land last. It is the same convention-hook shape as a module's `MapEndpoints`/
`ConfigureEndpointGroup`, one level finer:

- The parameter is `IEndpointConventionBuilder`, not `RouteHandlerBuilder` — ADR-0071's `RequestDelegate`
  registrations return the interface.
- The method must be `public static void` with exactly that one parameter. A method named
  `CustomizeEndpoint` with any other shape warns `ELHTTP006` and is ignored — never silently skipped.
  `public` is required because the call site is emitted into the referencing host's compilation, the same
  visibility a cross-assembly handler already needs for its request/response DTOs.
- Declaring the hook opts the handler's assembly into an ASP.NET Core reference — the same accepted,
  deliberate trade as `[FromRoute]`/`IFormFile` binding opt-ins. A strictly web-free module assembly keeps
  using the module group hook or an ADR-0040 `[ModuleEndpoints]` contributor.
- The hook type rides the existing `Elarion.Manifest.HttpEndpoint.v2` entry as a count-gated appended field
  (the RpcMethod `OnConnection` precedent), so older module assemblies decode unchanged with no hook.
- The hook attaches *host/transport conventions* (policies, rate limiting, output caching, metadata).
  Business authorization stays in the handler pipeline (`[Require*]`); the generator still never reads
  `[Authorize]`/`[AllowAnonymous]` off a handler.

**A handler may declare `Result<ElarionCreated<T>>` as its response.** The envelope follows the
`ElarionFile` precedent — declaring the semantic once on the response type, letting each transport do its
best with it:

- The generated HTTP mapping peels the envelope at compile time: the translation is `ToCreatedResult`
  (`201`, optional `Location` header from the envelope, body = inner value), and the emitted
  `ProducesResponseTypeMetadata` advertises `201` with the **inner** type, so the OpenAPI document stays
  truthful and the envelope never appears on the HTTP wire.
- Failures are untouched: the central `AppError` → RFC 7807 translation applies unchanged.
- The name-routed JSON surfaces have no status code to express, so there the envelope serializes as a plain
  object (`value` + `location`). Operations designed for JSON-RPC/MCP should prefer a plain response type.

## Alternatives considered

- **A per-handler result-transform hook** (a static method turning the handler's success value into a
  custom `IResult` per endpoint). Rejected — it lets any handler fork the
  canonical error contract and makes the generator-emitted response metadata unverifiable. Outcome-dependent
  response construction beyond the declarative envelopes stays in the hand-written `MapEndpoints` escape
  hatch, which is strictly more capable.
- **A `SuccessStatus` knob on `[HttpEndpoint]`.** Rejected as incomplete — a status alone cannot carry the
  `Location` (or a future `ETag`), which is per-response data. The envelope carries values, not just codes.
- **Reading host authorization attributes from handlers.** Rejected — authorization is declarative,
  transport-neutral, and enforced in the handler pipeline; HTTP policy attaches at the module group or,
  now, the per-endpoint hook — both explicitly host-facing seams.
- **Strongly-typed route groups.** Rejected — a second, module-orthogonal grouping axis.
  The module group hook plus the per-endpoint hook covers the observed use cases.

## Consequences

- Per-endpoint conventions no longer force a handler off the generated path; the last documented reason to
  hand-map an otherwise ordinary handler disappears.
- One new warning (`ELHTTP006`); the manifest gains an appended field with no key bump; hook-less endpoints
  emit byte-identical text to before.
- `ElarionCreated<T>` joins `ElarionFile` as the second response envelope the HTTP emission special-cases;
  both are recognized by fully-qualified name through `ElarionGeneratorConventions`, shared by the manifest
  and bootstrapper generators so the two discoveries cannot drift.
