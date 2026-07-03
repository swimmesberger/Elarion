// TanStack Router adapter — deliberately a single semantic helper, not routing machinery (an ADR-0032
// non-goal): routes stay module-owned TanStack subtrees composed with the router's own API. What the
// framework owns is the `when` clause, and a route guard must evaluate it with exactly the slot-filter
// semantics (AND, fail-closed) — this sub-export exists so that never drifts. The guard takes the same
// `when` object a sidebar item declares, so "this page and its nav entry gate identically" is one literal.
import { redirect } from "@tanstack/react-router"
import { evaluateWhen, type CapabilityReader, type Vocabulary, type WhenClause } from "./index.js"

/** The router-context shape the guard reads — the app's router context carries the capability snapshot. */
export interface GuardContext {
  readonly context: { readonly caps: CapabilityReader }
}

/**
 * A `beforeLoad` guard: redirects to `to` unless the `when` clause holds against the capability snapshot
 * in the router context — the route-level mirror of a contribution's `when`, so a deep link into a hidden
 * module bounces instead of rendering. UX only: the handlers behind the route still enforce their own
 * `[RequirePermission]`/`[FeatureGate]` on every call.
 *
 * @example
 * ```ts
 * createRoute({ path: "/invoices", beforeLoad: redirectUnless({ module: "Invoicing" }, "/"), … })
 * ```
 */
export function redirectUnless<V extends Vocabulary = Vocabulary>(
  when: WhenClause<V>,
  to: string
): (opts: GuardContext) => void {
  return ({ context }) => {
    if (!evaluateWhen(when, context.caps)) {
      throw redirect({ to })
    }
  }
}

/** The vocabulary-bound guard factory — the router-side sibling of `createContributionKit`. */
export interface RouteGuards<V extends Vocabulary> {
  redirectUnless(when: WhenClause<V>, to: string): (opts: GuardContext) => void
}

/**
 * Binds the route guards to an application's capability vocabulary, so a route's `when` clause is
 * compile-checked against the same generated literal unions as a manifest's. Purely a typing layer.
 */
export function createRouteGuards<V extends Vocabulary>(): RouteGuards<V> {
  return { redirectUnless }
}
