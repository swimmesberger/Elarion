// React bindings over the contribution kernel — the thin `/react` adapter of ADR-0032 (the same split as
// Elarion.Abstractions ↔ Elarion.AspNetCore): one provider, one hook, one slot component. Everything
// interesting happens in the framework-free core; this file only surfaces the resolved registry through
// React context, so porting it to another view framework is a page of code, not a redesign.
import {createContext, Fragment, useContext, type ReactNode} from "react"
import type {Contribution, ContributionRegistry, ExtensionPoint} from "./index.js"

const RegistryContext = createContext<ContributionRegistry | null>(null)

export function ContributionProvider({
                                       registry,
                                       children,
                                     }: {
  registry: ContributionRegistry
  children: ReactNode
}) {
  return <RegistryContext.Provider value={registry}>{children}</RegistryContext.Provider>
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
