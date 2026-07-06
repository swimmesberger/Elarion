# ADR-0040: Host-declared module endpoint hooks (`[ModuleEndpoints]`)

- Status: Accepted
- Date: 2026-07-06
- Related: [ADR-0039](0039-binary-file-responses.md) (file responses cover the download case that exposed this
  gap), [ADR-0018](0018-generated-infrastructure-is-framework-named.md) (the bootstrapper the hooks compose
  into), [ADR-0031](0031-imperative-handler-transport-mapping.md) (imperative mapping for handlers you don't
  own), the [modules](../concepts/modules.mdx) concept doc.

## Context

A module's hand-written endpoints hang off two convention hooks on the `[AppModule]` type — `static
MapEndpoints(IEndpointRouteBuilder)` and `static ConfigureEndpointGroup(IEndpointRouteBuilder)` — which
`MapElarionEndpoints` calls inside the module's feature gate. That is exactly the right seam, but both hooks
take `IEndpointRouteBuilder`, a shared-framework type. An application assembly that is deliberately
**web-free** (the point of the `Elarion.EntityFrameworkCore.Identity` split, and the recommended shape for
module assemblies) cannot declare them without re-importing `Microsoft.AspNetCore.App`.

The observed workaround in a consuming app: the module owns the behavior behind a `[Service]`, the host maps
the routes by hand and re-applies the module's feature gate manually via `IsModuleEnabled(configuration,
"ImportExport")` — hand-duplicating what `MapElarionEndpoints` does for every other module surface, and silently
drifting the moment the module is renamed or the gating rules change.

## Decision

`[ModuleEndpoints("Name")]` (in `Elarion.AspNetCore`) marks a static class that declares endpoint hooks **for a
module from outside its assembly** — typically in the host:

```csharp
[ModuleEndpoints("ImportExport")]
internal static class ImportExportEndpoints {
    public static IEndpointRouteBuilder ConfigureEndpointGroup(IEndpointRouteBuilder endpoints) =>
        endpoints.MapGroup("/import-export").RequireAuthorization();

    public static void MapEndpoints(IEndpointRouteBuilder endpoints) {
        // hand-written routes for the module; runs inside the module's feature gate
    }
}
```

- The class declares the **same convention hooks** a module type may declare (either or both; a class with
  neither is dead weight and warns `ELMOD005`).
- `AppModuleDiscoveryGenerator` discovers contributors in the **host compilation** directly and in **referenced
  assemblies via the per-assembly Elarion manifest** (new `Elarion.Manifest.ModuleEndpoints.v1` entry — adding a
  key is backward-compatible; old readers ignore it). Cross-assembly support exists so a web companion assembly
  beside a web-free module assembly works without a silent no-op — the alternative (host-compilation only)
  would make a referenced contributor vanish with no diagnostic anywhere.
- `MapElarionEndpoints` calls the hooks **inside the named module's feature gate**, composed with the module's
  own hooks: group hooks chain (module's first, then contributors in stable type-name order), then
  `MapEndpoints` in the same order, then the module's generated `[HttpEndpoint]` routes — all onto the builder
  the group-hook chain returns. A disabled module's contributed endpoints disappear with it.
- A contributor naming a module no discovery produced warns `ELMOD004` and is skipped — mapping it ungated
  would defeat the feature gate the attribute exists to reuse.

## Alternatives considered

- **Let the application assembly reference the shared framework.** Rejected — it defeats the web-free split
  the package layout is built around and drags the whole of ASP.NET into the data/domain layer.
- **Framework-owned `IModuleEndpointContributor` interface resolved from DI.** Rejected — runtime resolution
  loses the compile-time module-name check (`ELMOD004`), the deterministic emission order, and RDG/AOT
  friendliness of statically-visible mapping calls (ADR-0031's constraint).
- **A `partial ElarionBootstrapper` extension point.** Rejected — user-declared partials of framework-named
  types are exactly what ADR-0018 rules out, and a partial has no story for per-module gating.
- **Only proposal 1 (file responses, ADR-0039) without this seam.** Rejected as incomplete — downloads were
  the trigger, but any endpoint a handler cannot express (SSE, websockets, third-party middleware, response
  streaming beyond files) hits the same wall; this is the general seam, ADR-0039 is the happy path.

## Consequences

- Hosts stop re-implementing module gating for hand-mapped routes; the module name is checked at compile time.
- A module can ship as a web-free core assembly plus an optional web companion assembly whose contributions
  ride the manifest like every other Elarion discovery.
- Two new warnings (`ELMOD004`, `ELMOD005`); the manifest gains one entry kind with no schema-version bump.
