# @swimmesberger/elarion-contributions

The [Elarion](https://github.com/swimmesberger/Elarion) **frontend contribution model**
([ADR-0032](https://github.com/swimmesberger/Elarion/blob/main/docs/decisions/0032-frontend-contribution-model.md)):
typed extension-point tokens, declarative module manifests, and capability-gated, deterministic resolution.
It extends the backend's review-isolation rule — *a module only touches its own code* — to the frontend:
adding a sidebar item or a cross-module action edits one file in the owning module, never the shell.

- **Framework-free core** (`@swimmesberger/elarion-contributions`) — no dependencies, no React. `when`
  clauses are typed **strictly** against your vocabulary (a typo'd permission is a compile error, and
  vocabulary axes are optional — a no-auth app binds `{ module }` only and any stray axis use fails to
  compile), `ItemOf`/`ContextOf` extract a point's declared types, and `createStaticCapabilities` is
  the `CapabilityReader` for apps without a session snapshot yet.
- **React bindings** (`@swimmesberger/elarion-contributions/react`) — `ContributionProvider`,
  `useContributions`, `<ExtensionSlot>` (its `context` prop is type-checked against the point's
  `TContext` and handed to the render prop). React is an optional peer dependency; other view frameworks
  can port the bindings in a page of code.
- **Angular bindings** (`@swimmesberger/elarion-contributions/angular`) — `provideContributions`,
  `injectContributions` (returns a `Signal`). Idiomatic for Angular 20–22: signal-first, standalone,
  no NgModule, no shipped directive — you render with `@for`. `@angular/core` is an optional peer
  dependency, and the bindings are decorator/template-free so they ship in this one package (no
  ng-packagr).
- **TanStack Router guard** (`@swimmesberger/elarion-contributions/tanstack-router`) —
  `redirectUnless(when, to)` + the vocabulary-bound `createRouteGuards<V>()`: a `beforeLoad` guard
  evaluating the same `when` clause as a contribution, so a route and its nav entry can never gate
  differently. `@tanstack/react-router` is an optional peer; everything else routing-related stays
  the router's own API.

```bash
npm install @swimmesberger/elarion-contributions
```

> [!IMPORTANT]
> **Vite users:** the `/react` bindings call hooks, so they must resolve to your app's **single** React
> instance. If the first `useContributions` throws `Invalid hook call … Cannot read properties of null
> (reading 'useContext')`, Vite's dependency optimizer pre-bundled the package against a second React
> copy. Add `resolve: { dedupe: ["react", "react-dom"] }` plus an `optimizeDeps.include` entry for the
> package (and its `/react` sub-export) to `vite.config.ts`, then clear `node_modules/.vite`. Vite does
> not consult peer-dependency metadata when pre-bundling, so there is no package-side fix.

## What you import vs. what you own

This package ships the *machinery* with fixed semantics; your application owns the *points* and the shell
(Elarion deliberately ships no UI kit and no router integration):

| You import (fixed semantics) | You own (copy from the sample) |
| --- | --- |
| `defineExtensionPoint`, `defineModule`, `contribute`, `ItemOf`/`ContextOf` | Your extension points (`sidebarItems`, …) and their payload types |
| `evaluateWhen` + the strict `when` AND semantics | The kit instantiation binding your (generated or hand-authored) vocabulary |
| `createContributionRegistry` (filter + deterministic order + id validation) | The app shell that renders each slot |
| `createStaticCapabilities` (the no-snapshot `CapabilityReader`) | The real snapshot wiring once `elarion.session` exists |
| `ContributionProvider` / `useContributions` / `<ExtensionSlot context=…>` (React) | Route composition and module discovery (e.g. `import.meta.glob`) |
| `provideContributions` / `injectContributions` (Angular) | Slot rendering — an `@for` block, or a small `*extensionSlot` directive you own (see below) |
| `redirectUnless` route guard (`/tanstack-router`) | Everything else routing — the router's own API |

## Quick start

Bind the kernel once to your generated capability vocabulary (from the Elarion client generator's
`session-client.ts`), so `when` clauses are compile-checked against the catalog the backend enforces:

```ts
// platform/contributions.ts — the app's one kit
import { createContributionKit, type ModuleManifest } from "@swimmesberger/elarion-contributions"
import type { FlagName, ModuleName, PermissionName, RoleName } from "./generated/session-client"

export interface AppVocabulary {
  module: ModuleName
  permission: PermissionName
  flag: FlagName
  role: RoleName
}
export type AppManifest = ModuleManifest<AppVocabulary>
export const { defineModule, defineExtensionPoint, contribute } = createContributionKit<AppVocabulary>()
```

Declare only the axes your application has — they are all optional, and `when` clauses are checked
**strictly**: a typo'd name or a clause on an undeclared axis is a compile error, not a silently hidden
item. No auth and no generated `session-client.ts` yet? Bind `{ module: ModuleName }` with a hand-authored
union and use `createStaticCapabilities()` as the reader (modules/permissions/roles default `"all"`, flags
default none); swap in the generated `SessionCapabilities` later — same structural interface.

Declare a point where the *owner* of the slot lives (the shell for a sidebar, a module for its own
extension surface), and export the token from the owner's public entry. The payload shape is yours —
whatever your shell needs to render:

```ts
// platform/points.ts
export interface SidebarItem { readonly label: string; readonly icon: LucideIcon; readonly to: string }
export const sidebarItems = defineExtensionPoint<SidebarItem>("platform.sidebar")
```

Modules contribute through their manifest — plain data plus lazy component references, never import-time
registration:

```ts
// modules/invoicing/module.tsx
export const invoicingManifest = defineModule({
  name: Modules.Invoicing,
  when: { module: Modules.Invoicing },   // module-level gate, ANDed into every contribution
  contributes: [
    contribute(sidebarItems, [{
      id: "invoices", label: "Invoices", to: "/invoices", order: 20,
      when: { permission: Permissions.invoices.read },
    }]),
  ],
})
```

The composition root resolves once per capability snapshot and hands the registry to React:

```tsx
import { createContributionRegistry } from "@swimmesberger/elarion-contributions"
import { ContributionProvider, useContributions } from "@swimmesberger/elarion-contributions/react"

const registry = createContributionRegistry(appModules.map((m) => m.manifest), capabilities)
// <ContributionProvider registry={registry}> … </ContributionProvider>

// anywhere in the shell:
const items = useContributions(sidebarItems)
```

When a point declares a slot context (`defineExtensionPoint<TItem, TContext>`), the slot site supplies it
through `<ExtensionSlot context=…>` — type-checked against `TContext` and handed to the render prop — and
payloads pin their signatures to the declaration with `ContextOf`:

```tsx
// the point: export const stackDetailTabs = defineExtensionPoint<StackTab, { stack: Stack }>("stacks.detailTabs")
// the payload type: component: (context: ContextOf<typeof stackDetailTabs>) => ReactNode
<ExtensionSlot point={stackDetailTabs} context={{ stack }} render={(tab, ctx) => tab.component(ctx)} />
```

## Angular

Same kernel, idiomatic Angular surface. `provideContributions` is an environment provider (shaped like
`provideRouter`); `injectContributions` returns a `Signal` your templates track, so refreshing the
capability snapshot re-resolves every slot by setting one signal:

```ts
// main.ts — hand the resolved registry to the injector tree
import { createContributionRegistry } from "@swimmesberger/elarion-contributions"
import { provideContributions } from "@swimmesberger/elarion-contributions/angular"

const registry = createContributionRegistry(appModules.map((m) => m.manifest), capabilities)
bootstrapApplication(App, { providers: [provideContributions(registry)] })
// snapshot can change at runtime? provideContributions(signal(registry)) and .set(...) on login.
```

```ts
// any standalone component — read the point as a signal, render with @for
import { Component } from "@angular/core"
import { injectContributions } from "@swimmesberger/elarion-contributions/angular"
import { sidebarItems } from "../platform/points"

@Component({
  standalone: true,
  imports: [RouterLink],
  template: `@for (item of items(); track item.id) {
    <a [routerLink]="item.to">{{ item.label }}</a>
  }`,
})
export class Sidebar {
  readonly items = injectContributions(sidebarItems)
}
```

### Want a `*extensionSlot` directive? Build it on top

The package deliberately ships no directive (that would need the Angular compiler / ng-packagr and a second
build). If you prefer slot sugar over an inline `@for`, a structural directive is a few lines in **your**
app over `injectContributions` — you own the rendering, the kernel stays framework-agnostic:

```ts
// app/extension-slot.directive.ts
import {
  Directive, effect, inject, Injector, input, runInInjectionContext, TemplateRef, ViewContainerRef,
} from "@angular/core"
import { injectContributions } from "@swimmesberger/elarion-contributions/angular"
import type { Contribution, ExtensionPoint } from "@swimmesberger/elarion-contributions"

@Directive({ selector: "[extensionSlot]", standalone: true })
export class ExtensionSlotDirective<TItem> {
  // *extensionSlot="point" — the point token to render.
  readonly point = input.required<ExtensionPoint<TItem, unknown>>({ alias: "extensionSlot" })

  private readonly tpl = inject<TemplateRef<{ $implicit: Contribution<TItem> }>>(TemplateRef)
  private readonly vcr = inject(ViewContainerRef)
  private readonly injector = inject(Injector)

  constructor() {
    // Re-render when the point changes or the snapshot refreshes; each item is the template's $implicit.
    // injectContributions() calls inject(), so it must run in an injection context — hence runInInjectionContext.
    effect(() => {
      const items = runInInjectionContext(this.injector, () => injectContributions(this.point())())
      this.vcr.clear()
      for (const item of items) this.vcr.createEmbeddedView(this.tpl, { $implicit: item })
    })
  }
}
```

```html
<!-- usage: item is the contribution, typed by the point -->
<a *extensionSlot="sidebarItems; let item" [routerLink]="item.to">{{ item.label }}</a>
```

## Semantics (the part that must not drift)

- **`when` is AND** across its fields (`module`, `permission`, `flag`, `role`), mirroring
  `[RequirePermission]`/`[RequireRole]`/`[FeatureGate]`; a manifest-level `when` is ANDed into every
  contribution. Absent capabilities fail closed.
- **`when` is strictly typed** against the kit's vocabulary: out-of-vocabulary names do not compile
  (fail-closed evaluation would otherwise turn a typo into invisibly hidden UI). Only a manifest's
  `name` accepts free strings — it is an identity, not a lookup.
- **Resolution is pure and deterministic**: contributions sort by `order` (default 0), then `id`, using
  code-unit comparison — the same input renders the same UI on server and client. Two co-visible
  contributions to one point sharing an id throw at resolution (ids double as render keys); prefix ids
  with the module name (`"invoicing.create-invoice"`). Refreshing the snapshot means building a new
  registry.
- **UX projection, never security.** A hidden contribution is not a secured operation — the backend
  handler's own gates are the enforcement on every call.

## Reference usage

The [Billing sample](https://github.com/swimmesberger/Elarion/tree/main/samples/Billing/web) is the
living reference: module folders, a shell-owned sidebar point, a cross-module row-action point with a
lazily-chunked contributed dialog, and TanStack Router route subtrees composed per module.
