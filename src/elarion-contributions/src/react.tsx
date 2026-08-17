// React bindings over the contribution kernel — the thin `/react` adapter of ADR-0032 (the same split as
// Elarion.Abstractions ↔ Elarion.AspNetCore): one provider, one hook, one slot component. Everything
// interesting happens in the framework-free core; this file only surfaces the resolved registry through
// React context, so porting it to another view framework is a page of code, not a redesign.
import {createContext, Fragment, useCallback, useContext, useSyncExternalStore, type ReactNode} from "react"
import type {
  Contribution,
  ContributionRegistry,
  ContributionRegistryStore,
  ExtensionPoint,
} from "./index.js"

const RegistryContext = createContext<ContributionRegistry | null>(null)

const NO_OP = () => {}

function isStore(
  source: ContributionRegistry | ContributionRegistryStore
): source is ContributionRegistryStore {
  return typeof (source as ContributionRegistryStore).subscribe === "function"
}

/**
 * Publishes the resolved registry to the tree. Accepts either a plain {@link ContributionRegistry} (a static
 * snapshot, resolved once) or a {@link ContributionRegistryStore} — the store form subscribes, so slots
 * re-render when the capability snapshot changes (login, tenant switch, a post-mutation refresh).
 *
 * The context always carries a plain registry, so `useContributions` and `ExtensionSlot` are unaware of the
 * difference: reactivity is confined to this component.
 */
export function ContributionProvider({
                                       registry,
                                       children,
                                     }: {
  registry: ContributionRegistry | ContributionRegistryStore
  children: ReactNode
}) {
  // Both callbacks are declared unconditionally (hooks rules) and branch inside; a plain registry subscribes
  // to nothing and reports itself, which useSyncExternalStore treats as a value that never changes.
  const subscribe = useCallback(
    (onStoreChange: () => void) => (isStore(registry) ? registry.subscribe(onStoreChange) : NO_OP),
    [registry]
  )
  // The store caches `current` between changes, so this is referentially stable — the invariant
  // useSyncExternalStore requires to avoid re-rendering forever.
  const getSnapshot = useCallback(
    () => (isStore(registry) ? registry.current : registry),
    [registry]
  )
  const resolved = useSyncExternalStore(subscribe, getSnapshot, getSnapshot)
  return <RegistryContext.Provider value={resolved}>{children}</RegistryContext.Provider>
}

/** The resolved contributions for a point — already filtered by `when` and deterministically ordered. */
export function useContributions<TItem, TContext>(
  point: ExtensionPoint<TItem, TContext>
): ReadonlyArray<Contribution<TItem>> {
  const registry = useContext(RegistryContext)
  if (registry === null) throw new Error("useContributions requires a <ContributionProvider> above it.")
  return registry.get(point)
}

/**
 * Renders a point's contributions through a render prop — sugar over {@link useContributions} for inline
 * slots. When the point declares a slot context (`TContext`), pass it as `context` and the render prop
 * receives it, typed, as its second argument — so what the slot site supplies can never drift from what
 * the point declares:
 *
 * ```tsx
 * <ExtensionSlot point={stackDetailTabs} context={{ stack }} render={(tab, ctx) => tab.component(ctx)} />
 * ```
 *
 * Without `context`, the render prop takes only the item — for slots that render inert parts (buttons,
 * menu entries) and hand the payload its context later, at invocation time.
 */
export function ExtensionSlot<TItem, TContext>(props: {
  point: ExtensionPoint<TItem, TContext>
  /** The slot context the point declares — handed to `render` as the second argument. */
  context: TContext
  render: (item: Contribution<TItem>, context: TContext) => ReactNode
}): ReactNode
export function ExtensionSlot<TItem, TContext>(props: {
  point: ExtensionPoint<TItem, TContext>
  render: (item: Contribution<TItem>) => ReactNode
}): ReactNode
// Overloads rather than a props union: JSX contextually types the render prop per overload, where a
// union would leave the render parameters implicitly `any`. The context form must come first — JSX
// falls through cleanly on its *missing* `context` prop, but would not fall through past an *excess* one.
export function ExtensionSlot<TItem, TContext>(props: {
  point: ExtensionPoint<TItem, TContext>
  context?: TContext
  render: (item: Contribution<TItem>, context?: TContext) => ReactNode
}): ReactNode {
  const items = useContributions(props.point)
  return (
    <>
      {items.map((item) => (
        <Fragment key={item.id}>{props.render(item, props.context)}</Fragment>
      ))}
    </>
  )
}
