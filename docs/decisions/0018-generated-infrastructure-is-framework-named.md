# ADR-0018: Generated infrastructure is framework-named, not user-declared

- Status: Accepted
- Date: 2026-06-29
- Related: [ADR-0014](0014-cross-assembly-generator-composition.md) (the cross-assembly aggregation these
  artifacts use), [ADR-0006](0006-incremental-source-generator-conventions.md) (generator conventions), and the
  [source generation](../concepts/source-generation.mdx) concept doc.

## Context

Elarion's generators produce two physically different *shapes* of output, and until now the choice between them
was made case by case:

1. **A generator fills a partial class you declare.** `[GenerateDbSets]` / `[GenerateElarionIdentity]` add `DbSet`
   properties and the Identity model onto *your* `DbContext` — a class you name, give a base type and constructor,
   and fill with your own entities, and which two generators co-author.
2. **A generator emits a standalone, framework-named type.** `ElarionManifest`, each module's
   `{Module}ElarionModuleServices`, and (most recently) `ElarionPermissions` are emitted whole; you annotate the
   *inputs* (`[AppModule]`, `[RequirePermission]`, …) and never declare the output.

The **module bootstrapper** sat on the wrong side of that line. `[GenerateModuleBootstrapper]` was a *class*
attribute: you declared `public static partial class ModuleBootstrapper { }` and the generator filled it — but you
supplied *nothing* to that class (no members, no base type — just a name). So the partial bought you exactly one
thing: the freedom to name it. And that freedom is a liability: one app calls it `App.Hosting.Bootstrap`, another
`MyApp.ModuleHost`, so the composition root looks different in every Elarion codebase.

The question this ADR settles: **when should a generated type be a partial you declare, versus a framework-named
standalone type?**

## Decision

The deciding question is **whose identity the type carries** — not how much you write into it.

- **Purely-derived infrastructure** — a type whose entire content the generator infers from *other* declarations,
  to which you contribute no members, base type, or constructor — is **framework-named, assembly-triggered, and
  emitted standalone** into the host's root namespace. You annotate the inputs; the framework owns the output's
  name and location. This covers `ElarionPermissions`, the module bootstrapper (now **`ElarionBootstrapper`**),
  `ElarionManifest`, and the per-module `ConfigureDefaultServices` siblings.

- **Augmentations of a type whose identity is genuinely yours** — where you choose the name, base type, and
  constructor, add your own members, and possibly have several generators co-author one type — stay
  **user-declared partials**. This is `[GenerateDbSets]` / `[GenerateElarionIdentity]` on the application's
  `DbContext`: its name, its `: DbContext` base, its `(DbContextOptions)` constructor, and its domain entities are
  all yours, and `[GenerateDbSets]` + `[GenerateElarionIdentity]` both contribute to it.

**Why — cross-project consistency.** Fixed, framework-owned names mean every Elarion application exposes the same
composition root and the same catalog — `ElarionBootstrapper`, `ElarionPermissions` — so a developer moving
between Elarion codebases lands on the same anchors every time, the way `Program`/`Startup` are always where you
expect in an ASP.NET app or `manage.py`/`settings.py` in Django. Naming freedom for a piece of pure framework
wiring buys nothing and costs familiarity. The "derived vs augmentation" test is the *boundary*: you can only fix
the name when the type's identity isn't yours to begin with — and a `DbContext`'s genuinely is.

Concretely, `[GenerateModuleBootstrapper]` becomes an **assembly** attribute
(`[assembly: GenerateModuleBootstrapper]`) and `AppModuleDiscoveryGenerator` emits the fixed-name
`ElarionBootstrapper` static (`AddElarion`, `MapElarionEndpoints`, `RegisterHandlers`, `GetMcpMetadata`, …) into
the host's root namespace — exactly the mechanism `ElarionPermissions` already uses.

These standalone types are still emitted `partial`, purely as an opt-in escape hatch: a host *may* add its own
`partial class ElarionBootstrapper` to drop a hand-written member beside the generated ones, but never *must*.

## Consequences

- **Breaking (intended).** A host drops its hand-declared `ModuleBootstrapper` partial, adds
  `[assembly: GenerateModuleBootstrapper]`, and references the fixed name (`ElarionBootstrapper.RegisterHandlers`).
  Pre-1.0, and consistent with [the clean-API stance](../concepts/source-generation.mdx).
- **Every Elarion host reads the same.** The composition root is `ElarionBootstrapper` everywhere; nothing to name,
  nothing to discover, no per-project drift.
- **The rule guides future generators.** New purely-derived infrastructure is framework-named and standalone;
  only generators that augment a genuinely user-owned type (today, the `DbContext`) take a user-declared partial.
- **Extensibility is preserved.** The generated types are `partial`, so a host can still extend them without owning
  their declaration.
