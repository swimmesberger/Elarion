# ADR-0018: Variant service injection (transparent, via an opt-in async-resolving handler proxy)

- Status: Accepted
- Date: 2026-06-30
- Related: [ADR-0016](0016-feature-flag-gating.md) (the boolean feature-flag seam that anticipated a dedicated
  variant accessor), [ADR-0004](0004-handler-result-caching.md) (seam-in-Abstractions / impl-in-package precedent),
  the [feature-flags concept doc](../concepts/feature-flags.mdx) (usage).

## Context

Feature gating (ADR-0016) answers "is this on?". The next need is **variant service injection**: ship a different
service *implementation* to different users based on a feature flag's allocated **variant** (the A/B-tested
algorithm pattern) — the capability behind Microsoft's `WithVariantService<T>` / `IVariantServiceProvider<T>`, but
**provider-neutral** (Elarion targets OpenFeature) and with a **transparent** DX.

Two requirements shaped the design:

1. **The consuming handler must not differentiate.** It injects `IForecastAlgorithm` like any service; choosing the
   variant implementation for the current user is the framework's job. Only the *implementation* classes are
   variant-aware.
2. **Pay only when async resolution is needed.** Variant selection is an async flag evaluation, but DI constructor
   injection is synchronous.

The hard part is that tension. Options considered:

- **Sync-over-async bridge** in the contract's factory — rejected (silent deadlock risk; a wart).
- **Make the whole handler pipeline async** — rejected: there is no central choke point. Handlers are resolved at
  ~7 independent sites (`HandlerInvoker`, `HandlerSender`, the named-bus delegate feeding JSON-RPC/MCP, the HTTP
  minimal-API `[FromServices]` lambdas, the scheduler and event-consumer delegates), and the generated
  `IHandler<,>` factory is a synchronous `Func<IServiceProvider, IHandler>`. Converting all of that is a broad,
  risky change for a narrow need.
- **Proxy the variant contract** (an async proxy of `IForecastAlgorithm`) — clean and transitive-friendly, but it
  constrains the contract's members to be async (the proxy can only defer inside an `await`).
- **Opt-in async-resolving handler proxy** (chosen) — a general primitive that defers building one handler's
  pipeline to its first (async) call, so any async-resolved dependency can be `await`ed during construction.

## Decision

### A general async-resolving handler proxy (Layer 1)

`AsyncResolvedHandler<TRequest, TResponse>` (`Elarion.Abstractions.Pipeline`) wraps a build delegate and runs it on
the first `HandleAsync` (async), caching the result per scope. The handler-registration generator emits it **only
for handlers whose constructor depends on a variant contract**; every other handler keeps today's synchronous
registration byte-for-byte, so the cost is paid only when needed. The proxy is registered as the ordinary
`IHandler<,>`, so **no transport, dispatcher, or HTTP code changes** — they still resolve via
`GetRequiredService<IHandler<,>>()`. The generator emits the decorator chain once as a shared synchronous
`BuildPipeline(sp)`; the normal registration calls it directly, and a variant handler's async builder
(`BuildPipelineAsync(sp, ct)`) `await`s the variant pre-warm and then calls the same `BuildPipeline` — so the chain
is defined once and only the variant case goes through the proxy. DI still constructs the handler, so nothing
re-implements constructor resolution. Because the variant is fully resolved *before* construction and injected as a
normal instance, **the variant interface may have synchronous members** (an advantage over a contract-level proxy).

### The variant feature on top (Layer 2)

- **`IFeatureVariantService`** (`Elarion.Abstractions.Features`) — `ValueTask<string?> GetVariantAsync(string
  feature, ct)`. A *sibling* of the boolean `IFeatureFlagService` (ADR-0016 kept that seam frozen), implemented in
  `Elarion.FeatureFlags.OpenFeature` by reading `FlagEvaluationDetails<string>.Variant` (OpenFeature spec §1.4.6).
- **`[FeatureVariant("feature", Variant = "x")]`** on implementations (no `Variant` ⇒ the default) +
  `VariantServiceRegistrationGenerator`, emitting per contract: keyed implementation registrations, a
  `VariantServiceBinding<T>` (feature + default key), the imperative `IVariantServiceProvider<T>`, and the
  **transparent** unkeyed registration of the contract that reads the per-scope `VariantResolutionCache` the proxy
  warmed. Wired into modules via a new `AddVariantServices` `ConfigureDefaultServices` hook. Diagnostics `ELVAR001`,
  `ELVAR003`–`ELVAR007`. `[FeatureVariant]` is a **modifier on `[Service]`**, not a separate registration path: the
  impl must also carry `[Service]` (which declares the service, its contract(s), and lifetime; `ELVAR007` otherwise),
  and `ModuleServiceRegistrationGenerator` skips the plain registration for a `[FeatureVariant]` class so the keyed +
  transparent registration is the only one — keeping `[Service]` the single consistent way to register a service. The
  contract is **not repeated** on the attribute: it comes from the `[Service]` via the shared
  `ServiceContractResolver` (implemented interfaces, or explicit `[Service(typeof(IX))]`), so a service that
  registers under several interfaces is variant-resolved on each — making the previous generic `<TContract>`
  redundant. (`ELVAR002` "implementation does not implement its contract" is gone with the generic — the contract is
  always one the `[Service]` already provides.)
  A variant applies to a *service* a handler injects — not to the handler itself; handlers are selected by request
  type, so behaviour is varied via a strategy service, never by swapping the whole handler.
- The imperative **`IVariantServiceProvider<T>.GetAsync(ct)`** is the escape hatch (resolve outside a proxied
  handler, or re-evaluate per call).

### Detection is same-compilation; transitive/cross-assembly uses the provider

The generator detects a handler's *direct* constructor dependency on an in-compilation variant contract. A variant
reached only **transitively** (through another service) or from a **referenced assembly** is not auto-detected —
inject the contract into the handler directly, or use `IVariantServiceProvider<T>` (which works anywhere). A
cross-assembly contract manifest (the ADR-0014 pattern) is a possible future enhancement; it was deferred to keep
the generator change contained.

### Startup is not pre-warmed

Variant resolution is per-user/per-request and the pipeline is scoped, so there is nothing user- or scope-specific
to cache at startup — the proxy's laziness is correct, and provider readiness is already handled by OpenFeature's
hosted lifecycle. A startup *validation* (bindings + keyed impls registered) is a reasonable future hardening.

## Consequences

**Positive**

- Handlers stay transparent; only handlers with a variant dependency pay anything; all transports are untouched.
- Provider-neutral: works with any OpenFeature provider that surfaces the variant name; swapping providers never
  touches a handler.
- The async-resolving proxy is a reusable primitive for future async-resolved handler dependencies.

**Negative / accepted**

- **The shipped Microsoft.FeatureManagement OpenFeature provider does not surface the variant name.** Verified: the
  `OpenFeature.Contrib.Provider.FeatureManagement` `0.1.2-preview` evaluates the variant (returning its
  `configuration_value` as `.Value`) but leaves `FlagEvaluationDetails.Variant` null. So **variant service
  injection requires a native OpenFeature provider** (flagd, LaunchDarkly, ConfigCat, the in-memory provider) that
  populates `.Variant` per spec; it is unavailable through the Microsoft.FeatureManagement default. Boolean
  `[FeatureGate]` is unaffected. (Guarded by a regression test that will surface a future provider fix.)
- Transitive and cross-assembly variant injection are not auto-detected (use the imperative provider).
- First keyed-services use in the repo (net10's default container is keyed-capable).

## Implementation

- Proxy: `src/Elarion.Abstractions/Pipeline/AsyncResolvedHandler.cs`.
- Variant runtime: `src/Elarion.Abstractions/Features/` (`IFeatureVariantService`, `FeatureVariantAttribute`,
  `VariantServiceBinding`/`VariantServiceKeys`, `VariantResolutionCache`, `IVariantServiceProvider` /
  `DefaultVariantServiceProvider`, `VariantServiceCollectionExtensions`).
- Accessor impl: `src/Elarion.FeatureFlags.OpenFeature/OpenFeatureFeatureVariantService.cs`.
- Generation: `src/Elarion.Generators/VariantServiceRegistrationGenerator.cs` (requires `[Service]`; contracts via
  the shared `ServiceContractResolver`), the `[FeatureVariant]`-class skip in `ModuleServiceRegistrationGenerator`
  (also refactored onto `ServiceContractResolver`, no duplicated resolution), and the variant-dep
  detection + proxy emission in `HandlerRegistrationGenerator.{Discovery,Models,Emit}.cs`; the `AddVariantServices`
  hook in `ModuleDefaultsEmitter`.
