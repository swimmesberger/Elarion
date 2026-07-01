# ADR-0016: Feature-flag gating (`[FeatureGate]` over an OpenFeature-backed seam)

- Status: Accepted
- Date: 2026-06-29
- Related: [ADR-0009](0009-authorization-building-blocks.md) (the sibling declarative gate this mirrors),
  [ADR-0004](0004-handler-result-caching.md) (the seam-in-Abstractions / impl-in-package precedent),
  [ADR-0017](0017-dependency-light-core.md) (provider defaults are opt-in packages),
  the [feature-flags concept doc](../concepts/feature-flags.mdx) (usage).

## Context

A handler should be gateable behind a feature flag — declaratively, per handler, evaluated at run time so a
flag drives gradual rollouts, targeting, and kill switches without a redeploy. This is distinct from
**module** feature flags (`Modules:{Name}:Enabled`), which are compose-time: a disabled module is never
registered. A per-handler gate must leave the handler registered and decide per call.

The framework already composes a per-handler **decorator pipeline** at compile time, and already has the exact
shape of this problem solved once for authorization (ADR-0009): a class-level attribute, a generated decorator
auto-attached as a functional gate, short-circuiting with an `AppError`. Feature gating is the same shape.

Two questions shaped the design:

1. **Do we need our own abstraction, or is Microsoft's `IFeatureManager` enough?** The same question the cache
   subsystem faced with `HybridCache` (ADR-0004), which was answered by introducing the `IHandlerCache` seam.
2. **Which backend?** Microsoft.FeatureManagement, OpenFeature, LaunchDarkly, ConfigCat, Unleash, and Flagsmith
   all have different client shapes; the user asked to be able to plug any of them.

## Decision

### A thin Elarion seam, not `IFeatureManager` directly

The `[FeatureGate]` attribute and the `FeatureGateDecorator` are part of the handler pipeline, so they **must**
live in `Elarion.Abstractions` — which is `IsAotCompatible` and must not depend on any runtime integration
package. It therefore *cannot* reference `Microsoft.FeatureManagement`, exactly as it cannot reference
`HybridCache`. So a minimal Elarion-owned seam is forced:

```csharp
public interface IFeatureFlagService {
    ValueTask<bool> IsEnabledAsync(string feature, CancellationToken ct = default);
}
```

It deliberately exposes only the boolean enablement question — the 80% case — keeping it AOT-clean and
provider-agnostic. Variants/multivariate evaluation are out of scope for the gate. This mirrors `IHandlerCache`:
a small seam in Abstractions, a concrete backend one layer up.

### Target OpenFeature, with Microsoft.FeatureManagement as the default provider

The default `IFeatureFlagService` is backed by **OpenFeature's `IFeatureClient`**, not by `IFeatureManager`
directly. OpenFeature is itself the vendor-neutral standard — its `FeatureProvider` model already plugs every
major vendor behind one client — so targeting it once makes `[FeatureGate]` work against any provider.

Two opt-in packages ship:

- **`Elarion.FeatureFlags.OpenFeature`** — `OpenFeatureFeatureFlagService : IFeatureFlagService` over `IFeatureClient`, plus
  the `ICurrentUser` → `EvaluationContext` mapping (`ElarionEvaluationContext`). Depends only on `OpenFeature`
  core. `AddElarionOpenFeature()` registers the service; the host brings the provider via `AddOpenFeature(...)`.
- **`Elarion.FeatureFlags.FeatureManagement`** — the batteries-included default. `AddElarionFeatureManagement(config)` wires
  the `OpenFeature.Contrib.Provider.FeatureManagement` provider (which is built on the current
  `IVariantFeatureManager`) and `AddElarionOpenFeature()`. It is one line of sugar over the base, isolating the
  Microsoft.FeatureManagement dependency so it is pulled only when used.

### Targeting is ambient, off-HTTP

`ElarionEvaluationContext` maps `ICurrentUser` (user id + roles) into the OpenFeature context — the user id as
the standard `TargetingKey` **and** as the `UserId`/`Groups` attributes the Microsoft.FeatureManagement provider
reads — so targeting/percentage rollouts work transport-neutrally with no `HttpContext` (the off-HTTP analog of
MS's `DefaultHttpTargetingContextAccessor`).

### Gate semantics and generation

- The attribute (`Elarion.Abstractions.Features`) is class-level, `AllowMultiple`, shaped like MVC
  `[FeatureGate]`: `(params string[] features)` plus a `(FeatureRequirement, params string[])` overload and a
  `Negate` named arg. `FeatureRequirement` is `All` (default) or `Any`. Multiple attributes AND.
- `HandlerRegistrationGenerator` auto-attaches `FeatureGateDecorator<TRequest, TResponse>` **just inside the
  authorization gate** (so authorization runs first and a disabled feature never reaches caching/pipeline/handler).
  It validates the response implements `IResultFailureFactory<TResponse>` (else **`ELFEAT001`**, an error, like
  ELAUTH001) and reports a gate with no/blank feature name (**`ELFEAT002`**, a warning).
- A closed gate returns `AppError.NotFound` with a **generic** message — echoing the feature name would leak the
  thing the 404 is meant to hide. The decorator reads gates off the concrete handler type via `HandlerMetadata`
  (cached per type), never `inner.GetType()`, so it is correct at any chain position.

## Consequences

**Positive**

- `[FeatureGate]` is transport-neutral and identical under JSON-RPC, MCP, and HTTP; it composes with the rest of
  the pipeline through one deterministic generated registration.
- The backend is swappable without touching handlers: OpenFeature reaches the whole vendor ecosystem, and a
  custom `IFeatureFlagService` registration replaces it wholesale (e.g. a test double).
- `Elarion.Abstractions` and `Elarion` core stay free of any feature-management dependency.

**Negative / accepted**

- The seam is boolean-only; multivariate/variant flags need a dedicated accessor, not `[FeatureGate]`.
- The shipped Microsoft.FeatureManagement OpenFeature provider is an early-stage (`0.x-preview`) community
  package; it is isolated to the optional `Elarion.FeatureFlags.FeatureManagement` package, and any other OpenFeature
  provider is a drop-in alternative.
- Targeting flows through OpenFeature's `EvaluationContext`; a provider that ignores the context (or the MS
  provider's preview limitations) limits targeting fidelity — a provider concern, not a gate concern.

## Implementation

- Seam, attribute, decorator: `src/Elarion.Abstractions/Features/`
  (`IFeatureFlagService`, `FeatureGateAttribute`, `FeatureRequirement`, `FeatureGateDecorator`).
- Generation: `HandlerRegistrationGenerator.Discovery.cs` (`ParseFeatureGates`) and `.Emit.cs`
  (`AppendFeatureGateDecorator`); diagnostics `ELFEAT001`/`ELFEAT002` in `.Models.cs`.
- OpenFeature backend: `src/Elarion.FeatureFlags.OpenFeature/` (`OpenFeatureFeatureFlagService`, `ElarionEvaluationContext`,
  `AddElarionOpenFeature`).
- Microsoft.FeatureManagement default: `src/Elarion.FeatureFlags.FeatureManagement/` (`AddElarionFeatureManagement`).
