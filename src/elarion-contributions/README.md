# @swimmesberger/elarion-contributions

The [Elarion](https://github.com/swimmesberger/Elarion) **frontend contribution model**
([ADR-0032](https://github.com/swimmesberger/Elarion/blob/main/docs/decisions/0032-frontend-contribution-model.md)):
typed extension-point tokens, declarative module manifests, and capability-gated, deterministic resolution.
It extends the backend's review-isolation rule — *a module only touches its own code* — to the frontend:
adding a sidebar item or a cross-module action edits one file in the owning module, never the shell.

- **Framework-free core** (`@swimmesberger/elarion-contributions`) — no dependencies, no React.
- **React bindings** (`@swimmesberger/elarion-contributions/react`) — `ContributionProvider`,
  `useContributions`, `<ExtensionSlot>`. React is an optional peer dependency; other view frameworks
  can port the bindings in a page of code.
- **TanStack Router guard** (`@swimmesberger/elarion-contributions/tanstack-router`) —
  `redirectUnless(when, to)` + the vocabulary-bound `createRouteGuards<V>()`: a `beforeLoad` guard
  evaluating the same `when` clause as a contribution, so a route and its nav entry can never gate
  differently. `@tanstack/react-router` is an optional peer; everything else routing-related stays
  the router's own API.

```bash
npm install @swimmesberger/elarion-contributions
```

## What you import vs. what you own

This package ships the *machinery* with fixed semantics; your application owns the *points* and the shell
(Elarion deliberately ships no UI kit and no router integration):

| You import (fixed semantics) | You own (copy from the sample) |
| --- | --- |
| `defineExtensionPoint`, `defineModule`, `contribute` | Your extension points (`sidebarItems`, …) and their payload types |
| `evaluateWhen` + the `when` AND semantics | The kit instantiation binding your generated vocabulary |
| `createContributionRegistry` (filter + deterministic order) | The app shell that renders each slot |
| `ContributionProvider` / `useContributions` / `<ExtensionSlot>` | Route composition and module discovery (e.g. `import.meta.glob`) |
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

## Semantics (the part that must not drift)

- **`when` is AND** across its fields (`module`, `permission`, `flag`, `role`), mirroring
  `[RequirePermission]`/`[RequireRole]`/`[FeatureGate]`; a manifest-level `when` is ANDed into every
  contribution. Absent capabilities fail closed.
- **Resolution is pure and deterministic**: contributions sort by `order` (default 0), then `id`, then
  contributing module name, using code-unit comparison — the same input renders the same UI on server
  and client. Refreshing the snapshot means building a new registry.
- **UX projection, never security.** A hidden contribution is not a secured operation — the backend
  handler's own gates are the enforcement on every call.

## Reference usage

The [Billing sample](https://github.com/swimmesberger/Elarion/tree/main/samples/Billing/web) is the
living reference: module folders, a shell-owned sidebar point, a cross-module row-action point with a
lazily-chunked contributed dialog, and TanStack Router route subtrees composed per module.
