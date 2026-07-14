# ADR-0059: Merge the always-on tracing + context-enrichment decorators into one observability decorator

- Status: Accepted (implemented 2026-07-14)
- Date: 2026-07-14
- Related: [ADR-0033](0033-user-context-trace-and-log-enrichment.md) (introduced the context-enrichment
  decorator; this ADR reverses its "fold into tracing" rejected alternative on new grounds),
  [ADR-0034](0034-abstractions-holds-contracts-not-implementations.md) (moved every pipeline decorator out
  of `Elarion.Abstractions` into `Elarion` core — the change that makes this merge clean).

## Context

Every generated handler — even a bare `[Handler]` with no gate attributes — is wrapped by **two always-on
decorators**, applied by `HandlerRegistrationGenerator` innermost-to-outermost as the last two build steps:

1. `HandlerContextEnrichmentDecorator` (ADR-0033) — runs the registered `IHandlerContextEnricher`s, tags the
   span, opens one log scope.
2. `TracingDecorator` — starts the handler span and records the execution metric; emitted last, so it is the
   outermost decorator and its span parents everything below.

They are **always both present and always adjacent** (tracing outermost, enrichment directly inside — nothing
sits between them), so the decorator chain always begins `… → context-enrichment → tracing` from the handler's
point of view.

The `HandlerPipelineBenchmarks` (default dispatch pipeline, M4 Pro, .NET 10, no listener) isolate Elarion's
fixed per-request cost as `ResolveScopedAndCall − ScopeAndBareHandler`:

- `ScopeAndBareHandler` ≈ **336 B** — the DI request scope + the scoped handler instance, which a minimal API
  pays for *any* handler, decorators or not.
- `ResolveScopedAndCall` ≈ **496 B** before this change — the same, plus the decorated chain rebuilt per
  resolution.

`HandlerMetadata` is already a static-readonly singleton per handler type, and the resolved-pipeline step list
is collected only on the first resolution, so the **two decorator objects rebuilt on every resolution** are the
remaining reducible surface on this path.

## Decision

Merge the two always-on decorators into a **single `ObservabilityDecorator<TRequest, TResponse>`** in
`Elarion.Pipeline`, replacing `TracingDecorator` and `HandlerContextEnrichmentDecorator`. The generator emits
one `ObservabilityDecorator` as the outermost decorator (one pipeline step instead of two).

Its `HandleAsync` preserves the exact former behavior, in the exact former order: start the handler span
(`HandlerTelemetry.Source`, `HasListeners()` fast path) → run the enrichers (tagging `Activity.Current`, which
is that span) → open the log scope around the inner call → await inner → record the `ok`/`error`/`exception`
outcome tag, `ActivityStatusCode`, the exception `ActivityEvent`, and `HandlerTelemetry.RecordExecution`. The
empty-enrichers / empty-scope / no-logger / no-listener fast paths are all retained (each remains a straight
pass-through), and enricher execution stays inside the try that records the `exception` outcome — a throwing
enricher is attributed identically to before.

**The behavior lives in a static `HandlerObservability.InvokeAsync(...)` core**; `ObservabilityDecorator` is a
thin wrapper that only carries the per-request `inner` reference and the per-instance rendered-pipeline-tag
cache, and delegates to the static method. This is the shape a stateless decorator wants: the *behavior* is
allocation-free and callable from anywhere that already holds the inner handler; only the per-request state
(the `inner` pointer) forces a wrapper object.

### Why this reverses the ADR-0033 rejected alternative

ADR-0033 explicitly listed "fold enrichment into `TracingDecorator`" as a rejected alternative, for two
reasons. Both are now resolved:

- **"It forces `Microsoft.Extensions.Logging.Abstractions` onto the deliberately-minimal `Elarion.Abstractions`
  package"** — *moot since [ADR-0034](0034-abstractions-holds-contracts-not-implementations.md)*, which moved
  `TracingDecorator` (and every other pipeline decorator) out of Abstractions into `Elarion` core. Both
  decorators already live in `Elarion.Pipeline`, which already references `M.E.Logging.Abstractions`. The merge
  adds **zero** package dependencies.
- **"It mutates the byte-identical outermost tracer"** — addressed by extracting the shared, behavior-identical
  static core and keeping the observable output (spans, metrics, log scope, the `elarion.handler.pipeline` tag
  contents modulo the decorator's own name) unchanged, verified by the existing tracing/enrichment tests plus
  the generator byte-output tests.

At the time of ADR-0033 the merge bought nothing (both decorators were cheap and separate concerns read
cleanly); the new motivation is the measured fixed per-request allocation on the framework's hottest path, plus
the static-core reuse below.

## Consequences

- **One fewer allocation per request.** Two ref-type wrappers become one: the merge removes one object header
  (16 B) and one redundant `inner` pointer (8 B). Measured: `ResolveScopedAndCall` **496 B → 472 B** (the
  Elarion delta over the bare-handler baseline drops 160 B → 136 B). This is a structural one-object saving, not
  a halving — neither wrapper was large; the remaining delta is the merged object plus the on-by-default
  `IHandlerContextEnricher` resolution, both inherent to enrichment being a default. `Decorated` with no
  listener still allocates **0 B**; attaching a listener still allocates only the `Activity` per call (tracing
  stays free until observed).
- **The `elarion.handler.pipeline` diagnostic tag** now lists one `Observability` entry where it previously
  listed `ContextEnrichment,Tracing`. The decorator is named `ObservabilityDecorator` (no `Handler` prefix) so
  it renders as `Observability`, consistent with every sibling (`Tracing`, `Authorization`, `Validation`, …).
- **Single build step, single responsibility framed as "observability."** Tracing, metrics, and context
  enrichment were never independently attachable (both always on, always adjacent); collapsing them matches how
  they actually compose and removes a layer of indirection on the hot path.
- **Behavior preserved across every transport.** JSON-RPC, HTTP, MCP, scheduler jobs, and event consumers see
  identical spans, metrics, and log scopes; anonymous/no-enricher executions stay inert.

## Deferred: a compile-time-composed handler (the larger optimization)

The static-core shape is deliberately reusable because the *general* version of this optimization is bigger and
belongs in its own change. Today a handler with N gate decorators (authorization, validation, feature-gate,
audit, …) allocates N wrapper objects per resolution; the runtime-composed chain costs one wrapper (or one
closure) per step by construction. The only way to reach O(1) is **compile-time** composition — which is
available here because `HandlerRegistrationGenerator` already emits per-handler code. A future ADR could have
the generator emit, per handler, a single sealed handler whose `HandleAsync` inlines each *stateless* decorator
body as a static `Apply` call (the way `HandlerObservability.InvokeAsync` is already written), collapsing the
gated chain to one object regardless of how many decorators apply. It is deferred because it is a much larger,
higher-risk refactor on the most-tested path, it must preserve the conditional-attachment semantics
(`AppliesTo`, soft-service audit/inbox) and the resolved-pipeline diagnostic, and the always-on path this ADR
addresses is the one every request pays. This merge is the first, safe step and proves the static-core pattern.

### Rejected alternatives

- **Keep the two decorators; accept the allocation.** The status quo. Rejected: the merge is behavior-neutral,
  removes a fixed per-request object on the hottest path, and reads at least as clearly (the two were never
  separable).
- **Name it `HandlerObservabilityDecorator`.** Rejected: `RenderPipeline` strips only the `Decorator` suffix, so
  that name renders as `HandlerObservability` in the `elarion.handler.pipeline` tag — inconsistent with every
  sibling decorator (none carries a `Handler` prefix). `ObservabilityDecorator` renders as `Observability`. The
  reusable static core keeps the descriptive name `HandlerObservability`.
- **Do the full compile-time-composed handler now.** Rejected for this change — see *Deferred* above.
