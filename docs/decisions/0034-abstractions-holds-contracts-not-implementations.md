# ADR-0034: Abstractions holds contracts, not implementations

- Status: Accepted
- Date: 2026-07-04
- Amends: [ADR-0017](0017-dependency-light-core.md) (dependency-light core — this refines *where* the pipeline
  decorators live)
- Related: [ADR-0009](0009-authorization-building-blocks.md) / [ADR-0016](0016-feature-flag-gating.md) /
  [ADR-0027](0027-declarative-request-validation.md) (the seam/impl-split concerns whose decorators move),
  [ADR-0033](0033-user-context-trace-and-log-enrichment.md) (the enrichment decorator that first forced the
  question by needing `ILogger`).

## Context

`Elarion.Abstractions` is meant to be the implementation-neutral contract layer — interfaces, attributes, markers,
result/data records — carrying no runtime-integration dependencies so any consumer (a contracts-only assembly, an
opt-in provider package) can reference it without pulling in behavior. In practice it also held **nine concrete
pipeline decorators** — `TracingDecorator`, `AuthorizationDecorator`, `ValidationDecorator`, `FeatureGateDecorator`,
`IdempotencyDecorator`, `ResilienceDecorator`, `CacheDecorator`, `CacheInvalidationDecorator`, `TransactionDecorator`
— plus `HandlerTelemetry`. Those are *implementation*: classes with control flow, not contracts. They fit in
Abstractions only because they happened to need nothing beyond Abstractions + the BCL; "dependency-light" was
conflated with "is an abstraction."

Two things brought this to a head:

- **A decorator needed a dependency Abstractions deliberately lacks.** The user-context enrichment decorator
  (ADR-0033) needs `Microsoft.Extensions.Logging.Abstractions` to open an `ILogger.BeginScope`. Abstractions
  references only the configuration and DI abstractions, so the decorator could not live there — it went to
  `Elarion` core. That left the pipeline **split**: eight decorators in Abstractions, one in core. Any future
  decorator that needs `ILogger`, `IConfiguration`, or another modest dependency would face the same wall.
- **The split had no upside.** Nothing in Abstractions constructs these decorators — the source generator emits
  them into the application assembly, which references `Elarion` core regardless (the generator is bundled there).
  No opt-in package references the decorator *classes* either; they implement and consume the seam *interfaces*,
  which stay in Abstractions. So the decorators being in Abstractions bought no reuse — only the illusion that the
  contract package contained behavior.

## Decision

**`Elarion.Abstractions` holds contracts only. Concrete pipeline behavior lives in `Elarion` core.**

- Move all nine pipeline decorators to `Elarion` core under the **`Elarion.Pipeline`** namespace, and
  `HandlerTelemetry` (the shared `ActivitySource`/meter + `RecordExecution` helpers) to `Elarion.Diagnostics`.
- `Elarion.Abstractions` keeps the seam interfaces (`IAuthorizer`, `IRequestValidator`, `IFeatureFlagService`,
  `IHandlerCache`, `IUnitOfWork`, `IIdempotencyStore`, `IHandlerContextEnricher`, …), the attributes, the result and
  data records (`Result`, `AppError`, `HandlerMetadata`, `HandlerEnrichmentContext`), and the source-gen triggers.
- The source generator emits the decorators by their new `Elarion.Pipeline.*` fully-qualified names. Nothing else
  about generation changes; the emitted pipeline order is unchanged.
- **`Elarion.Abstractions` grants `InternalsVisibleTo("Elarion")`.** The decorators use a few `internal` Abstractions
  helpers (e.g. the field-error flattener behind `AppError.Validation`); rather than widen those to public API, core
  — the reference implementation of the contracts — is trusted to see Abstractions internals. This is the standard
  abstractions/implementation pattern and keeps the public surface minimal.

The scope of *this* change is the pipeline decorators + `HandlerTelemetry`. Other implementation types still sitting
in Abstractions (`HandlerDispatcher`, the dispatch-scope rail, `AsyncResolvedHandler`, `VariantResolutionCache`,
`SchedulerTelemetry`/`EventTelemetry`) are a follow-up pass under the same rule; they are left in place here to keep
the diff coherent.

## Consequences

- **The rule is now uniform and future-proof.** "Contract → Abstractions; behavior → core (or an opt-in sibling)"
  is a single, checkable line. A new decorator that needs `ILogger` or `IConfiguration` has an obvious home and no
  longer forces a package-boundary debate. The ADR-0033 enrichment decorator stops being a lone exception.
- **`Elarion.Abstractions` gets smaller and truer.** A contracts-only assembly is lighter for a shared
  contracts library to reference, and the "no behavior here" invariant is easy to enforce in review.
- **Costs are mechanical and pre-1.0.** The decorators' public namespaces change (`Elarion.Abstractions.*` →
  `Elarion.Pipeline`), a breaking change for anyone referencing them directly — acceptable before 1.0 and mostly
  invisible (the generator emits the names; hosts rarely type them). The host-facing `HandlerTelemetry.ActivitySourceName`/`MeterName`
  move to `Elarion.Diagnostics`; the OpenTelemetry-registration docs and sample are updated. No new package
  dependency is added to any assembly.
- **`InternalsVisibleTo` couples core to Abstractions internals.** This is a deliberate, narrow grant to the one
  reference-implementation assembly, not a general escape hatch — the alternative (making internal helpers public)
  would pollute the contract surface for worse reasons.

### Rejected alternatives

- **Leave the decorators in Abstractions; add `Microsoft.Extensions.Logging.Abstractions` there.** Lets the
  enrichment decorator live beside the others, but pushes a dependency onto the most-referenced, deliberately-minimal
  contract package to satisfy one decorator, and entrenches "behavior in the contract layer." Rejected.
- **Keep the split (enrichment in core, the rest in Abstractions).** The status quo after ADR-0033. Rejected: an
  inconsistent, arbitrary line that the next dependency-needing decorator re-opens.
- **Move every implementation type out of Abstractions in this change.** The correct end state, but a much larger
  diff entangled with a feature PR. Deferred to a focused follow-up under the same rule.
