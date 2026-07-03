# ADR-0029: The variant registry (`ElarionVariants`), named defaults, and the host-seeded catalog

- Status: Accepted
- Date: 2026-07-03
- Related: [ADR-0028](0028-configuration-selected-service-variants.md) (configuration-selected variants — this
  ADR builds the registry over both axes), [ADR-0019](0019-variant-service-injection.md) (feature-selected
  variants), [ADR-0027](0027-declarative-request-validation.md) (declarative constraints — `[AllowedValues]`
  joins its export set here), [ADR-0014](0014-cross-assembly-generator-composition.md) (the Elarion manifest),
  the `ElarionPermissions` catalog precedent in [ADR-0009](0009-authorization-building-blocks.md).

## Context

With `[FeatureVariant]` and `[ConfigurationVariant]`, the generator knows every switch an application ships —
its selector key, its value vocabulary, its default, its owning module — and then throws that knowledge away
after emitting DI registrations. Everything an admin surface needs must be hand-maintained: the allowed-value
list on the write DTO, the dropdown in the settings UI, the "what switches exist" page. That is the same shape
of drift the permission catalog eliminated for `[RequirePermission]` (zero central edits per guarded handler).

Three placement/ownership realities constrain the design:

1. **Variant implementations often live in the infrastructure assembly** (the port/adapter pattern), under no
   `[AppModule]`. That is the *documented correct* placement — platform, not a smell — so "not in a module"
   must not be a diagnostic for variants (unlike jobs/consumers, whose only registration path is module hooks).
2. **Layering decides who may see generated symbols.** Infrastructure references Application, never the
   reverse: consts generated from infrastructure declarations are invisible to application DTOs — and should
   be (application modules must not compile against platform implementation names).
3. **Applications must be able to design their own settings-change APIs** — their own command shapes,
   authorization, auditing — including over platform switches they cannot reference at compile time.

A first design cut runtime DI registration of catalog entries entirely ("compile-time static only") — but (3)
brought a runtime surface back in a cheaper form: application handlers need *data* about what the platform
offers, without symbols.

## Decision

### 1. Named defaults (`IsDefault`)

A default implementation may now carry a value: `[ConfigurationVariant("Email:Backend", Value = "smtp",
IsDefault = true)]` (both axes). It is registered under its own value key **and** becomes the binding's
default key — so the default state has a writable, validatable name (an admin switches back to SMTP by writing
`"smtp"`, not by removing the key). An unnamed default keeps the collision-proof sentinel. More than one
default per contract is `ELVAR001` (`"(default)"`); a registry needs a complete vocabulary, so named defaults
are the recommended form for admin-facing switches.

### 2. The compile-time registry: `ElarionVariants`

`VariantCatalogGenerator` (triggered by `[UseElarion]` / `[GenerateVariantCatalog]`) emits a partial static in
the assembly root namespace, aggregated **cross-assembly from the Elarion manifest** (a new
`Elarion.Manifest.Variant.v1` entry per contract, written by `ElarionManifestGenerator` via the shared
`VariantDiscovery`) — the exact `ElarionPermissions` shape:

- one **accessor class per switch** (`ElarionVariants.EmailBackend`) with the selector `Key` const and one
  `const string` per value — usable in attributes: `[AllowedValues(ElarionVariants.EmailBackend.Smtp, …)]`;
- one `VariantDescriptor` per variant contract (axis, key, contract name + `Type?`, ordinally-sorted values,
  default, module), surfaced as `All` / `ByKey` (case-insensitive) / `ByModule` / **`Platform`** — descriptors
  of variants under no module carry `Module = null` and group under `Platform`, first-class;
- accessor-name collisions (two selectors or values PascalCasing identically) report `ELVAR010` (warning);
  every entry stays in the data surfaces, only the second typed accessor is omitted (`ELPERM002` precedent);
- an **internal contract** in a referenced assembly still contributes its switch (key/values/default) with
  `Contract = null` — `typeof` would not compile in the aggregating assembly; `ContractName` always carries
  the FQN. In the declaring assembly's own registry, `typeof` is emitted even for internal contracts.

### 3. The host-seeded runtime catalog: `IVariantCatalog`

The generator emits **no runtime DI registrations** for the registry. The host — the one assembly whose
registry aggregates everything — seeds the data explicitly:

```csharp
builder.Services.AddElarionVariantCatalog(ElarionVariants.All);
```

`IVariantCatalog` (`All`, case-insensitive `FindByKey`) is the bridge across the layering boundary: symbols
stay host-side, data flows down, and an application module designs its own settings-change handler — its own
route, DTO, policy, audit — validating a requested value against "what the platform offers" without
referencing the platform. The framework ships no admin endpoint; the registry is handed to application/host
code, which decides how it is exposed.

### 4. The validation worker

`AddElarionVariantValidation()` (in `Elarion` core, which has logging — `Elarion.Abstractions` deliberately
does not) registers a hosted validator over the seeded catalog: at startup **and on every configuration
reload** each configuration-axis switch's current value must be one the registry offers (closing ADR-0028's
documented silent-fallback gap — the fallback keeps requests serving; the validator makes the mismatch loud),
and at startup each **platform** descriptor's contract must actually resolve in DI
(`IServiceProviderIsService`, no construction) — catching a forgotten host `Add…VariantService()` call at boot
instead of at first send. Module-owned variants are excluded from the registration check on purpose: the
registry is compile-time, so a deliberately disabled module's variants would otherwise cry wolf every start.
`VariantValidationOptions.Strict` turns startup findings into a failed start; reload-time findings always only
log (a runtime settings write must never take a host down).

### 5. `[AllowedValues]` joins the constraint export set (ADR-0027 addendum)

The registry makes declared value sets first-class, so the wire contracts follow:
`[AllowedValues(...)]` now maps to the JSON Schema **`enum`** keyword in the JSON-RPC schema exporter (MCP
tool schemas share the builder), a parity schema transformer mirrors it onto the OpenAPI document (Microsoft's
built-in DataAnnotations mapping omits it; an existing non-empty `enum` is left untouched), and the generated
TypeScript client already maps `enum` to `z.enum(...)`/literal unions with string-union types — so a write DTO
guarded with registry consts pre-validates in every client and renders as a dropdown-able enum in admin UIs.
The validation-resolver generator already constant-constructs the `params object?[]` attribute, so runtime
enforcement needed no change.

## Who owns the switch (the usage guidance this enables)

| Pattern | Menu owner | Consumers use | Best for |
|---|---|---|---|
| Module variant | the module's declarations; registry consts *are* the vocabulary | `ElarionVariants.X.Y` consts, `[AllowedValues]` | in-app strategies (forecast algorithm) |
| **Port-owned vocabulary (recommended)** | app-declared consts beside the port; adapters' attributes reference them (Infrastructure → Application works) | the same app consts in DTOs and attributes | product-meaningful choices the app's UX presents (email backend with a typed settings command) |
| Platform offering | infrastructure declarations; host aggregates | `IVariantCatalog` (host-seeded data, no symbols) | ops-flavored parameters, generic settings consoles, zero per-switch code |

All three keep the application in charge of its own API design; they differ in whether the vocabulary is
compile-time (consts → schema enums → typed clients) or runtime data (dynamic enumeration).

**Recommended defaults (convention over configuration).** Docs designate a happy path so the three patterns
never read as an open-ended menu: an in-module strategy has nothing to decide (its module's declarations are
the vocabulary); a switchable adapter defaults to **port-owned vocabulary** — the port owns the interface, so
it owns the menu, matching the framework's compile-time-first grain (one declaration feeds enforcement, every
schema surface, every client, and the registry) — and the platform offering is reserved for genuinely generic
surfaces (zero per-switch code, or the application must not know the choices). On the axis itself the rule is
*who decides*: an answer that can differ between two concurrent requests is `[FeatureVariant]`; one answer per
process is `[ConfigurationVariant]` — and **when in doubt, start configuration-selected**, the smaller machine
(no flag provider, no async proxy, injectable anywhere); migrating a switch to the feature axis later touches
only the implementation classes' attributes.

## Consequences

- Adding a variant implementation updates the write-DTO allowed set, the admin dropdown data, the schema
  `enum`, the client validation, and the startup checks with **zero central edits** — the permission-catalog
  property, transplanted.
- The registry is honest about module gating: it is compile-time, so a disabled module's switches still appear
  in `ElarionVariants`. Runtime filtering (if ever needed) belongs to whoever seeds the catalog; the
  validation worker already accounts for it by scoping the DI check to platform descriptors.
- One more assembly-level trigger to know (`[GenerateVariantCatalog]`, subsumed by `[UseElarion]` like every
  `HasAssemblyTrigger` feature).
- The manifest schema grew an entry kind; per ADR-0014's versioning, older generators reading a newer manifest
  skip unknown keys, and the schema-version guard covers breaking changes.
- Rejected: generator-emitted runtime catalog registrations (the host-seed pattern gives the same runtime
  surface for one explicit line, with no hidden DI and no module-hook coupling); a framework-owned admin
  endpoint (applications own their settings APIs — the framework supplies data, not routes); a "variant under
  no module" diagnostic (platform placement is the documented pattern, not an accident).
