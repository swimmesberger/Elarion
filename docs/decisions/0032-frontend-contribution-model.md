# ADR-0032: Frontend contribution model and the typed capability vocabulary

- Status: Accepted
- Date: 2026-07-03
- Related: [ADR-0030](0030-client-capability-bootstrap.md) (the capability snapshot this model gates on),
  [ADR-0031](0031-imperative-handler-transport-mapping.md), [ADR-0002](0002-cross-module-communication.md)
  (`[ModuleContract]`/`ELMOD002` — the backend boundary model this mirrors), ADR-0007 ("module as plugin"),
  and the [client-capabilities concept doc](../concepts/client-capabilities.mdx).

## Context

Elarion's backend is a modular monolith with a hard review-isolation property: adding a feature to a module
touches only that module's code — handlers, services, jobs, consumers, permissions, and variants are discovered
and aggregated by generators, and `ELMOD002` polices the boundaries. The frontend has no analog. In a typical
SPA, adding a sidebar item for a new Invoicing module means editing the shell's sidebar component — exactly the
central edit the backend model eliminates — and the conditions that should gate that item (module enabled,
`invoices.read` held, a flag on) are backend facts that teams re-implement by hand as string literals that drift.

[ADR-0030](0030-client-capability-bootstrap.md) already delivers the **facts** at runtime: one snapshot of
modules/flags/variants/grants, with a generated `session-client.ts` and an OpenFeature provider. Two gaps remain:

1. **No typed vocabulary.** The snapshot accessors take `string` — `hasPermission("invocies.read")` compiles and
   silently returns `false`. The exported schema carries only per-method `params`/`result`/`idempotent`; module
   names, `[ClientFeatures]` names, and the permission catalog never reach the client generator, so it cannot emit
   constants. The backend has `ElarionPermissions`; the frontend has string literals.
2. **No contribution mechanics.** Nothing defines how a frontend module declares "I contribute this sidebar item /
   this context-menu action", how another module contributes to *its* extension points, or how the shell renders
   the aggregate.

The problem splits three ways, and only part of it is framework-worthy: contribution *mechanics* are ~150 lines
any team can write (their value is convention); module *boundaries* are already solved by workspace-package
`exports` + lint; the *facts and vocabulary* are things only the framework can export without drift.

## Decision

### 1. The typed capability vocabulary rides the schema (shipped with this ADR)

`JsonRpcSchemaExporter.Generate` accepts an optional `JsonRpcSchemaExportOptions` carrying the
`ClientCapabilityManifest` (moved to `Elarion.Abstractions.Modules`, beside `[ClientFeatures]`, so the
Abstractions-only exporter can type it) and the `IPermissionCatalog`. The schema-generation tool resolves both
from the application's own DI registrations — zero configuration — and the schema gains a `capabilities` block:

```json
"capabilities": {
  "modules": { "Clients": { "features": ["bulk-import", "client-portal-v2"] } },
  "permissions": [{ "permission": "clients.read", "resource": "clients", "verb": "read" }],
  "roles": ["billing-admin"]
}
```

Only **enabled** modules contribute (the same gating the exported methods follow); permissions are structured
(resource/verb parts are free-form, so clients never re-parse the composed string); everything is deterministically
sorted; the block is omitted entirely when nothing is registered, keeping vocabulary-free schemas byte-identical.

The TypeScript generator turns the block into typed constants and literal unions in `session-client.ts` —
`Modules`, `Flags`, `Permissions` (nested resource → verb), `Roles`, with `ModuleName`/`FlagName`/
`PermissionName`/`RoleName` aliases — and the `SessionCapabilities` accessors and `Keys` builders take
`Name | (string & {})`, so a typo is a compile error while out-of-vocabulary names stay expressible. Without the
block, every alias falls back to `string` (older schemas keep working). This is the frontend `ElarionPermissions`:
one vocabulary, compile-checked on both sides of the wire.

### 2. Contributions are declarative manifests, not import-time registration

A frontend module ships a **manifest** — plain data plus lazy component references — from its public entry
(`modules/{name}/index.ts`); the composition root **discovers** the modules with a Vite glob
(`import.meta.glob("./modules/*/index.ts", { eager: true, import: "default" })`), so adding a module is
creating its folder — zero central edits, zero lines per *contribution*. The glob expands at build time into
static imports (compile-time discovery, deterministic order), and disabled modules stay in the one build
artifact but resolve to nothing at runtime. The accepted cost: a glob-composed route tree types as
`AnyRoute[]`, so `Link to` loses literal-union checking — a team that wants fully-typed navigation lists the
modules statically instead (one line per module, the grain of a host `ProjectReference`); both compose the
same manifests:

```ts
// modules/invoicing/module.tsx
export const invoicingManifest = defineModule({
  name: Modules.Invoicing,
  when: { module: Modules.Invoicing },   // module-level gate, ANDed into every contribution below
  contributes: [
    contribute(sidebarItems, [{
      id: 'invoices', label: 'Invoices', icon: ReceiptText, to: '/invoices', order: 20,
      when: { permission: Permissions.invoices.read },
    }]),
  ],
})
```

Two shapes are deliberate: `contribute(point, items)` batches rather than a computed-key record, because a
computed key cannot type its items against the point's payload (the batch function can); and a **manifest-level
`when`** ANDed into every contribution, so a backend-paired module gates itself on its module once instead of
repeating the clause per item.

Imperative `registry.register(...)` calls in module initializers are rejected: they are import-order-dependent,
side-effectful, break tree-shaking, and cannot be validated up front (the reason VS Code moved contributions into
static `package.json` data). Manifests are inspectable, deterministic (contributions sort by `order`, id-tiebreak),
and testable — a slot renders from a plain array.

### 3. Extension points are typed tokens exported from a module's public entry

`defineExtensionPoint<TItem, TContext = void>(id)` creates a token; exporting it from the module's public entry is
the frontend `[ModuleContract]`. A module contributing to another module's point imports that token — the dependency
is explicit, compile-checked (the generics type both the contribution and the context the slot supplies), and
correctly directed. Because contributions are data + lazy refs, importing a token does not pull the owning module
into the contributor's chunk. The kernel itself is application-agnostic; typing `when` clauses against the
*generated* vocabulary is the app's one-liner — `createContributionKit<AppVocabulary>()` in its platform folder,
from which modules import `defineModule`/`defineExtensionPoint`/`contribute` (the `createSessionCapabilities`
pattern). Boundaries are enforced the way TS already can: one workspace package (or folder
with lint boundaries) per module, deep imports blocked by `package.json` `exports` — the `ELMOD002` analog.

### 4. `when` clauses evaluate against the capability snapshot — UX, never security

Contribution visibility is a declarative `when: { module?, permission?, flag?, role? }` evaluated against
`SessionCapabilities`, mirroring `[RequirePermission]`/`[FeatureGate]` in shape and vocabulary. Frontend hiding is
a **read-only UX projection**; the handler's own gates remain the enforcement (ADR-0030's rule restated).

### 5. Packaging is sample-first; the reference stack is the documented happy path

The primitives (~150 lines: `defineModule`, `defineExtensionPoint`, the `when` evaluator, an `<ExtensionSlot>`)
are proven in the Billing sample's web app first and promoted to an npm package
(framework-free core + a thin `/react` adapter sub-export — the Abstractions ↔ AspNetCore split) once stable:
the `TransactionDecorator` precedent. The documented happy path is the stack the tutorial already uses — Vite +
React + TanStack (Router for module-owned lazy route subtrees, so a disabled module's chunk never downloads) —
while the core primitives and everything in this ADR up to the slot component stay framework-agnostic.

## Non-goals

- **Micro-frontends / module federation / runtime plugin loading.** The goal is review isolation in one build at
  Elarion's 1–10-node positioning, not independent deployment. A team that truly needs separately deployed
  frontends replaces the composition seam with module federation; the default never grows toward it.
- **UI contributions declared in C#.** Icons, i18n, and components live in TS and cannot ride a C# attribute.
  Backend owns facts (enablement, grants, flags); frontend owns presentation; the snapshot + vocabulary are the
  bridge.
- **A UI kit.** The app shell owns the sidebar's look; the framework ships slot mechanics and vocabulary only.
- **A bespoke router or meta-framework.** Route contribution composes with TanStack Router's generated route tree;
  Elarion adds no routing machinery.

## Consequences

**Positive**

- The review-isolation test extends to the frontend: a new sidebar item touches one file in the owning module;
  a new module is a new folder (glob-discovered — no central edits); a cross-module action is the contributor's
  manifest plus a token import.
- Capability checks are compile-checked against the same vocabulary the backend enforces, ending string drift.
- The vocabulary block is additive and self-gating — hosts without `[ClientFeatures]`/authorization export
  byte-identical schemas; older generated clients ignore the block.

**Negative / accepted**

- The schema now carries declared permission/role names. It is the same information the session endpoint already
  returns per user and the OpenAPI document implies per operation; a host that considers its schema artifact
  sensitive controls its distribution (the schema is a build artifact, not a served document, by default).
- Two more concepts (manifest, extension-point token) for frontend authors to learn; mitigated by the sample and
  by mirroring backend names deliberately.

## Implementation

- **Shipped with this ADR:** the `capabilities` schema block (`JsonRpcSchemaExportOptions`, tool auto-wiring,
  `ClientCapabilityManifest` relocated to `Elarion.Abstractions.Modules`) and the typed TS vocabulary in
  `session-client.ts`.
- **Shipped (sample-first, then promoted):** the contribution primitives were proven in the Billing web app —
  restructured into module folders with TanStack Router: module-owned lazy route subtrees, the shell-owned
  `sidebarItems` point, and a cross-module contribution (Invoicing → the Clients row-action point) whose dialog
  loads as the contributor's own chunk — and then promoted to **`@swimmesberger/elarion-contributions`**
  (`src/elarion-contributions`: framework-free, dependency-free core; React bindings
  `ContributionProvider`/`useContributions`/`<ExtensionSlot>` under the `/react` sub-export with React as an
  optional peer; unit-tested). Point payload shapes stay app-owned — a framework `NavItem` contract was
  considered and rejected (a five-line interface with no drift cost is not framework-worthy); the
  modular-sidebar approach is documented instead. The sample now consumes the package; the app-owned half (kit instantiation,
  points, shell, route composition) deliberately stays sample code to copy.
- **Router integration is one semantic helper, not machinery:** the `/tanstack-router` sub-export ships
  `redirectUnless(when, to)` (+ the vocabulary-bound `createRouteGuards<V>()`), a `beforeLoad` guard that
  evaluates the same `when` clause with the same AND/fail-closed semantics as slot filtering — so a route and
  its sidebar item can never gate differently. `@tanstack/react-router` is an optional peer; everything else
  routing-related remains TanStack's own API composed by the app. A TanStack **Start** integration is
  deliberately deferred until the sample itself runs Start (sample-first).
- **Docs:** the [frontend-modules concept doc](../concepts/frontend-modules.mdx) documents the model and the
  import-vs-own boundary.
