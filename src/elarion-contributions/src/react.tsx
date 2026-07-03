// React bindings over the contribution kernel — the thin `/react` adapter of ADR-0032 (the same split as
// Elarion.Abstractions ↔ Elarion.AspNetCore): one provider, one hook, one slot component. Everything
// interesting happens in the framework-free core; this file only surfaces the resolved registry through
// React context, so porting it to another view framework is a page of code, not a redesign.
import { createContext, Fragment, useContext, type ReactNode } from "react"
import type { Contribution, ContributionRegistry, ExtensionPoint } from "./index.js"

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

/** Renders a point's contributions through a render prop — sugar over {@link useContributions} for inline slots. */
export function ExtensionSlot<TItem, TContext>({
  point,
  render,
}: {
  point: ExtensionPoint<TItem, TContext>
  render: (item: Contribution<TItem>) => ReactNode
}) {
  const items = useContributions(point)
  return (
    <>
      {items.map((item) => (
        <Fragment key={item.id}>{render(item)}</Fragment>
      ))}
    </>
  )
}
