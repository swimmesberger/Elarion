# ADR-0078: Settings are declared definitions with one effective-value resolver

- Status: Proposed
- Date: 2026-10-04
- Related: [ADR-0011](0011-runtime-settings-subsystem.md) (the settings subsystem this reshapes),
  [ADR-0006](0006-incremental-source-generator-conventions.md) (generator conventions),
  [ADR-0017](0017-dependency-light-core.md) (heavy defaults live in opt-in siblings),
  [ADR-0070](0070-contract-set-registration.md), [ADR-0074](0074-reactive-client-capabilities.md),
  and the [settings](../concepts/settings.mdx) concept page.

## Context

ADR-0011 made settings a string-keyed bag: `GetAsync(key, fallback)` / `SetStringAsync(key, value)` over a store,
with an `IConfiguration` provider as one more consumer. Three pressures landed on that shape at once.

- **Secrets.** Applications keep credentials in settings, so the value must not be readable from the table or a
  backup, and an admin screen must be able to say "a value is set" without receiving it.
- **Pinning.** A deployment (an environment variable, a mounted file) must be able to fix a value so the UI
  cannot silently diverge from what the process actually uses.
- **Admin surfaces.** A generic settings screen needs to enumerate what exists, its type, whether it is secret or
  pinned, and where the effective value comes from.

An earlier, compatibility-driven version of this work kept the string-key API and bolted the three concerns on:
secrets were marked by key *prefix* in options, the protection state was a `elarion-secret:v1:` prefix inside the
stored value, pinning was an opt-in `IConfiguration` precedence switch (`StoreWins`/`ConfigurationWins`) and a
separate "source inspector", and the `IConfiguration` provider doubled as a second source of truth. Every one of
those is a workaround for the same root cause: **the system does not know what settings exist.** Keys are
strings nobody declared, so "is it secret", "may configuration pin it", "what type is it" and "what is its
default" are re-derived from prefixes and call-site fallbacks, and the answer a reader gets can differ from the
value that takes effect.

## Decision

1. **Settings are declared once, at compile time.** A `static partial` class marked `[SettingDefinitions]` holds
   `static partial SettingDefinition<T>` properties annotated `[Setting("key", ...)]` with the scope kinds,
   `Secret`, `Pinnable`, description and a default (a constant `Default`, or a static `DefaultFactory` for
   records, evaluated lazily). A generator (`SettingDefinitionGenerator`, ADR-0006 conventions:
   `ForAttributeWithMetadataName`, equatable pipeline models, diagnostics as data, deterministic output, cache
   tests) implements each property and adds a per-class `All`. Containers are published in the Elarion manifest;
   `[GenerateSettingDefinitionCatalog]` (or `[UseElarion]`) emits `ElarionSettingDefinitions.All` aggregating the
   assembly and the public containers of referenced assemblies, and the host seeds the runtime catalog
   explicitly (`AddElarionSettings(o => o.AddDefinitions(...))`), like the variant catalog. Declarations are
   verified at build time: duplicate keys (case-insensitive, since keys double as `IConfiguration` keys), invalid
   keys, a secret with a default, `Pinnable` without the global scope, and invalid defaults are errors
   (`ELSDEF001`–`ELSDEF007`). There is no imperative registration path: `SettingDefinition<T>` constructors are
   public only because generated code in the consuming assembly calls them, and the runtime refuses any
   definition that is not registered in the catalog.

2. **Undeclared keys cannot be named.** The manager is addressed by `SettingDefinition<T>`, never a string, so an
   undeclared setting is a compile error at the call site (there is no key to type). An unregistered definition
   or a scope the definition does not allow throws `InvalidOperationException` — it is a programming error, not
   a runtime condition. Runtime conditions are `Result` failures (below). The store seam still accepts any key
   (it is a sink), but only the resolver and manager sit on top of it.

3. **One effective-value resolver.** `ISettingResolver` layers default < store < configuration. The
   configuration layer applies only to definitions marked `Pinnable` in the global scope; a configuration value
   *pins* the definition when present and, unless `EmptyConfigurationValuesPin` is set, neither empty nor
   whitespace (a blank environment variable or unfilled template must not freeze a setting to `""`).
   `ISettingsManager` reads *through* the resolver, so code never sees a value other than the one that takes
   effect. A write or reset of a pinned definition is refused: `Result<SettingWrite>.Failure` with an `AppError`
   (`BusinessRule`, code `settings.pinned`) whose data is `SettingWriteFailure(key)`; a lost optimistic race is
   `Conflict` with code `settings.concurrency_conflict` ([ADR-0080](0080-errors-are-a-declared-contract-with-a-stable-code.md)). The configuration layer reads the configuration root's providers but skips
   *projection* providers (`ISettingsProjectionProvider`), so the projection cannot feed back into the value it
   is derived from. Configuration text is mapped to canonical JSON: a string setting's value is the string, any
   other type uses its JSON form (bare words accepted for enums and string-like types); a malformed pinned value
   fails loudly on read.

4. **The `IConfiguration` provider is a projection, not a source.** `Elarion.Settings.Configuration` projects
   the resolver's effective global values (object values flattened to `key:property` paths so options binding
   works) into `IConfiguration`. There is no precedence switch: the resolver decides. Values that configuration
   pins are skipped (already present), and **secrets are never projected** — `IConfiguration` is routinely
   bound, logged and dumped, so a protected-at-rest secret must not be handed to it; code reads secrets through
   the manager. The refresher reprojects on store changes and on configuration reload; the provider reloads only
   when its data changed, which ends the cycle its own reload would otherwise start. `ISettingsManager.Watch`
   combines the store token with the configuration reload token for pinnable definitions.

5. **Secrets are protected at rest behind a seam, and fail closed.** `Secret = true` definitions are protected
   through `ISettingValueProtector` (`Scheme`, `Protect(purpose, plaintext)`, `Unprotect(purpose, payload)`). The
   purpose binds scope kind, owner and key, so a ciphertext copied to another row does not decrypt. Protection
   state is **store metadata**: `SettingEntry.Protection` carries the scheme (a nullable `protection` column on
   the EF row, a field in the in-process store), not a prefix inside the value, so a protected row, a plaintext
   row and a legacy plaintext row under a now-secret definition are distinguishable without parsing the value,
   and the scheme can change independently. A secret definition with no protector registered fails the catalog
   build and host startup (`SettingProtectionException`); there is deliberately no `AllowUnprotected` escape
   hatch — a development host registers the Data Protection implementation with its default ephemeral key ring.
   `ISettingReprotector` converges legacy plaintext, retired keys and older schemes idempotently, version-guarded
   so a concurrent write wins; `Elarion.Settings.DataProtection` (opt-in sibling, so core stays free of ASP.NET
   Core) ships the Data Protection protector and an optional startup re-protection step.

6. **Describe falls out of definitions.** `ISettingsManager.DescribeAsync(scope?, prefix?)` returns, per
   definition allowing the scope, `{Key, ValueType, Scope, AllowedScopes, IsSecret, IsPinnable, IsPinned,
   HasValue, Source (Default|Store|Configuration), ValueJson, Version, IsUnreadable, Description}`.
   `ValueJson` is never set for a secret; an unreadable stored secret is flagged instead of failing the call.
   It is transport-neutral — Elarion ships no handlers; the host maps it to whatever it exposes.

7. **Definition metadata is not exported to the client capability manifest (ADR-0074).** That manifest carries
   which *features* a deployment enables so a UI can gate itself; settings are server-side configuration whose
   value, pinning and existence are per deployment and often per user. Exporting key/type metadata would publish
   the server's configuration surface to every client, and the admin screen already gets what it needs from the
   authorized describe call. The definition metadata is nevertheless available at build time (the manifest
   carries the containers), so a future schema export can opt in without reshaping the declaration.

## Consequences

- **Breaking.** `ISettingsManager` is definition-based; the string-key `GetAsync`/`GetStringAsync`/
  `SetStringAsync`/`RemoveAsync` are removed and writes return `Result<SettingWrite>`. `ISettingsStore.GetAsync`
  returns `SettingEntry?`, `SetAsync` takes `protection`, `SettingEntry` gains `Protection`; the EF `Setting` row
  gains a nullable `protection` column (apps add a migration). `SettingsConfigurationProvider.Apply` and
  `SettingsConfigurationRefresher` change shape; the configuration provider no longer carries stored values for
  undeclared keys. Every consumer must declare the keys it used.
- Typed accessors for `string`/`int`/… still need the type in a JSON context (unchanged: canonical serializer,
  no reflection).
- Writes to a pinned definition are refused instead of silently ignored, and describe tells the UI why.
- A key rename of a secret needs a rewrite (the purpose includes the key); documented.
- Cost: a generator, a manifest entry kind, and a catalog the host must seed. The aggregated
  `ElarionSettingDefinitions.All` keeps seeding to one line.

## Rejected alternatives

- **The compatibility-driven variant (string keys plus prefix-marked secrets, an in-value `elarion-secret:v1:`
  envelope, a `StoreWins`/`ConfigurationWins` switch, a source inspector, and a configuration provider that is a
  second source).** It preserved the old API at the cost of five parallel mechanisms that each re-derive facts a
  declaration states once, and it let the value a reader sees differ from the one in effect. Pre-1.0, the clean
  seam wins.
- **Runtime-only registration of definitions** (a builder API in `Program.cs`). Loses the build-time checks and
  the manifest aggregation, and brings back string keys at the declaration site.
- **Attribute-only declaration with the default inside the attribute.** Attribute arguments cannot express a
  record default; `DefaultFactory` covers it without reflection.
- **Letting `IConfiguration` pin every key, or deriving pinnability from the key's presence.** Pinning is a
  per-definition decision (some settings must stay runtime-editable even if an environment variable of the same
  name exists), so it is declared, not inferred.
- **Projecting secrets into `IConfiguration` for convenience.** Rejected: it defeats protection at rest for the
  most commonly logged object in a host.
- **An `AllowUnprotectedSecrets` option.** Rejected: a switch that exists will be set in production.
