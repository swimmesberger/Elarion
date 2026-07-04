# ADR-0033: User-context trace and log enrichment

- Status: Accepted
- Date: 2026-07-04
- Related: [ADR-0034](0034-abstractions-holds-contracts-not-implementations.md) (the enrichment decorator lives in
  `Elarion.Pipeline` under the rule that all pipeline decorators are in core),
  [ADR-0017](0017-dependency-light-core.md) (the dependency-light-core boundary),
  [ADR-0009](0009-authorization-building-blocks.md) / [ADR-0016](0016-feature-flag-gating.md) (the
  generator-attached pipeline-decorator pattern this reuses and the `ICurrentUser` seam it reads),
  [ADR-0006](0006-incremental-source-generator-conventions.md) (the incrementality conventions the emit change
  preserves).

## Context

Elarion already instruments every handler: `HandlerRegistrationGenerator` wraps each handler in a
`TracingDecorator` (the outermost decorator) that opens one `handle {handler}` span through
`HandlerTelemetry` and records execution metrics. But that span carries only bounded, operation-level tags —
`elarion.handler`, `elarion.handler.request_type`, `elarion.handler.outcome` — and **no caller identity**.
Nothing in the framework opens an `ILogger` scope, so no log line emitted during a handler execution carries
the user either. A common diagnostic need — "show me this user's requests" / "filter these logs by user" — is
unmet.

The well-known ASP.NET recipe for this is a middleware that reads `HttpContext.User` after authentication, does
`Activity.Current?.SetTag("user.id", …)`, and wraps `next()` in a `logger.BeginScope`. That shape is
**structurally HTTP-only**: it reads `HttpContext` and lives in the ASP.NET request pipeline. Elarion runs
handlers under five transports — JSON-RPC, `[HttpEndpoint]`, MCP, scheduler jobs, and event consumers — and
four of them never touch `HttpContext`, so a middleware would enrich a fraction of the execution surface and
silently miss the rest.

Elarion already has the two pieces the recipe needs, in transport-neutral form:

- **Identity is seam-provided and scope-seeded before the pipeline runs.** `ICurrentUser` (`UserId`, `Roles`,
  `GetClaimValues`, …) is populated for every transport at its boundary — `CurrentUserScopeInitializer` for the
  dispatch-scope transports, `CurrentUserMiddleware` for `[HttpEndpoint]` — so it resolves inside the handler DI
  scope by the time any decorator runs.
- **The span is already there, uniformly.** The generator-attached `TracingDecorator` opens the handler span on
  every transport, because every transport funnels its handler through the one generated pipeline.

The gap is that these are never joined. The **handler decorator pipeline** — not an ASP.NET middleware — is the
altitude at which Elarion places cross-cutting concerns precisely because it is where all transports converge.

One dependency fact constrains the design. A `BeginScope` log scope needs
`Microsoft.Extensions.Logging.Abstractions`. `Elarion` (core) already references it; `Elarion.Abstractions` —
where `TracingDecorator` lives — deliberately references only the configuration and DI abstractions
(ADR-0017). Trace-tag enrichment needs no new dependency in either package (`Activity.SetTag` is
`System.Diagnostics`, in the BCL).

## Decision

Add **user-context trace + log enrichment** as a first-class, default-on, extensible stage of handler tracing.

**A contributor seam in `Elarion.Abstractions` (BCL-only).** `IHandlerContextEnricher.Enrich(HandlerEnrichmentContext)`
is the extension point; `HandlerEnrichmentContext` is a sink with `SetTag(key, value)` (trace tags, OpenTelemetry
semantic-convention keys) and `AddScopeItem(key, value)` (log-scope items, PascalCase keys). It accumulates
nothing until first written, so a no-contribution run allocates nothing. Placing the seam in Abstractions matches
every other seam (`IAuthorizer`, `IFeatureFlagService`, `ICurrentUser`) and adds no dependency.

**A decorator in `Elarion` core (`Elarion.Pipeline`).** `HandlerContextEnrichmentDecorator<TRequest, TResponse>`
runs every registered `IHandlerContextEnricher` (`sp.GetServices<…>()`) into one `HandlerEnrichmentContext`, then
applies the accumulated tags to `Activity.Current` and opens a single `ILogger.BeginScope` carrying the
accumulated items around the inner handler. It knows nothing about "user" — it drains whatever the enrichers
contribute. It lives in core because the log scope needs `Microsoft.Extensions.Logging.Abstractions` (which only
core references) — one instance of the broader rule that **all pipeline decorators live in core, not
Abstractions** (ADR-0034); this keeps it alongside `TracingDecorator` and the other gates and adds **zero new
package dependencies** (no OpenTelemetry SDK anywhere).

**Attached just inside `TracingDecorator`, unconditionally.** The generator emits it immediately before
`AppendTracingDecorator` (inner→outer build order), making it the second-outermost wrapper:
`tracing → context enrichment → authorization → feature gate → validation → …`. This position is load-bearing:
it runs *inside* the tracing span (so `Activity.Current` is the handler span it tags), and its `BeginScope` wraps
the authorization/validation/handler chain (so a denied or invalid request is still attributed to its caller).
Like tracing, it is emitted for every handler with no opt-in attribute.

**Default-on user context, privacy-safe.** The built-in `UserContextEnricher` is itself a first-class
`IHandlerContextEnricher`, registered by default when current-user support is added (`AddElarionClaimsCurrentUser`)
— so the framework dogfoods its own seam. It emits, from
`ICurrentUser`, `user.id` + `user.roles` + `user.permissions` (permissions = claims of the
`AuthorizationOptions` permission claim type). `user.id`/`user.roles` are OpenTelemetry semantic-convention
attributes; `user.permissions` mirrors them (semconv has none). We emit `user.*` and **not** the deprecated
`enduser.*`. **Email is PII and off by default**; roles/permissions are bounded (`MaxItems`, default 16). User
identity is **never recorded on metrics** — `HandlerTelemetry` metric tags stay `{handler, request_type,
outcome}`; identity rides only the span and the log scope. Log-scope keys surface only when the host enables
`IncludeScopes` on its logging exporter.

**Two small opt-in surfaces.** `AddElarionUserContextEnrichment(o => …)` configures or disables the built-in
enricher (`Enabled`, `IncludeRoles`, `IncludePermissions`, `IncludeEmail`, `MaxItems`); calling it is not
required on a standard host (current-user wiring already registers the enricher with default options), only to
change the payload, disable it, or enable it for a custom `ICurrentUser`. `AddElarionHandlerContextEnricher<T>()`
registers a host enricher via `TryAddEnumerable` (scoped, so it may inject `ICurrentUser`), composing with the
built-in one rather than replacing it. The decorator soft-resolves its enrichers (`GetServices`), so it is a
straight pass-through when no enricher contributes anything (no current-user, anonymous caller, or the built-in
disabled), and never a resolution failure in a bare or test host.

## Consequences

- **Transport-neutral by construction.** One decorator enriches JSON-RPC, HTTP, MCP, scheduler, and
  event-consumer executions identically — the realization of the ASP.NET recipe that the recipe itself cannot
  reach. Anonymous executions (scheduler, after-commit delivery) run with `IsAuthenticated == false` and add
  nothing, which is correct attribution, not a coverage gap.
- **Costs are contained.** A second decorator layer is always emitted, but its inert path is a direct
  pass-through; only enriched (authenticated / has-enricher) executions allocate a context and a scope. The
  span-tag half is free of any host prerequisite; the log-scope half is inert unless the host opts into
  `IncludeScopes`. Permissions can be verbose, so they are bounded and individually disableable.
- **Boundaries held.** `Elarion.Abstractions` stays dependency-light (seam is BCL-only); `TracingDecorator` and
  `HandlerTelemetry` are untouched; both packages remain `IsAotCompatible` (concrete generic decorator, no
  reflection). Zero new package dependencies.
- **A pre-1.0 naming bet.** OpenTelemetry's `user.*` namespace is still evolving; if a key shifts we flip one
  constant and note it. We prefer shipping the current convention over the deprecated `enduser.*`.

### Rejected alternatives

- **Fold enrichment into `TracingDecorator`.** Smallest change, most literally "part of tracing", but it mutates
  the byte-identical outermost tracer and — because that decorator lives in `Elarion.Abstractions` — forces
  `Microsoft.Extensions.Logging.Abstractions` onto the most-referenced, deliberately-minimal package to carry the
  log scope. Rejected in favor of a single-responsibility decorator in core.
- **An ASP.NET enrichment middleware (the blog recipe).** HTTP-only; enriches neither JSON-RPC/MCP nor the
  scheduler/event-consumer executions. Rejected: wrong altitude for a five-transport framework.
- **Enrich at the dispatch-scope rail (a `UserContextScopeInitializer`).** The nearest generalization of the
  middleware, but unworkable: the handler span is not started at initializer time, `IDispatchScopeInitializer.Initialize`
  returns `void` so it cannot hold a `BeginScope` across the pipeline, and scheduler/event-consumer scopes do not
  run initializers at all. Rejected.
