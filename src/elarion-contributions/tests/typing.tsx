// The compile-time contract of the vocabulary typing (the issue #71 guarantees): checked by
// `npm run typecheck` (and `npm test`), never executed. A regression shows up either as a real type
// error or as an unused `@ts-expect-error` (TS2578) — both fail the build. Assertions that must ERROR
// keep the offending expression on a single line so the directive covers it.
import type { ReactNode } from "react"
import {
  createContributionKit,
  createContributionRegistry,
  createStaticCapabilities,
  type ContextOf,
  type Contribution,
  type ExtensionPoint,
  type ItemOf,
  type ModuleManifest,
  type WhenClause,
} from "../src/index.js"
import { ExtensionSlot } from "../src/react.js"
import { createRouteGuards } from "../src/tanstack-router.js"

type Expect<T extends true> = T
type Equal<A, B> = (<T>() => T extends A ? 1 : 2) extends <T>() => T extends B ? 1 : 2 ? true : false

// ─── A partial, module-only vocabulary (the no-auth shape): omitted axes reject every value ─────────
interface ModuleOnlyVocabulary {
  module: "core" | "ai-agent"
}

const kit = createContributionKit<ModuleOnlyVocabulary>()
interface SidebarItem {
  readonly label: string
}
const sidebarItems = kit.defineExtensionPoint<SidebarItem>("platform.sidebar")

export const validModuleOnly = kit.defineModule({
  name: "core",
  when: { module: "core" },
  contributes: [kit.contribute(sidebarItems, [{ id: "core.home", label: "Home" }])],
})

// A typo'd module name is a compile error — not a silently hidden item.
// @ts-expect-error out-of-vocabulary module name
export const moduleTypo: WhenClause<ModuleOnlyVocabulary> = { module: "Typo-Module" }

// An axis the vocabulary does not declare rejects every value.
// @ts-expect-error the vocabulary declares no permission axis
export const strayPermission: WhenClause<ModuleOnlyVocabulary> = { permission: "anything" }
// @ts-expect-error the vocabulary declares no flag axis
export const strayFlag: WhenClause<ModuleOnlyVocabulary> = { flag: "anything" }

// `never`-declared axes (the pre-partial-vocabulary spelling) behave the same.
interface NeverAxesVocabulary {
  module: "Stacks"
  permission: never
  flag: never
  role: never
}
// @ts-expect-error a `never` axis rejects every value
export const neverAxis: WhenClause<NeverAxesVocabulary> = { permission: "anything" }

// UI-only module names stay expressible: `name` is an identity, not a capability lookup.
export const uiOnly = kit.defineModule({ name: "ui-only-module", contributes: [] })

// ─── A full generated-style vocabulary: every axis strictly checked ──────────────────────────────────
interface FullVocabulary {
  module: "Clients" | "Invoicing"
  permission: "clients.read" | "invoices.read" | "invoices.write"
  flag: "beta"
  role: "admin"
}

export const fullClause: WhenClause<FullVocabulary> = {
  module: "Invoicing",
  permission: "invoices.read",
  flag: "beta",
  role: "admin",
}
// @ts-expect-error a typo'd permission is a compile error (the documented guarantee)
export const permissionTypo: WhenClause<FullVocabulary> = { permission: "invocies.read" }

// ─── The unbound default stays permissive (plain strings) for kit-less usage ─────────────────────────
export const unbound: WhenClause = { module: "anything", permission: "any", flag: "x", role: "y" }

// ─── Vocabulary-bound manifests feed the registry without casts ──────────────────────────────────────
const manifests: ReadonlyArray<ModuleManifest<ModuleOnlyVocabulary>> = [validModuleOnly]
export const registry = createContributionRegistry(manifests, createStaticCapabilities())

// ─── ItemOf / ContextOf extract the point's declared types ───────────────────────────────────────────
interface Stack {
  readonly id: number
}
interface StackTabContext {
  readonly stack: Stack
}
interface StackTab {
  readonly label: string
  readonly component: (context: ContextOf<typeof stackDetailTabs>) => ReactNode
}
const stackDetailTabs = kit.defineExtensionPoint<StackTab, StackTabContext>("stacks.detailTabs")

export type ItemExtracts = Expect<Equal<ItemOf<typeof stackDetailTabs>, StackTab>>
export type ContextExtracts = Expect<Equal<ContextOf<typeof stackDetailTabs>, StackTabContext>>
export type VoidContextExtracts = Expect<Equal<ContextOf<typeof sidebarItems>, void>>

// ─── ExtensionSlot: the context-free form and the typed slot-context form ────────────────────────────
declare const stack: Stack

// Context-free (void point): the render prop takes only the item — unchanged from before.
export const slotVoid = <ExtensionSlot point={sidebarItems} render={(item) => item.label} />

// Context-free on a context-declaring point stays legal: inert rendering, context handed at invocation.
export const slotInert = <ExtensionSlot point={stackDetailTabs} render={(tab) => tab.label} />

// The typed form: `context` is checked against the point's declaration and reaches `render`.
export const slotWithContext = (
  <ExtensionSlot
    point={stackDetailTabs}
    context={{ stack }}
    render={(tab, context) => tab.component(context)}
  />
)

// Asking for the context without supplying it is a compile error.
// @ts-expect-error a two-argument render requires the context prop
export const slotMissingContext = <ExtensionSlot point={stackDetailTabs} render={(tab, context) => tab.component(context)} />

// Supplying a context the point did not declare is a compile error.
// @ts-expect-error the context shape must match the point's declaration
export const slotWrongContext = <ExtensionSlot point={stackDetailTabs} context={{ wrong: true }} render={(tab, context) => tab.component(context)} />

// ─── Route guards share the same strict clause typing ────────────────────────────────────────────────
const { redirectUnless } = createRouteGuards<ModuleOnlyVocabulary>()
export const guard = redirectUnless({ module: "ai-agent" }, "/")
// @ts-expect-error a typo'd module name in a route guard is a compile error too
export const guardTypo = redirectUnless({ module: "ai-agnet" }, "/")

// The contribution type stays assignable across vocabularies (bound → base) for interop helpers.
export const widened: Contribution<SidebarItem> = { id: "core.home", label: "Home" }
export const pointWidens: ExtensionPoint<SidebarItem, unknown> = sidebarItems
