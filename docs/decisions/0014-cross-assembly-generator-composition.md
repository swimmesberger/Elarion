# ADR-0014: Cross-assembly generator composition

- Status: Accepted
- Date: 2026-06-29
- Related: [ADR-0006](0006-incremental-source-generator-conventions.md) (the incrementality conventions this
  builds on), [ADR-0013](0013-resource-and-data-level-authorization.md) (resource authorization — the first
  feature to need it), [ADR-0003](0003-decorator-attachment-predicates.md) (logic graduates to generator-wired
  code), and the [source generation](../concepts/source-generation.mdx) concept doc.

## Context

Elarion is accumulating optional feature packages — Identity, resource grants/filters, settings, outbox, and
more to come — that each want to contribute generated code to a **shared aggregation point** in the consuming
application: the per-module `ConfigureDefaultServices`, the host's `AddElarion` DI registration, the transport
maps. The roadblock surfaced concretely when the resource-filter generator (in
`Elarion.EntityFrameworkCore.Generators`) wanted to register its generated `IQueryAuthorizer<TEntity>` specs
through the per-module `ConfigureDefaultServices` aggregation — which is owned by `ModuleDefaultServicesGenerator`
in a **different** analyzer package (`Elarion.Generators`).

The constraint is structural, not incidental: **independent Roslyn generators cannot observe each other's
output** in a compilation pass (this is deliberate — it preserves incrementality, and the Roslyn team has
declined to add inter-generator ordering/communication APIs). That leaves exactly two coordination channels:

1. **Shared convention code** compiled into each generator (already used — `EquatableArray.cs`,
   `SourceModels.cs`, `AssemblyMetadataReader.cs` are `<Compile Include>`-linked across generator projects).
2. **Referenced-assembly metadata** — a generator emits `[assembly: AssemblyMetadata(key, value)]` and another
   reads it from *referenced* assemblies via `MetadataReferencesProvider`. This is the existing
   `ElarionManifest` / `EntityConfigurationManifest` mechanism.

## Decision

> **Cross-package contributions aggregate at the composition root and are discovered from referenced-assembly
> manifests. Feature packages *publish* (a user-written marker, a generated implementation, and a manifest
> descriptor); they never co-mutate a shared partial class owned by another package's generator.**

Concretely:

- **The host bootstrapper is the single aggregator.** `AppModuleDiscoveryGenerator` (triggered by
  `[GenerateModuleBootstrapper]`) already reads every reference's `ElarionManifest`, associates entries with
  modules by longest-prefix namespace match, and emits module-feature-gated registrations. New cross-package
  contributions extend this generator with one decode + one gated emit, rather than threading a fragile
  partial-method contract between two analyzer assemblies.
- **The `ElarionManifest` is the cross-assembly channel.** Each feature publishes a typed descriptor: a new
  manifest entry kind = a key constant + a value-equatable record + an `Encode`/`TryDecode` over the
  length-prefixed codec + a reader case. `ElarionManifestGenerator` discovers the feature's user-written marker
  attribute (by metadata name — no dependency on the feature package) and emits the descriptor.
- **Same-assembly partial aggregation stays — but only for what it fits.** Categories authored *inside* a
  module and discovered *structurally* in the current compilation (handlers, services, validators, scheduled
  jobs, event consumers) keep the co-located `ConfigureDefaultServices` partial-class aggregation; all those
  fillers ship in one package (`Elarion.Generators`), so they cannot drift. The moment a contributor lives in
  another package, it uses the publish-and-discover channel instead.
- **The small shared contract is the emitted-member convention, documented, not negotiated at runtime.** For
  resource filters: a field-only spec exposes a static `Specification` (registered `AddSingleton`); a `Shared`
  spec is a scoped service with a grant-source constructor (registered `AddScoped`). The host emits against
  that convention; the feature generator emits to it. Both key off the same user-written `[ResourceFilter]`.

### First instance: resource-filter DI registration

Resource authorization ([ADR-0013](0013-resource-and-data-level-authorization.md)) is the first feature built
this way. `ElarionManifestGenerator` discovers `[ResourceFilter<TEntity>]` and publishes a
`ResourceFilter.v1` descriptor `(SpecFqn, EntityFqn, Namespace, IsShared)`. `AppModuleDiscoveryGenerator` reads
referenced manifests, buckets each filter under its owning module, and emits a gated, per-module
`Add{Module}ResourceFilters(IServiceCollection)` called from `AddElarion`. A referenced module library's
filters therefore register at the host with the rest of the module — module-feature-gated, AOT-clean (static
typed `AddScoped`/`AddSingleton`), and with no participation from the EF generators package in the aggregation.

### A complementary same-compilation variant

For model configuration that must compose in *one* compilation (Identity + grants on a single `DbContext`),
the convention-sharing channel is the right tool instead of the manifest: the EF `DbContextGenerator` discovers
the user's `[GenerateElarion*]` opt-ins and emits a per-feature `OnEntitiesConfigured_{Feature}` seam each
feature generator implements by the same naming convention (see ADR-0013). Manifest = cross-assembly
registration; convention-shared per-feature seam = same-compilation model config. Both avoid two generators
sharing one fragile partial-method signature.

## Consequences

**Positive**

- Optional feature packages compose into the host's DI **cross-assembly**, **module-feature-gated**, with no
  inter-generator partial-method contract — reusing the manifest mechanism already proven for handlers/modules.
- Gating is free: the composition root (`AddElarion`) already owns `IsModuleEnabled`, so a disabled module's
  feature contributions disappear too.
- Generated registrations are concrete and statically typed (AOT/trim-clean); discovery follows ADR-0006
  (value-equatable models, byte-identical output, cache-reuse tests).

**Negative / accepted**

- A feature must **publish** a manifest descriptor and honor a small **emitted-member contract** (e.g. the
  `Specification` member / scoped-vs-singleton choice). That contract is documented here and in the manifest
  record, not enforced by the compiler across packages.
- `AppModuleDiscoveryGenerator` gains knowledge of each manifest entry kind (one decode + one emit per kind).
  This is bounded and centralizes aggregation in the place that already does module gating; the alternative
  (every feature generator co-mutating a shared class) is worse.
- Transport-style aggregation is **referenced-manifest-only**: handlers/endpoints/filters are aggregated from
  referenced module assemblies, matching the established structure (a thin host references module libraries).
- Cross-assembly composition needs an **integration safety net**: a test that compiles a referenced assembly
  to an image (running its manifest generator), references it from a host, runs the bootstrapper generator,
  and asserts both the emitted registrations and that the merged output compiles. This is now a standing
  convention for new manifest entry kinds.

## Implementation

- New manifest entry kind: add the key + record + `Encode`/`TryDecode` to `ElarionManifest`, a case to
  `ElarionManifestReader.AddEntry`, discovery + emission to `ElarionManifestGenerator`, and a gated emit to
  `AppModuleDiscoveryGenerator`. All follow [ADR-0006](0006-incremental-source-generator-conventions.md).
- Tests: a cross-assembly `CompileToImage` → reference → bootstrapper test (asserting the gated registration
  and that the generated code compiles against the referenced types), plus the existing determinism check.
- Shared conventions live in `ElarionGeneratorConventions` (in `Elarion.Generators`, `<Compile Include>`-linked
  into the EF and resource-grants generator packages like `EquatableArray.cs`). It centralizes the genuinely
  cross-package agreements — the marker-attribute metadata names, the EF model-configuration seam naming
  (`ModelConfigurationSeamName`), and the `[ResourceFilter]` emitted-member contract — so independent
  generators agree by *calling the same code*, not by copying literals. New cross-package conventions are added
  here. The drift is caught by integration tests that run the cooperating generators together and assert the
  merged output compiles (e.g. the DbContext-seam-declares / feature-generator-implements compose test).

## Addendum (2026-07-11): the bootstrapper also discovers the current compilation

The manifest channel only covers *references* — the bootstrapper compilation's own manifest is emitted as
source into that same compilation and is never read back. Originally the bootstrapper read modules (and
`[ModuleEndpoints]` contributors) directly from the current compilation but transport handlers exclusively
from manifests, so a single-project host (Program + modules in one csproj) silently produced empty transport
maps: DI registration worked, but every `[HttpEndpoint]` 404'd and `RegisterHandlers` mapped nothing, with no
diagnostic. `AppModuleDiscoveryGenerator` now mirrors the manifest generator's per-node discovery for
`[HttpEndpoint]`/`[Handler]`/`[ResourceFilter]` in the current compilation and merges it with the referenced
manifests (current-compilation entries win deduplication; shape diagnostics stay owned by
`ElarionManifestGenerator`, which always runs alongside). The manifest remains the only *cross-assembly*
channel; this closes the same-assembly gap.
