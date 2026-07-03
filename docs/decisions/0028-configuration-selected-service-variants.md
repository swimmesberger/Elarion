# ADR-0028: Configuration-selected service variants (`[ConfigurationVariant]`)

- Status: Accepted
- Date: 2026-07-03
- Related: [ADR-0019](0019-variant-service-injection.md) (feature-selected variant service injection — the
  substrate this ADR adds a second selection axis to), [ADR-0011](0011-runtime-settings-subsystem.md) /
  [ADR-0024](0024-postgres-listen-notify-settings-changes.md) (the runtime settings subsystem and its
  `IConfiguration` bridge — the flagship way to drive this axis at run time),
  [ADR-0017](0017-dependency-light-core.md) (why `Microsoft.Extensions.Options` stays out of
  `Elarion.Abstractions`), [ADR-0016](0016-feature-flag-gating.md) (feature gating and the OpenFeature default).

## Context

The motivating scenario: an admin configures a different service backend at run time — `Email:Backend =
office365` instead of `smtp` — and from the next unit of work on, consumers of `IEmailSender` must resolve
`Office365EmailSender` instead of `SmtpEmailSender`, cluster-wide, without a restart.

ADR-0019's variant service injection is the framework's primitive for "several implementations of one
contract, one selected at resolve time" — but its selection seam is `IFeatureVariantService`, which is
**asynchronous and ambient** (the allocated variant can differ per user). Two consequences follow from that
shape, and both are pure overhead when the selection is actually a process-global admin choice:

1. **The answer must be re-evaluated per scope**, because it may differ per caller. For a setting that changes
   perhaps once a year, every request still paid a flag evaluation — and with a settings-backed selector, a
   store read per scope.
2. **Because the evaluation is async but constructor injection is synchronous**, any handler injecting the
   contract must be wrapped in the `AsyncResolvedHandler` proxy that warms a per-scope
   `VariantResolutionCache` behind a semaphore. The generator cannot know a given feature is "really global",
   so it must always emit the pessimistic machinery; injecting the contract outside a proxied handler throws,
   and detection is same-compilation only.

Separately, the batteries-included flag backend (`AddElarionFeatureManagement`, the
`OpenFeature.Contrib.Provider.FeatureManagement` preview) does not surface variant *names*, so the
config-driven default had no variant story at all — running a dedicated flag service just to store one string
was the only supported path to a runtime backend switch.

The root cause is that **selection cadence was baked into the only selection seam**. To delete the proxy for
the global case, the cadence has to be visible to the generator — i.e. declared on the attribute.

## Decision

Model variant services as **one substrate with two selection axes**, declared by two sibling attributes:

- **`[FeatureVariant("Feature", Variant = "x")]`** — selected by *who is asking* (a feature flag's allocated
  variant): per-scope, asynchronous, warmed by the handler proxy. Unchanged from ADR-0019; the async tax is
  inherent to per-caller selection.
- **`[ConfigurationVariant("Key", Value = "x")]`** (new) — selected by *what is configured* (a plain
  `IConfiguration` value): process-global, **synchronous**, read live on each resolution.

Both are modifiers on `[Service]` (contract and lifetime come from `[Service]`, `ELVAR007` otherwise), share
the keyed-registration substrate, the default-fallback semantics, and the `IVariantServiceProvider<T>`
imperative accessor. A contract is selected by exactly one axis (`ELVAR008`).

For the configuration axis specifically:

1. **The transparent registration is a synchronous factory** — read `IConfiguration[key]`, resolve the
   value-keyed implementation, fall back to the default key (`ConfigurationVariantServiceProvider<T>.Resolve`).
   No `AsyncResolvedHandler`, no `VariantResolutionCache`, no semaphore: `HandlerRegistrationGenerator`
   deliberately excludes configuration-variant contracts from proxy wrapping, so a handler depending on one
   keeps the plain synchronous registration. The contract is injectable **anywhere** (services, singletons at
   their own peril, cross-assembly) — the "not warmed in this scope" failure mode is feature-axis-only.
2. **Selection is per DI scope.** The transparent registration defaults to scoped, so a unit of work pins its
   implementation at first resolution and the *next* scope observes a changed value — the same take-effect
   semantics as the feature axis, with in-flight work never seeing a mid-flight swap.
3. **`IConfiguration` is the selection substrate — not the settings API, not `IVariableSource`, not
   `IOptionsMonitor`.**
   - *Not the settings API:* `IConfiguration` is .NET's universal composition point — the same attribute works
     with static `appsettings.json` (switch = redeploy), `reloadOnChange` file config (ops edits switch at run
     time), environment variables, or any config provider. The Elarion settings subsystem participates as
     **just another provider** via the existing `AddElarionSettingsConfiguration()` bridge (ADR-0011), which is
     what makes the value admin-writable and — with `Elarion.Settings.PostgreSql` (ADR-0024) — cluster-propagated
     and commit-gated. The attribute knows nothing about `Elarion.Settings`.
   - *Not `IVariableSource`:* it would be a second seam wrapping the same lookup (the config-backed source is
     the shipped default), and its `${...}`-substitution semantics buy nothing for a direct key read.
   - *Not `IOptionsMonitor<T>`:* `Microsoft.Extensions.Options` is deliberately barred from
     `Elarion.Abstractions` (ADR-0017), an options-typed selector cannot know which section the host bound, the
     classic binder is reflection-based under the repo's AOT posture, and freshness would be identical anyway
     (options change notifications are driven by configuration reload tokens). Options **compose beside** the
     axis instead: bind the same section for the implementations' own knobs, and use
     `[AllowedValues(...)]` + `ValidateOnStart` when strict value validation is wanted.
   `Elarion.Abstractions` already references `Microsoft.Extensions.Configuration.Abstractions`, so the axis
   adds no dependency.
4. **Values match case-insensitively.** Configuration *keys* are case-insensitive by spec; for *values* the
   generator lower-cases declared `Value`s at emit time and the resolver lower-cases the configured value
   before the keyed lookup (these values are typed by humans into admin UIs and env vars — `"Office365"`
   silently falling back to SMTP is the failure mode we least want). `ELVAR001` consequently rejects
   case-only duplicate declarations. An absent key or a value matching no variant resolves the **default**
   implementation; with no default registered, resolution throws (mirroring the feature axis).
5. **Module integration is unchanged.** `ModuleServiceRegistrationGenerator` skips `[ConfigurationVariant]`
   classes exactly as it skips `[FeatureVariant]` ones (the variant path owns the registration), and the
   per-contract registrations ride the same per-module `Add{Module}VariantServices` /
   `AddVariantServices` hook, so a disabled module contributes no variants.

Diagnostics: new `ELVAR008` (error — contract mixes selection axes) and `ELVAR009` (warning — blank
configuration key, mirroring `ELVAR005`); `ELVAR004` retitled "Conflicting variant selector" and
`ELVAR003`/`ELVAR007` messages generalized to name the offending attribute. Manual registration mirrors the
feature axis: `AddElarionConfigurationVariantService<TService>(key, defaultKey, lifetime)` plus caller-supplied
keyed registrations (keys lower-case).

## Consequences

- **The admin scenario is first-class end-to-end with zero new infrastructure:** settings write → EF store
  commit → `pg_notify` → every node's configuration reloads → the next scope on any node resolves the new
  implementation. No flag provider, no restart. The FeatureManagement no-variant-names limitation stops
  mattering for the global case (per-user variants still require a variant-capable OpenFeature provider).
- **The async-proxy tax becomes exclusive to genuinely per-caller selection.** A configuration-variant
  handler's registration is byte-identical in shape to a non-variant handler's; per-resolution cost is one
  in-memory configuration lookup plus one keyed resolve.
- **Broader injectability:** any consumer in any assembly injects the contract directly; the
  same-compilation handler-detection limit and the warm-throw are confined to the feature axis.
- **Unknown values fall back silently to the default.** `Elarion.Abstractions` has no logging dependency, so
  the resolver cannot warn; the documented mitigations are the options-validation pattern
  (`[AllowedValues]` + `ValidateOnStart`) and validating the write at the admin endpoint. If this proves
  insufficient in practice, a warn-once hook would require taking `Microsoft.Extensions.Logging.Abstractions`
  into `Elarion.Abstractions` — declined for now.
- **Singleton consumers still pin their construction-time choice** (unchanged lifetime rule for every
  switchable contract): resolve per unit of work via a scope, or inject `IVariantServiceProvider<T>`.
- **Startup window with the settings bridge:** DB-backed configuration values load after the container is
  built (`SettingsConfigurationRefresher`, bounded initial load before later hosted services); until then the
  key is absent and the default implementation serves. Plain `appsettings.json`/environment values are present
  from the first resolution. Consequently, composition-root `if` branching on a DB-backed setting remains
  unsupported — the axis exists precisely so selection happens at resolve time.
- Two attributes to learn instead of one; `ELVAR008` keeps every contract unambiguous about its axis.
