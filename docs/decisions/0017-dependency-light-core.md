# ADR-0017: Elarion core is dependency-light; provider defaults are opt-in packages

- Status: Accepted
- Date: 2026-06-29
- Related: [ADR-0004](0004-handler-result-caching.md) (caching seam/decorator in Abstractions),
  [ADR-0016](0016-feature-flag-gating.md) (the feature-flag seam that established the pattern cleanly),
  [the solution-structure concept doc](../concepts/solution-structure.mdx).

## Context

`Elarion.Abstractions` holds implementation-neutral contracts, attributes, and the pipeline decorators; it must
stay `IsAotCompatible` and free of runtime integration packages. `Elarion` core holds the runtime primitives.

Over time, two **provider-backed defaults** that carry concrete third-party runtime dependencies accreted into
`Elarion` core:

- `HybridHandlerCache` — the `IHandlerCache` default, pulling `Microsoft.Extensions.Caching.Hybrid`.
- `MicrosoftResiliencePipelineRunner` — the `IResiliencePipelineRunner` default, pulling
  `Microsoft.Extensions.Resilience` (Polly).

In both cases the **seam and the decorator already live in `Elarion.Abstractions`** (`IHandlerCache` +
`CacheDecorator`, `IResiliencePipelineRunner` + `ResilienceDecorator`); generated handler code references only
the Abstractions decorator and resolves the implementation from DI. Only the concrete, dependency-heavy default
sat in core. The consequence: every consumer of `Elarion` core transitively pulled HybridCache **and** Polly,
even if it used neither — the question "what *is* core, and why does using it drag these in?" had no clean
answer. Introducing the feature-flag backend (ADR-0016) forced the question, because doing it right meant *not*
repeating the mistake.

## Decision

**`Elarion` core depends only on the `Microsoft.Extensions.*` *Abstractions* packages** (Configuration,
DependencyInjection, Hosting, Logging abstractions). Every default that carries a concrete third-party/runtime
dependency lives in its own opt-in package, so the dependency is pulled only when the feature is used:

| Package | Provides | Heavy dependency |
| --- | --- | --- |
| `Elarion.Caching` | `HybridHandlerCache` + `AddElarionHandlerCaching` | `Microsoft.Extensions.Caching.Hybrid` |
| `Elarion.Resilience` | `MicrosoftResiliencePipelineRunner` + `AddElarionResilience` | `Microsoft.Extensions.Resilience` |
| `Elarion.FeatureFlags.OpenFeature` / `Elarion.FeatureFlags.FeatureManagement` | `IFeatureFlagService` backends | OpenFeature / Microsoft.FeatureManagement |

The moved files keep their original namespaces (`Elarion.Caching`, `Elarion.Resilience`), so consuming code
changes only its *package reference*, not its `using` directives.

### The resilience split

Resilience could not be a clean lift-and-shift because the **scheduler** (a core feature) depends on the
resilience subsystem. The split follows the dependency weight:

- **Stays in core (dependency-light):** the `IResiliencePolicyCatalog` in-memory catalog and policy *metadata*
  registration (`AddElarionResiliencePolicyCatalog`, `AddElarionResiliencePolicyMetadata`). The scheduler needs
  the catalog to resolve a job's retry policy; it carries no Polly dependency.
- **Moves to `Elarion.Resilience` (heavy):** the Polly-backed pipeline *runner* and `AddElarionResilience`.

`AddElarionScheduler` therefore registers the catalog, not the runner. The runner is resolved lazily and only
on the deferred/inline-retry path, so basic scheduling works without `Elarion.Resilience`; resilient execution
(and `[Resilient]` handlers) opts in by referencing the package and calling `AddElarionResilience()`.

## Consequences

**Positive**

- Core's dependency surface is now honest: referencing `Elarion` pulls only `Microsoft.Extensions.*`
  abstractions. "Use X ⇒ only then Y is pulled in" holds for caching, resilience, and feature flags alike.
- The package boundary is a rule, not an exception — the next provider-backed default has an obvious home.

**Negative / accepted**

- **Breaking package layout (pre-1.0, acceptable):** an app that uses handler caching now references
  `Elarion.Caching`; one that uses `[Resilient]` handlers or deferred scheduler retries references
  `Elarion.Resilience`. The registration calls and namespaces are unchanged.
- `AddElarionScheduler` no longer auto-wires the Polly runner. A host that relies on deferred-retry scheduling
  must add `Elarion.Resilience` + `AddElarionResilience()` (the Billing sample already does).

## Implementation

- `Elarion.csproj` drops `Microsoft.Extensions.Caching.Hybrid` and `Microsoft.Extensions.Resilience`.
- `src/Elarion.Caching/` and `src/Elarion.Resilience/` hold the moved defaults; `InMemoryResiliencePolicyCatalog`
  and the catalog/metadata registration stay in `src/Elarion/Resilience/`.
- `SchedulerServiceCollectionExtensions.AddElarionScheduler` calls `AddElarionResiliencePolicyCatalog()`.
