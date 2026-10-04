# ADR-0079: Every feature flag is declared and has exactly one owner

- Status: Proposed
- Date: 2026-10-04
- Related: [ADR-0016](0016-feature-flag-gating.md) (the handler gate and the thin flag seam),
  [ADR-0019](0019-variant-service-injection.md) (variant services), [ADR-0030](0030-client-capability-bootstrap.md)
  (the session snapshot), [ADR-0074](0074-reactive-client-capabilities.md) (client capabilities),
  [ADR-0075](0075-ambient-tenant-scoping.md) (the tenant a flag may target),
  [ADR-0006](0006-incremental-source-generator-conventions.md) (generator conventions),
  [ADR-0034](0034-abstractions-holds-contracts-not-implementations.md) (contracts versus implementations).

## Context

Feature flags are strings in three unrelated places. `[FeatureGate("x")]` names a flag on a handler,
`[ClientFeatures("x")]` names it again on a module for the frontend, and `[FeatureVariant("x")]` a third time on
a service. Nothing declares the flag: no description, no statement of who decides its value, no check that the
names agree. A typo is a flag that is silently off, in a gate that then answers 404 for everyone.

The value comes from one `IFeatureFlagService` that the host registers — in practice the OpenFeature-backed one,
so every flag, including a trivial "is this user on the beta roster" rule that would be three lines of C#, has to
be modelled in an external flag system. There is no place for a flag whose owner is the application. Making that
possible by letting several providers answer (a composite that asks each in turn and takes the first non-null)
trades one problem for another: the answer now depends on registration order, an undeclared name falls through
every provider, and "which system owns this flag" is unanswerable from the code.

Two further gaps. *Who is asking* is ambient: the OpenFeature service reads `ICurrentUser` from the DI scope, so
a flag cannot be evaluated for anyone but the current caller, and the targeting model (tenant, extra attributes)
is whatever one provider chose to map. And an exposed client flag is whatever a module lists, evaluated by
whatever the host wired, so the session snapshot and the handler gate agree only by coincidence.

## Decision

**A flag is declared once, and the declaration names its owner.**

1. **Two declaration forms, both under a module.**
   `[FeatureFlag("name", Description, ExposeToClient)]` on a class that implements `IFeatureFlagResolver` makes
   that class the owner (code-defined). `[BackendFeatureFlag("name", Description, ExposeToClient)]` on any class
   under the module hands evaluation to the host's `IBackendFeatureFlagEvaluator` (Microsoft.FeatureManagement
   or any OpenFeature provider). `[ClientFeatures]` is removed; `ExposeToClient` on the declaration replaces it.
2. **The generators build the catalog; nothing is scanned at run time.** The manifest generator publishes every
   declaration as assembly metadata, like the other manifest entries. A flag generator checks usages against the
   union of this assembly's declarations and its references' (`ELFLAG001` used but undeclared, `ELFLAG002`
   declared twice, `ELFLAG003` declared without an owner, `ELFLAG004` outside a module, `ELFLAG005` blank name),
   emits a per-module registration into the module's default services — so a flag exists exactly when its module
   is enabled — and, with `[assembly: GenerateFeatureFlags]`, the `ElarionFeatureFlags` registry with typed
   `FeatureFlagKey`s and name constants.
3. **Exactly one resolver per flag.** `IFeatureFlagService` looks the flag up in the `IFeatureFlagCatalog` and
   dispatches to its owner. There is no composite, no precedence, no fallback, no backend marker interface. The
   backend is the resolver for backend-declared flags only; a host that declares such flags and registers no
   backend fails at startup.
4. **Evaluation context is explicit.** Owners receive a `FeatureEvaluationContext` (user id, roles, tenant id,
   attributes, services) instead of reading the DI scope. `IFeatureFlagService.CreateContext()` builds the ambient
   one from `ICurrentUser` and `ITenantContext`; every overload also accepts a context, so a flag can be evaluated
   for another subject. `[FeatureGate]`, the session snapshot and application code all evaluate through the same
   service and catalog.
5. **Variants are inside the model.** `IFeatureVariantService` is folded into `IFeatureFlagService`
   (`GetVariantAsync`); the owner of a flag allocates its variant, and `[FeatureVariant]` must name a declared
   flag (`ELFLAG001`). Configuration variants are a different axis with no flag and are unchanged.
6. **An unknown name at run time is disabled and logged once** — undeclared, or declared by a disabled module.
   Typed keys make that path rare; the compiler removes it for gates.

## Consequences

- A typo in a gate, a variant or an exposed client flag is a build error. Every flag has a description, an
  exposure decision and an owner, readable from the catalog.
- A rule that belongs in application code is a small resolver class with injectable dependencies, not a flag
  system entry; a rule that belongs to product/ops stays in the backend. Neither can shadow the other.
- Per-subject evaluation (admin preview, jobs acting for a user) and tenant targeting need no scope tricks.
- The session snapshot's flag vocabulary and the schema export's `capabilities.modules[*].features` now come from
  the runtime catalog, so they cannot drift from the gates. The wire shapes are unchanged.
- Cross-assembly checks rely on the declaring assembly being the gating assembly or one it references.

**Breaking changes.** `[ClientFeatures]` and `ClientModuleManifest.Features` are removed;
`IFeatureVariantService` is removed and `IFeatureFlagService` gains context overloads, `GetVariantAsync` and
`CreateContext`; `OpenFeatureFeatureFlagService`/`OpenFeatureFeatureVariantService` are replaced by
`OpenFeatureFlagEvaluator` (`IBackendFeatureFlagEvaluator`); `ElarionEvaluationContext.Create` takes a
`FeatureEvaluationContext`; `SessionHandler` takes the catalog; the schema tool and `JsonRpcSchemaExportOptions`
gain `FeatureFlags`. Every flag a handler, variant or module uses must now be declared.

## Rejected alternatives

- **A composite provider (first non-null wins) with a backend marker interface and "adopt plain registration"
  logic**, as in the earlier compatibility-driven branch. It kept string flags undeclared and let the answer
  depend on registration order; an unknown name fell through every provider; and the adoption logic existed only
  to preserve old registrations. Declaring the owner removes the question instead of ordering it.
- **Keep `[ClientFeatures]` beside the declarations.** A second list of names that must agree with the first is
  the original defect; exposure is a property of the flag.
- **Scan assemblies at startup for resolver classes.** Reflection scanning, trimming-hostile and invisible to the
  compiler; the manifest already carries cross-assembly facts.
- **Leave variants out of the model.** They would remain the one place a flag name is an unchecked string.
- **Keep ambient evaluation.** It forbids evaluating for another subject and hides the targeting inputs.
